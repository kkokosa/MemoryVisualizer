module MemoryVisualizer.Worker.Program

open System
open MemoryVisualizer.Hosting

[<EntryPoint>]
let main arguments =
    match arguments with
    | [| "--protocol"; "--backend=fake" |]
    | [| "--backend=fake"; "--protocol" |] ->
        Worker.runAsync (Console.OpenStandardInput()) (Console.OpenStandardOutput()) Console.Error
        |> fun running -> running.GetAwaiter().GetResult()
    | _ -> CommandLine.run "MemoryVisualizer.Worker" arguments Console.Out Console.Error
