namespace MemoryVisualizer.Analysis.Tests

open System
open System.IO
open System.Text

type internal ProcessOutput(capacity: int) =
    let gate = obj ()
    let tail = StringBuilder(capacity)

    member _.Tail = lock gate (fun () -> tail.ToString())

    member _.DrainAsync(reader: TextReader) =
        task {
            let buffer = Array.zeroCreate<char> 1024
            let mutable reading = true

            while reading do
                let! count = reader.ReadAsync(buffer.AsMemory())

                if count = 0 then
                    reading <- false
                else
                    lock gate (fun () ->
                        tail.Append(buffer, 0, count) |> ignore

                        if tail.Length > capacity then
                            tail.Remove(0, tail.Length - capacity) |> ignore)
        }
