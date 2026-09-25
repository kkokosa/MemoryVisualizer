module MemoryVisualizer.Cli.Program

open System
open System.Threading
open MemoryVisualizer.Cli
open MemoryVisualizer.Hosting

[<EntryPoint>]
let main arguments =
    if arguments.Length > 0 && arguments[0] = "inspect" then
        use cancellation = new CancellationTokenSource()

        let cancel =
            ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                cancellation.Cancel())

        Console.CancelKeyPress.AddHandler cancel

        try
            Inspect.run arguments[1..] Console.Out Console.Error cancellation.Token
        finally
            Console.CancelKeyPress.RemoveHandler cancel
    else
        let result =
            CommandLine.run "MemoryVisualizer.Cli" arguments Console.Out Console.Error

        if
            result = 0
            && (arguments.Length = 0 || Array.contains arguments[0] [| "--help"; "-h"; "help" |])
        then
            Console.Out.WriteLine("\nDump analysis: " + Inspect.usage)

        result
