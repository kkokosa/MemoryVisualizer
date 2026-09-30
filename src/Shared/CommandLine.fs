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
            if worker then
                $"       {executable} --protocol --backend=native"
            ""
            if worker then
                "MemoryVisualizer native analysis worker."
            else
                "MemoryVisualizer foundation scaffold."
            if executable = "MemoryVisualizer.Cli" then
                "Real queries and rendering are not implemented. Use inspect for dump summaries."
            elif worker then
                "Native dump analysis, bounded MQL, positioned scenes, and SVG export."
            else
                "Dump analysis, real queries, and rendering are not implemented in this host."
            if worker then
                "Protocol v1 uses explicit synthetic fixtures; v3 uses the native shared engine."
            ""
            "  --help, -h, help       Show this help."
            "  --version, version    Show the application version."
            if worker then
                "  --protocol --backend=fake    Run the synthetic v1 stdio worker."
            if worker then
                "  --protocol --backend=native  Run the native v3 stdio worker."
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
