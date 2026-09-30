namespace MemoryVisualizer.Worker

open System
open System.IO
open System.Threading
open System.Threading.Tasks

/// Keeps at most one bounded frame and one small read buffer, even without a newline.
type FrameReader(input: Stream, ?frameTimeout: TimeSpan) =
    let buffer = Array.zeroCreate<byte> 4096
    let frame = Array.zeroCreate<byte> Protocol.MaxFrameBytes
    let mutable position = 0
    let mutable available = 0

    member _.ReadAsync(cancellation: CancellationToken) =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            let mutable length = 0
            let mutable finished = false
            let mutable result = None

            while not finished do
                if position = available then
                    let reading =
                        Task.Run<int>(fun () -> input.ReadAsync(buffer.AsMemory(), deadline.Token).AsTask())

                    let! count =
                        task {
                            try
                                return! reading.WaitAsync(deadline.Token)
                            with :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                                return raise (ProtocolFailure "TransportTimeout")
                        }

                    position <- 0
                    available <- count

                    if count = 0 then
                        if length <> 0 then
                            raise (ProtocolFailure "InvalidFrame")

                        finished <- true

                if not finished then
                    if length = 0 then
                        deadline.CancelAfter(defaultArg frameTimeout (TimeSpan.FromSeconds(10.0)))

                    let value = buffer[position]
                    position <- position + 1

                    if value = 10uy then
                        if length = 0 then
                            raise (ProtocolFailure "InvalidFrame")

                        result <- Some(frame[0 .. length - 1])
                        finished <- true
                    elif value = 13uy || length = frame.Length then
                        raise (ProtocolFailure "InvalidFrame")
                    else
                        frame[length] <- value
                        length <- length + 1

            return result
        }

/// There is no output queue; producers await both serialization and pipe backpressure.
type FrameWriter(output: Stream, timeout: TimeSpan, ?validate: byte array -> unit) =
    let semaphore = new SemaphoreSlim(1, 1)
    let validate = defaultArg validate Protocol.validateOutbound

    member _.WriteAsync(bytes: byte array, cancellation: CancellationToken, ?prepare: unit -> byte array) =
        task {
            validate bytes
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            deadline.CancelAfter(timeout)
            let mutable entered = false

            try
                try
                    do! semaphore.WaitAsync(deadline.Token)
                    entered <- true

                    let bytes =
                        prepare |> Option.map (fun choose -> choose ()) |> Option.defaultValue bytes

                    validate bytes
                    let framed = Array.append bytes [| 10uy |]

                    let writing =
                        Task.Run(Func<Task>(fun () -> output.WriteAsync(framed.AsMemory(), deadline.Token).AsTask()))

                    do! writing.WaitAsync(deadline.Token)
                    let flushing = Task.Run(Func<Task>(fun () -> output.FlushAsync(deadline.Token)))
                    do! flushing.WaitAsync(deadline.Token)
                with :? OperationCanceledException when not cancellation.IsCancellationRequested ->
                    raise (ProtocolFailure "TransportTimeout")
            finally
                if entered then
                    semaphore.Release() |> ignore
        }

    member internal this.WriteFatalAsync(encode: unit -> byte array, ?startDeadline: unit -> CancellationTokenSource) =
        task {
            try
                // Cold serializer initialization must not consume the bounded pipe-write budget.
                let bytes = encode ()

                use deadline =
                    (defaultArg startDeadline (fun () -> new CancellationTokenSource(200))) ()

                do! this.WriteAsync(bytes, deadline.Token)
            with _ ->
                ()
        }
