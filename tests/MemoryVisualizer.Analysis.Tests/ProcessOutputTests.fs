module MemoryVisualizer.Analysis.Tests.ProcessOutputTests

open System
open System.IO
open System.IO.Pipes
open System.Text
open System.Threading.Tasks
open Xunit

[<Fact>]
let ``Process output drains beyond pipe capacity and retains only a bounded tail`` () =
    task {
        use pipe =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None)

        use input =
            new AnonymousPipeClientStream(PipeDirection.In, pipe.ClientSafePipeHandle)

        use reader = new StreamReader(input)
        let output = ProcessOutput(4096)
        let draining = output.DrainAsync reader

        let writing =
            Task.Run(fun () ->
                do
                    use writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true)
                    let chunk = String('x', 1024)

                    for _ in 1..1024 do
                        writer.Write chunk

                    writer.Write "diagnostic-tail"

                pipe.Dispose())

        do! Task.WhenAll(writing, draining).WaitAsync(TimeSpan.FromSeconds(10.0))
        Assert.Equal(4096, output.Tail.Length)
        Assert.EndsWith("diagnostic-tail", output.Tail)
    }
