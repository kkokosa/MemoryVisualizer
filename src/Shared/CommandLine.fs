namespace MemoryVisualizer.Hosting

open System.IO
open System.Reflection

[<RequireQualifiedAccess>]
module CommandLine =
    let private version () =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion

    let private help executable =
        let worker = executable = "MemoryVisualizer.Worker"

        String.concat "\n" [
            $"Usage: {executable} [--help | --version]"
            if worker then
                $"       {executable} --protocol --backend=fake"
            ""
            "MemoryVisualizer foundation scaffold."
            if executable = "MemoryVisualizer.Cli" then
                "Real queries and rendering are not implemented. Use inspect for dump summaries."
            else
                "Dump analysis, real queries, and rendering are not implemented in this host."
            if worker then
                "The opt-in worker protocol uses synthetic fixtures only."
            ""
            "  --help, -h, help       Show this help."
            "  --version, version    Show the application version."
            if worker then
                "  --protocol --backend=fake    Run the synthetic v1 stdio worker."
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
