namespace MemoryVisualizer.Worker

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis
open MemoryVisualizer.Query
open MemoryVisualizer.Scene

type private NativeSnapshot = {
    Store: IndexedHeapSnapshot
    Info: SnapshotIndexInfo
}

type private NativeQuery = {
    Id: uint64
    Result: QueryResult
    Rows: QueryRow array
    Scene: PositionedScene option
    Elements: SceneElement array
}

type private NativePending = {
    Request: NativeRequest
    Cancellation: CancellationTokenSource
    Completion: TaskCompletionSource<unit>
    Epoch: uint64
    Exclusive: bool
    mutable TerminalChosen: bool
}

[<RequireQualifiedAccess>]
module NativeWorker =
    let private errorCode =
        function
        | AnalysisError.InvalidInput _ -> "InvalidRequest"
        | AnalysisError.FileNotFound _ -> "FileNotFound"
        | AnalysisError.AccessDenied _ -> "AccessDenied"
        | AnalysisError.UnsupportedTarget _ -> "UnsupportedTarget"
        | AnalysisError.InvalidDump _ -> "InvalidDump"
        | AnalysisError.DacNotFound _ -> "DacNotFound"
        | AnalysisError.DacLoadFailed _ -> "DacLoadFailed"
        | AnalysisError.HeapUnavailable _ -> "HeapUnavailable"
        | AnalysisError.ReadFailed _ -> "ReadFailed"
        | _ -> "InternalError"

    let private destination (path: string) =
        if not (Path.IsPathFullyQualified path) then
            raise (NativeFailure "InvalidRequest")

        let full = Path.GetFullPath(path)
        let name = Path.GetFileName(full)
        let stem = (name.Split('.')[0]).ToUpperInvariant()

        if
            String.IsNullOrWhiteSpace name
            || name.EndsWith(' ')
            || name.EndsWith('.')
            || Protocol.utf8.GetByteCount(name) > 255
            || (name |> Seq.exists (fun ch -> ch < ' ' || "<>:\"/\\|?*".Contains ch))
            || List.contains stem [
                "CON"
                "PRN"
                "AUX"
                "NUL"
                "COM1"
                "COM2"
                "COM3"
                "COM4"
                "COM5"
                "COM6"
                "COM7"
                "COM8"
                "COM9"
                "LPT1"
                "LPT2"
                "LPT3"
                "LPT4"
                "LPT5"
                "LPT6"
                "LPT7"
                "LPT8"
                "LPT9"
            ]
            || File.Exists full
            || Directory.Exists full
            || not (Directory.Exists(Path.GetDirectoryName full))
        then
            raise (NativeFailure "InvalidRequest")

        full

    let internal detailEntity
        (item: HeapObject)
        (segment: SnapshotSegmentInfo option)
        (typ: Result<HeapType option, SnapshotIndexError>)
        : SelectedEntity =
        let invariant () =
            raise (NativeFailure "SnapshotInvariant")

        let segment = segment |> Option.defaultWith invariant

        let typ =
            typ
            |> Result.defaultWith (fun _ -> invariant ())
            |> Option.defaultWith invariant

        if
            segment.Runtime <> item.Identity.Runtime
            || segment.Address <> item.SegmentAddress
            || typ.Identity <> item.Type
        then
            invariant ()

        {
            Kind = Selector.Object
            Runtime = item.Identity.Runtime
            Heap = segment.HeapIndex
            SegmentAddress = item.SegmentAddress
            Address = item.Identity.Address
            Size = item.SizeBytes
            End = None
            TypeIdentity = Some item.Type
            TypeName = typ.Name
            Generation = item.Generation
            IsFree = Some item.IsFree
            HeapKind = segment.Kind
        }

    let internal runWithSceneClockAsync
        (sceneClock: TimeProvider)
        (readSnapshot:
            string
                -> SnapshotReaderOptions
                -> IProgress<SnapshotProgress>
                -> CancellationToken
                -> Task<Result<HeapSnapshot, AnalysisError>>)
        (input: Stream)
        (output: Stream)
        (diagnostics: TextWriter)
        =
        task {
            use connection = new CancellationTokenSource()
            let reader = FrameReader(input)

            let writer =
                FrameWriter(output, TimeSpan.FromSeconds(2.0), NativeProtocol.validateOutbound)

            let gate = obj ()
            let pending = Dictionary<uint64, NativePending>()
            let teardown = Dictionary<uint64, NativePending>()
            let mutable snapshot: NativeSnapshot option = None
            let mutable query: NativeQuery option = None
            let mutable epoch = 0UL
            let mutable lastRequest = 0UL
            let mutable nextQuery = 0UL
            let mutable exclusive = false

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

            let invalidate clearQuery =
                epoch <- epoch + 1UL

                if clearQuery then
                    query <- None

                cancelWhere (fun _ -> true)

            let current work =
                work.Epoch = epoch && not work.Cancellation.IsCancellationRequested

            let check work =
                work.Cancellation.Token.ThrowIfCancellationRequested()

                if not (lock gate (fun () -> current work)) then
                    raise (NativeFailure "Cancelled")

            let active work =
                lock gate (fun () ->
                    check work

                    match snapshot with
                    | Some value when Some(SnapshotId.format value.Info.Metadata.Id) = work.Request.Snapshot -> value
                    | _ -> raise (NativeFailure "SnapshotNotFound"))

            let currentQuery work id =
                lock gate (fun () ->
                    active work |> ignore

                    match query with
                    | Some value when value.Id = id -> value
                    | _ ->
                        let code =
                            match work.Request.Operation with
                            | NativeOperation.Elements _
                            | NativeOperation.Export _ -> "SceneNotFound"
                            | _ -> "QueryNotFound"

                        raise (NativeFailure code))

            let execute (work: NativePending) =
                task {
                    let token = work.Cancellation.Token

                    use deadline =
                        new Timer(
                            (fun _ ->
                                try
                                    work.Cancellation.Cancel()
                                with :? ObjectDisposedException ->
                                    ()),
                            null,
                            120000,
                            Timeout.Infinite
                        )

                    let mutable replacement: NativeSnapshot option = None
                    let mutable replacementQuery: NativeQuery option = None
                    let mutable exportStaging: (string * string) option = None
                    let mutable publishReady = false
                    let mutable newestProgress: SnapshotProgress option = None
                    let progressGate = obj ()
                    use stopProgress = CancellationTokenSource.CreateLinkedTokenSource(connection.Token)

                    let progress = {
                        new IProgress<SnapshotProgress> with
                            member _.Report(value) =
                                lock progressGate (fun () -> newestProgress <- Some value)
                    }

                    let pump =
                        task {
                            try
                                while not stopProgress.IsCancellationRequested do
                                    do! Task.Delay(100, stopProgress.Token)

                                    let update =
                                        lock progressGate (fun () ->
                                            let value = newestProgress
                                            newestProgress <- None
                                            value)

                                    match update with
                                    | Some value when not token.IsCancellationRequested ->
                                        do!
                                            writer.WriteAsync(
                                                NativeProtocol.progress
                                                    work.Request
                                                    (string value.Stage)
                                                    value.Completed,
                                                stopProgress.Token
                                            )
                                    | _ -> ()
                            with
                            | :? OperationCanceledException -> ()
                            | ProtocolFailure code -> fail code
                            | _ -> fail "InternalError"
                        }

                    try
                        try
                            let! terminal =
                                task {
                                    try
                                        check work

                                        let! bytes =
                                            task {
                                                match work.Request.Operation with
                                                | NativeOperation.Load(path, dac, cache, network) ->
                                                    if
                                                        not (Path.IsPathFullyQualified path)
                                                        || (network && cache.IsNone)
                                                    then
                                                        raise (NativeFailure "InvalidRequest")

                                                    if network && not (OperatingSystem.IsWindows()) then
                                                        raise (NativeFailure "UnsupportedTarget")

                                                    let options = {
                                                        SnapshotReaderOptions.defaults with
                                                            IncludeReferences = false
                                                            IncludeRoots = false
                                                            IncludeStringDetails = false
                                                            Dac = {
                                                                TrustedPaths =
                                                                    dac
                                                                    |> Option.map (fun path -> Map.ofList [ 0, path ])
                                                                    |> Option.defaultValue Map.empty
                                                                CacheDirectory = cache
                                                                AllowNetwork = network
                                                            }
                                                    }

                                                    let! extracted = readSnapshot path options progress token
                                                    check work

                                                    let raw =
                                                        extracted
                                                        |> Result.defaultWith (errorCode >> NativeFailure >> raise)

                                                    let store =
                                                        IndexedHeapSnapshot.Create(
                                                            raw,
                                                            SnapshotIndexLimits.defaults,
                                                            token
                                                        )
                                                        |> Result.defaultWith (fun _ ->
                                                            raise (NativeFailure "OutputLimit"))

                                                    let info =
                                                        try
                                                            store.GetInfo(token)
                                                            |> Result.defaultWith (fun _ ->
                                                                raise (NativeFailure "InternalError"))
                                                        with _ ->
                                                            store.Dispose()
                                                            reraise ()

                                                    replacement <- Some { Store = store; Info = info }

                                                    return
                                                        NativeProtocol.success
                                                            work.Request
                                                            (Some(SnapshotId.format raw.Metadata.Id))
                                                            {|
                                                                tag = "snapshot"
                                                                objectCount = Protocol.idValue raw.Metadata.ObjectCount
                                                                sourcePartial = raw.IsPartial
                                                                diagnosticCount = raw.Diagnostics.Length
                                                            |}
                                                | NativeOperation.Dispose ->
                                                    return
                                                        NativeProtocol.success work.Request work.Request.Snapshot {|
                                                            tag = "disposed"
                                                        |}
                                                | NativeOperation.Run(text, settings) ->
                                                    let owner = active work

                                                    let limits = {
                                                        QueryLimits.defaults with
                                                            MaxResults = settings.MaxResults
                                                    }

                                                    let result =
                                                        match
                                                            Mql.prepare limits text
                                                            |> Result.bind (Mql.bind owner.Info.Metadata.Id)
                                                        with
                                                        | Error errors -> {
                                                            SnapshotId = owner.Info.Metadata.Id
                                                            Status = QueryStatus.Failed errors
                                                            SourcePartial = owner.Info.IsPartial
                                                            SourceAvailable = true
                                                            SourceDiagnostics = owner.Info.Diagnostics
                                                            Rows = []
                                                            Directives = []
                                                            Candidates = 0
                                                          }
                                                        | Ok plan ->
                                                            Mql.execute limits plan owner.Store {
                                                                QueryExecutionContext.create token with
                                                                    IsSnapshotCurrent =
                                                                        fun _ -> lock gate (fun () -> current work)
                                                            }

                                                    check work

                                                    let built =
                                                        Scene.build settings.Scene result {
                                                            SceneExecutionContext.create token with
                                                                TimeProvider = sceneClock
                                                                IsSnapshotCurrent =
                                                                    fun _ -> lock gate (fun () -> current work)
                                                        }

                                                    check work

                                                    match result.Status, built.Status, built.Scene with
                                                    | QueryStatus.Failed _, _, _ -> ()
                                                    | QueryStatus.Cancelled, _, _
                                                    | _, SceneStatus.Cancelled, _ -> raise (NativeFailure "Cancelled")
                                                    | _, SceneStatus.Truncated _, None ->
                                                        raise (NativeFailure "SceneTruncated")
                                                    | _, SceneStatus.Failed, _
                                                    | _, _, None -> raise (NativeFailure "SceneFailed")
                                                    | _ -> ()

                                                    let id =
                                                        lock gate (fun () ->
                                                            nextQuery <- nextQuery + 1UL
                                                            nextQuery)

                                                    replacementQuery <-
                                                        Some {
                                                            Id = id
                                                            Result = result
                                                            Rows = List.toArray result.Rows
                                                            Scene = built.Scene
                                                            Elements =
                                                                built.Scene
                                                                |> Option.map (_.Elements >> List.toArray)
                                                                |> Option.defaultValue [||]
                                                        }

                                                    return
                                                        NativeProtocol.success
                                                            work.Request
                                                            work.Request.Snapshot
                                                            (NativeJson.queryInfo id id result built.Scene)
                                                | NativeOperation.Rows(id, cursor, size) ->
                                                    let result = currentQuery work id

                                                    return
                                                        NativeJson.page
                                                            work.Request
                                                            "rows"
                                                            id
                                                            cursor
                                                            size
                                                            result.Rows
                                                            NativeJson.row
                                                | NativeOperation.Elements(id, cursor, size) ->
                                                    let result = currentQuery work id

                                                    if result.Scene.IsNone then
                                                        raise (NativeFailure "SceneNotFound")

                                                    return
                                                        NativeJson.page
                                                            work.Request
                                                            "elements"
                                                            id
                                                            cursor
                                                            size
                                                            result.Elements
                                                            NativeJson.element
                                                | NativeOperation.Details(runtime, address, cursor, size) ->
                                                    let owner = active work

                                                    let identity = {
                                                        Runtime = {
                                                            SnapshotId = owner.Info.Metadata.Id
                                                            Index = runtime
                                                        }
                                                        Address = address
                                                    }

                                                    let item =
                                                        owner.Store.TryGetObject(identity, token)
                                                        |> Result.defaultWith (fun _ ->
                                                            raise (NativeFailure "SnapshotNotFound"))

                                                    let items =
                                                        match item with
                                                        | None -> [||]
                                                        | Some item ->
                                                            let segment =
                                                                owner.Info.Segments
                                                                |> Seq.tryFind (fun segment ->
                                                                    segment.Runtime = identity.Runtime
                                                                    && segment.Address = item.SegmentAddress)

                                                            let typ = owner.Store.TryGetType(item.Type, token)

                                                            [| detailEntity item segment typ |]

                                                    return
                                                        NativeJson.page
                                                            work.Request
                                                            "details"
                                                            0UL
                                                            cursor
                                                            size
                                                            items
                                                            NativeJson.entity
                                                | NativeOperation.Export(id, path) ->
                                                    let result = currentQuery work id

                                                    let scene =
                                                        result.Scene
                                                        |> Option.defaultWith (fun () ->
                                                            raise (NativeFailure "SceneNotFound"))

                                                    if
                                                        result.Result.Status <> QueryStatus.Complete
                                                        || result.Result.SourcePartial
                                                        || not result.Result.SourceAvailable
                                                        || scene.Completeness.SceneStatus <> SceneStatus.Complete
                                                    then
                                                        raise (NativeFailure "IncompleteResult")

                                                    let destination = destination path

                                                    let staging =
                                                        Path.Combine(
                                                            Path.GetDirectoryName destination,
                                                            ".memoryvisualizer-"
                                                            + Guid.NewGuid().ToString("N")
                                                            + ".pending"
                                                        )

                                                    use file =
                                                        new FileStream(
                                                            staging,
                                                            FileMode.CreateNew,
                                                            FileAccess.Write,
                                                            FileShare.None
                                                        )

                                                    exportStaging <- Some(staging, destination)

                                                    let count =
                                                        match Svg.write SvgLimits.defaults scene file token with
                                                        | Ok count -> count
                                                        | Error SvgError.OutputLimitExceeded ->
                                                            raise (NativeFailure "OutputLimit")
                                                        | Error SvgError.Cancelled ->
                                                            raise (OperationCanceledException(token))
                                                        | Error _ -> raise (NativeFailure "ExportFailed")

                                                    file.Flush(true)

                                                    return
                                                        NativeProtocol.success work.Request work.Request.Snapshot {|
                                                            tag = "export"
                                                            byteLength = Protocol.idValue (uint64 count)
                                                        |}
                                            }

                                        stopProgress.Cancel()
                                        do! pump
                                        publishReady <- true
                                        return bytes
                                    with
                                    | :? OperationCanceledException ->
                                        return NativeProtocol.error work.Request "Cancelled"
                                    | NativeFailure code -> return NativeProtocol.error work.Request code
                                    | :? UnauthorizedAccessException ->
                                        return NativeProtocol.error work.Request "AccessDenied"
                                    | :? IOException -> return NativeProtocol.error work.Request "ReadFailed"
                                    | :? ArgumentException -> return NativeProtocol.error work.Request "InvalidRequest"
                                }

                            stopProgress.Cancel()
                            do! pump

                            do!
                                writer.WriteAsync(
                                    terminal,
                                    connection.Token,
                                    fun () ->
                                        lock gate (fun () ->
                                            let chosen =
                                                try
                                                    check work

                                                    match replacement |> Option.filter (fun _ -> publishReady) with
                                                    | Some value ->
                                                        snapshot
                                                        |> Option.iter (fun previous -> previous.Store.Dispose())

                                                        snapshot <- Some value
                                                        query <- None
                                                        replacement <- None
                                                    | None -> ()

                                                    match
                                                        replacementQuery |> Option.filter (fun _ -> publishReady)
                                                    with
                                                    | Some value -> query <- Some value
                                                    | None -> ()

                                                    match work.Request.Operation with
                                                    | NativeOperation.Dispose when publishReady ->
                                                        snapshot |> Option.iter (fun value -> value.Store.Dispose())
                                                        snapshot <- None
                                                    | _ -> ()

                                                    match exportStaging |> Option.filter (fun _ -> publishReady) with
                                                    | Some(staging, destination) ->
                                                        File.Move(staging, destination, false)
                                                        exportStaging <- None
                                                    | None -> ()

                                                    terminal
                                                with
                                                | :? OperationCanceledException
                                                | NativeFailure "Cancelled" ->
                                                    NativeProtocol.error work.Request "Cancelled"
                                                | :? IOException
                                                | :? UnauthorizedAccessException ->
                                                    NativeProtocol.error work.Request "ExportFailed"

                                            // State/file commit and admission removal are one boundary:
                                            // a later cancel cannot rewrite the authoritative terminal.
                                            work.TerminalChosen <- true
                                            pending.Remove(work.Request.Id) |> ignore

                                            if work.Exclusive then
                                                exclusive <- false

                                            chosen)
                                )
                        with
                        | :? OperationCanceledException when connection.IsCancellationRequested -> ()
                        | ProtocolFailure code -> fail code
                        | _ -> fail "InternalError"
                    finally
                        stopProgress.Cancel()
                        replacement |> Option.iter (fun value -> value.Store.Dispose())

                        exportStaging
                        |> Option.iter (fun (path, _) ->
                            try
                                File.Delete path
                            with _ ->
                                ())

                        lock gate (fun () ->
                            pending.Remove(work.Request.Id) |> ignore
                            teardown.Remove(work.Request.Id) |> ignore
                            work.Cancellation.Dispose())

                        work.Completion.TrySetResult(()) |> ignore
                }

            let accept (request: NativeRequest) =
                task {
                    let decision =
                        lock gate (fun () ->
                            if request.Id <= lastRequest then
                                raise (ProtocolFailure "ProtocolViolation")

                            lastRequest <- request.Id

                            let scoped =
                                snapshot |> Option.map (fun value -> SnapshotId.format value.Info.Metadata.Id)

                            let isExclusive =
                                match request.Operation with
                                | NativeOperation.Load _
                                | NativeOperation.Run _
                                | NativeOperation.Export _
                                | NativeOperation.Dispose -> true
                                | _ -> false

                            if pending.Count >= Protocol.MaxOutstanding then
                                Choice1Of2 "Busy"
                            elif request.Snapshot.IsSome && request.Snapshot <> scoped then
                                Choice1Of2 "SnapshotNotFound"
                            elif exclusive then
                                Choice1Of2 "Busy"
                            else
                                match request.Operation with
                                | NativeOperation.Load _ -> invalidate false
                                | NativeOperation.Run _
                                | NativeOperation.Dispose -> invalidate true
                                | _ -> ()

                                if isExclusive then
                                    exclusive <- true

                                let work = {
                                    Request = request
                                    Epoch = epoch
                                    Exclusive = isExclusive
                                    TerminalChosen = false
                                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(connection.Token)
                                    Completion =
                                        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                                }

                                pending.Add(request.Id, work)
                                teardown.Add(request.Id, work)
                                Choice2Of2 work)

                    match decision with
                    | Choice1Of2 code -> do! writer.WriteAsync(NativeProtocol.error request code, connection.Token)
                    | Choice2Of2 work -> Task.Run(Func<Task>(fun () -> execute work)) |> ignore
                }

            let finish () =
                lock gate (fun () ->
                    invalidate true

                    teardown.Values
                    |> Seq.map (fun work -> work.Completion.Task :> Task)
                    |> Seq.toArray)
                |> Task.WhenAll

            let mutable graceful = false

            try
                let! first = reader.ReadAsync(connection.Token).WaitAsync(TimeSpan.FromSeconds(5.0))

                match first with
                | None -> ()
                | Some bytes ->
                    match NativeProtocol.parseInbound bytes with
                    | NativeInbound.Hello -> ()
                    | _ -> raise (ProtocolFailure "ProtocolViolation")

                    do! writer.WriteAsync(NativeProtocol.ready (), connection.Token)
                    let mutable reading = true

                    while reading do
                        let! bytes = reader.ReadAsync(connection.Token)

                        match bytes with
                        | None -> reading <- false
                        | Some bytes ->
                            match NativeProtocol.parseInbound bytes with
                            | NativeInbound.Hello -> raise (ProtocolFailure "ProtocolViolation")
                            | NativeInbound.Request request -> do! accept request
                            | NativeInbound.Cancel id ->
                                lock gate (fun () ->
                                    match pending.TryGetValue id with
                                    | true, work -> work.Cancellation.Cancel()
                                    | _ -> ())
                            | NativeInbound.Shutdown ->
                                graceful <- true
                                reading <- false

                    if graceful then
                        do! (finish ()).WaitAsync(TimeSpan.FromSeconds(2.0))
                        do! writer.WriteAsync(NativeProtocol.bye (), connection.Token)
            with
            | ProtocolFailure code -> fail code
            | :? TimeoutException -> fail "TransportTimeout"
            | :? OperationCanceledException when failure.Task.IsCompleted -> ()
            | _ -> fail "InternalError"

            connection.Cancel()

            try
                do! (finish ()).WaitAsync(TimeSpan.FromMilliseconds(250.0))
            with _ ->
                ()

            lock gate (fun () ->
                snapshot |> Option.iter (fun value -> value.Store.Dispose())
                snapshot <- None
                query <- None)

            if failure.Task.IsCompleted then
                do! writer.WriteFatalAsync(fun () -> NativeProtocol.fatal failure.Task.Result)

                try
                    let writing =
                        Task.Run(Func<Task>(fun () -> diagnostics.WriteLineAsync("Native worker protocol failure.")))

                    do! writing.WaitAsync(TimeSpan.FromMilliseconds(100.0))
                with _ ->
                    ()

                return 2
            else
                return 0
        }

    /// The injected reader is the same boundary used by the native process; tests need no native DAC.
    let runWithAsync readSnapshot input output diagnostics =
        runWithSceneClockAsync TimeProvider.System readSnapshot input output diagnostics

    let runAsync input output diagnostics =
        runWithAsync
            (fun path options progress token ->
                ClrMdSnapshotReader().ReadSnapshotAsync(path, options, Some progress, token))
            input
            output
            diagnostics
