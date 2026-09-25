namespace MemoryVisualizer.Hosting

open System.IO
open System.Reflection

[<RequireQualifiedAccess>]
module CommandLine =
    let private version () =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion

    let private help executable =
        String.concat "\n" [
            $"Usage: {executable} [--help | --version]"
            ""
            "MemoryVisualizer foundation scaffold."
            "Dump analysis, queries, rendering, and worker IPC are not implemented."
            ""
            "  --help, -h, help       Show this help."
            "  --version, version    Show the application version."
        ]

    let run executable (arguments: string array) (stdout: TextWriter) (stderr: TextWriter) =
        match arguments with
        | [||]
        | [| "--help" | "-h" | "help" |] ->
            stdout.WriteLine(help executable)
            0
        | [| "--version" | "version" |] ->
            stdout.WriteLine($"{executable} {version ()}")
            0
        | _ ->
            stderr.WriteLine("Unsupported arguments. This executable only supports help and version.")
            stderr.WriteLine(help executable)
            2
