namespace MemoryVisualizer.Worker

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

type private Pending = {
    Request: Request
    Cancellation: CancellationTokenSource
    Completion: TaskCompletionSource<unit>
    mutable TerminalChosen: bool
}

[<RequireQualifiedAccess>]
module Worker =
    let private items = [|
        {|
            objectId = "1"
            address = "0x0000000000001000"
            size = "24"
        |}
        {|
            objectId = "2"
            address = "0x0000000000001018"
            size = "32"
        |}
        {|
            objectId = "18446744073709551615"
            address = "0xffffffffffffffff"
            size = "18446744073709551615"
        |}
    |]

    let private numberedSnapshot (number: uint64) =
        let hex = number.ToString("x32")
        $"{hex[0..7]}-{hex[8..11]}-{hex[12..15]}-{hex[16..19]}-{hex[20..31]}"

    let runAsync (input: Stream) (output: Stream) (diagnostics: TextWriter) =
        task {
            use connection = new CancellationTokenSource()
            let reader = FrameReader(input)
            let writer = FrameWriter(output, TimeSpan.FromSeconds(2.0))
            let gate = obj ()
            let pending = Dictionary<uint64, Pending>()
            let mutable snapshot = None
            let mutable nextSnapshot = 0UL
            let mutable lastRequest = 0UL

            let failure =
                TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

            let fail code =
                if failure.TrySetResult(code) then
                    try
                        connection.Cancel()
                    with :? ObjectDisposedException ->
                        ()

            let cancelWhere predicate =
                for work in pending.Values do
                    if not work.TerminalChosen && predicate work.Request then
                        work.Cancellation.Cancel()

            let page request source size cursor =
                let offset = defaultArg cursor 0UL

                if offset > uint64 (Array.length source) then
                    Protocol.error request "InvalidRequest"
                else
                    let selected = source |> Array.skip (int offset) |> Array.truncate size
                    let following = int offset + selected.Length
                    let truncated = following < source.Length
                    let next = if truncated then string following else null

                    Protocol.success request request.Snapshot {|
                        tag = "page"
                        items = selected
                        nextCursor = next
                        truncated = truncated
                    |}

            let result request =
                match request.Operation with
                | Capabilities ->
                    Protocol.success request None {|
                        tag = "capabilities"
                        value = Protocol.capabilities
                    |}
                | Load _ ->
                    nextSnapshot <- nextSnapshot + 1UL
                    snapshot <- Some(numberedSnapshot nextSnapshot)

                    Protocol.success request snapshot {|
                        tag = "snapshot"
                        objectCount = "3"
                    |}
                | Dispose -> Protocol.success request request.Snapshot {| tag = "disposed" |}
                | Query(size, cursor) -> page request items size cursor
                | Details(objectId, size, cursor) ->
                    let selected =
                        items |> Array.filter (fun item -> item.objectId = Protocol.idValue objectId)

                    page request selected size cursor
                | Scene maximum ->
                    Protocol.success request request.Snapshot {|
                        tag = "scene"
                        schemaVersion = 1
                        items = Array.truncate maximum items
                        truncated = items.Length > maximum
                    |}
                | ValidateRecipe ->
                    Protocol.success request None {|
                        tag = "recipe"
                        schemaVersion = 1
                        valid = true
                    |}
                | Export ->
                    Protocol.success request request.Snapshot {|
                        tag = "export"
                        format = "svg"
                        artifactId = "fixture:svg"
                        byteLength = "0"
                    |}

            let execute work =
                task {
                    use requestDeadline =
                        new Timer((fun _ -> fail "TransportTimeout"), null, 10000, Timeout.Infinite)

                    try
                        try
                            let! terminal =
                                task {
                                    try
                                        match work.Request.Operation with
                                        | Load delay ->
                                            let elapsed = Stopwatch.StartNew()
                                            let mutable lastProgress = 0L

                                            while elapsed.ElapsedMilliseconds < int64 delay do
                                                let remaining = max 0L (int64 delay - elapsed.ElapsedMilliseconds)
                                                do! Task.Delay(int (min 100L remaining), work.Cancellation.Token)

                                                if elapsed.ElapsedMilliseconds - lastProgress >= 100L then
                                                    let completed =
                                                        min 100 (int (elapsed.ElapsedMilliseconds * 100L / int64 delay))
                                                    // The producer holds only the newest progress value, never a queue of updates.
                                                    do!
                                                        writer.WriteAsync(
                                                            Protocol.progress work.Request completed,
                                                            connection.Token
                                                        )

                                                    lastProgress <- elapsed.ElapsedMilliseconds
                                        | Query _
                                        | Scene _ -> do! Task.Delay(250, work.Cancellation.Token)
                                        | _ -> do! Task.Delay(10, work.Cancellation.Token)

                                        return
                                            lock gate (fun () ->
                                                work.TerminalChosen <- true

                                                if work.Cancellation.IsCancellationRequested then
                                                    Protocol.error work.Request "Cancelled"
                                                else
                                                    result work.Request)
                                    with :? OperationCanceledException when not connection.IsCancellationRequested ->
                                        return
                                            lock gate (fun () ->
                                                work.TerminalChosen <- true
                                                Protocol.error work.Request "Cancelled")
                                }

                            do! writer.WriteAsync(terminal, connection.Token)
                        with
                        | :? OperationCanceledException when connection.IsCancellationRequested -> ()
                        | ProtocolFailure code -> fail code
                        | :? IOException -> fail "InternalError"
                        | _ -> fail "InternalError"
                    finally
                        lock gate (fun () ->
                            pending.Remove(work.Request.Id) |> ignore
                            work.Cancellation.Dispose())

                        work.Completion.TrySetResult(()) |> ignore
                }

            let accept request =
                task {
                    let action =
                        lock gate (fun () ->
                            if request.Id <= lastRequest then
                                raise (ProtocolFailure "ProtocolViolation")

                            lastRequest <- request.Id

                            if pending.Count >= Protocol.MaxOutstanding then
                                Choice1Of2(Protocol.error request "Busy")
                            elif request.Snapshot.IsSome && request.Snapshot <> snapshot then
                                Choice1Of2(Protocol.error request "SnapshotNotFound")
                            else
                                match request.Operation with
                                | Load _ ->
                                    snapshot <- None

                                    cancelWhere (fun prior ->
                                        prior.Snapshot.IsSome
                                        || match prior.Operation with
                                           | Load _ -> true
                                           | _ -> false)
                                | Dispose ->
                                    snapshot <- None
                                    cancelWhere (fun prior -> prior.Snapshot = request.Snapshot)
                                | _ -> ()

                                let work = {
                                    Request = request
                                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(connection.Token)
                                    Completion =
                                        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                                    TerminalChosen = false
                                }

                                pending.Add(request.Id, work)
                                Choice2Of2 work)

                    match action with
                    | Choice1Of2 bytes -> do! writer.WriteAsync(bytes, connection.Token)
                    | Choice2Of2 work -> execute work |> ignore
                }

            let finishPending () =
                lock gate (fun () ->
                    snapshot <- None
                    cancelWhere (fun _ -> true)

                    pending.Values
                    |> Seq.map (fun work -> work.Completion.Task :> Task)
                    |> Seq.toArray)
                |> Task.WhenAll

            let mutable graceful = false

            try
                let! first = reader.ReadAsync(connection.Token).WaitAsync(TimeSpan.FromSeconds(5.0))

                match first with
                | None -> ()
                | Some bytes ->
                    match Protocol.parseInbound bytes with
                    | Hello(versions, extensions) ->
                        if not (Array.contains 1L versions) then
                            raise (ProtocolFailure "UnsupportedVersion")

                        if extensions.Length <> 0 then
                            raise (ProtocolFailure "UnsupportedExtension")
                    | _ -> raise (ProtocolFailure "ProtocolViolation")

                    do! writer.WriteAsync(Protocol.ready (), connection.Token)
                    let mutable reading = true

                    while reading do
                        let! frame = reader.ReadAsync(connection.Token)

                        match frame with
                        | None -> reading <- false
                        | Some frame ->
                            match Protocol.parseInbound frame with
                            | Hello _ -> raise (ProtocolFailure "ProtocolViolation")
                            | Request request -> do! accept request
                            | Cancel id ->
                                lock gate (fun () ->
                                    match pending.TryGetValue(id) with
                                    | true, work when not work.TerminalChosen -> work.Cancellation.Cancel()
                                    | _ -> ())
                            | Shutdown ->
                                graceful <- true
                                reading <- false

                    if graceful then
                        do! (finishPending ()).WaitAsync(TimeSpan.FromSeconds(2.0))
                        do! writer.WriteAsync(Protocol.bye (), connection.Token)
            with
            | ProtocolFailure code -> fail code
            | :? TimeoutException -> fail "TransportTimeout"
            | :? OperationCanceledException when failure.Task.IsCompleted -> ()
            | _ -> fail "InternalError"

            connection.Cancel()

            try
                do! (finishPending ()).WaitAsync(TimeSpan.FromMilliseconds(250.0))
            with _ ->
                ()

            if failure.Task.IsCompleted then
                let code = failure.Task.Result
                // Best effort is itself bounded; an uncooperative stream cannot keep the process alive.
                use fatalDeadline = new CancellationTokenSource(200)

                try
                    do! writer.WriteAsync(Protocol.fatal code, fatalDeadline.Token)
                with _ ->
                    ()
                // Diagnostics are fixed strings, and are not allowed to block cleanup either.
                try
                    let writing =
                        Task.Run(Func<Task>(fun () -> diagnostics.WriteLineAsync($"Worker failure: {code}.")))

                    do! writing.WaitAsync(TimeSpan.FromMilliseconds(100.0))
                with _ ->
                    ()

                return 2
            else
                return 0
        }
