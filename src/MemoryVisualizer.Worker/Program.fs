module MemoryVisualizer.Worker.Program

open System
open MemoryVisualizer.Hosting

[<EntryPoint>]
let main arguments =
    CommandLine.run "MemoryVisualizer.Worker" arguments Console.Out Console.Error
