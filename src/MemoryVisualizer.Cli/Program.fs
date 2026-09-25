module MemoryVisualizer.Cli.Program

open System
open MemoryVisualizer.Hosting

[<EntryPoint>]
let main arguments =
    CommandLine.run "MemoryVisualizer.Cli" arguments Console.Out Console.Error
