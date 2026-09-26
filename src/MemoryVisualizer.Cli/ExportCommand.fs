namespace MemoryVisualizer.Cli

open System
open System.Globalization
open System.IO
open System.Threading
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Core
open MemoryVisualizer.Query
open MemoryVisualizer.Scene

exception private ExportFailure of string

[<RequireQualifiedAccess>]
module ExportCommand =
    let usage =
        "export <dump-path> --query <MQL-text> --output <new.svg> [--dac <trusted-absolute-path>] [--cache <trusted-absolute-directory>] [--allow-network] [--view-start <uint64>] [--view-size <uint64>] [--plot-width <n>] [--redact-addresses] [--redact-strings] [--redact-paths] [--redact-labels] [--max-results <n>] [--max-directives <n>] [--max-candidates <n>] [--max-elapsed-ms <n>] [--max-scene-directives <n>] [--max-elements <n>] [--max-lanes <n>] [--max-label-chars <n>] [--max-total-label-chars <n>] [--max-scene-elapsed-ms <n>] [--max-svg-bytes <n>]"

    let private fail message = raise (ExportFailure message)

    let private parse (arguments: string array) =
        if arguments.Length = 0 || arguments[0].StartsWith("--", StringComparison.Ordinal) then
            fail usage

        let inspect = ResizeArray<string>([| arguments[0] |])
        let mutable query = None
        let mutable output = None
        let mutable queryLimits = QueryLimits.defaults
        let mutable sceneOptions = SceneOptions.defaults
        let mutable svgLimits = SvgLimits.defaults
        let mutable viewStart = None
        let mutable viewSize = None
        let mutable index = 1
        let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        while index < arguments.Length do
            let flag = arguments[index]

            if not (seen.Add flag) then
                fail $"Duplicate option: {flag}"

            let value () =
                if index + 1 >= arguments.Length then
                    fail $"Missing value for {flag}."

                index <- index + 1
                arguments[index]

            let integer () =
                match Int32.TryParse(value (), NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, count -> count
                | _ -> fail $"Expected a positive integer for {flag}."

            let unsigned () =
                let raw = value ()

                let digits, style =
                    if raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
                        raw.Substring(2), NumberStyles.AllowHexSpecifier
                    else
                        raw, NumberStyles.None

                match UInt64.TryParse(digits, style, CultureInfo.InvariantCulture) with
                | true, count -> count
                | _ -> fail $"Expected a decimal or 0x-prefixed uint64 for {flag}."

            match flag with
            | "--query" -> query <- Some(value ())
            | "--output" -> output <- Some(value ())
            | "--dac"
            | "--cache" ->
                inspect.Add flag
                inspect.Add(value ())
            | "--allow-network" -> inspect.Add flag
            | "--redact-addresses" ->
                sceneOptions <- {
                    sceneOptions with
                        Redaction = {
                            sceneOptions.Redaction with
                                Addresses = true
                        }
                }
            | "--redact-strings" ->
                sceneOptions <- {
                    sceneOptions with
                        Redaction = {
                            sceneOptions.Redaction with
                                Strings = true
                        }
                }
            | "--redact-paths" ->
                sceneOptions <- {
                    sceneOptions with
                        Redaction = {
                            sceneOptions.Redaction with
                                Paths = true
                        }
                }
            | "--redact-labels" ->
                sceneOptions <- {
                    sceneOptions with
                        Redaction = {
                            sceneOptions.Redaction with
                                Labels = true
                        }
                }
            | "--view-start" -> viewStart <- Some(unsigned ())
            | "--view-size" -> viewSize <- Some(unsigned ())
            | "--plot-width" ->
                sceneOptions <- {
                    sceneOptions with
                        PlotWidth = integer ()
                }
            | "--max-results" ->
                queryLimits <- {
                    queryLimits with
                        MaxResults = integer ()
                }
            | "--max-directives" ->
                queryLimits <- {
                    queryLimits with
                        MaxDirectives = integer ()
                }
            | "--max-candidates" ->
                queryLimits <- {
                    queryLimits with
                        MaxCandidates = integer ()
                }
            | "--max-elapsed-ms" ->
                queryLimits <- {
                    queryLimits with
                        MaxElapsedMilliseconds = integer ()
                }
            | "--max-scene-directives" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxDirectives = integer ()
                        }
                }
            | "--max-elements" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxElements = integer ()
                        }
                }
            | "--max-lanes" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxLanes = integer ()
                        }
                }
            | "--max-label-chars" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxLabelCharacters = integer ()
                        }
                }
            | "--max-total-label-chars" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxTotalLabelCharacters = integer ()
                        }
                }
            | "--max-scene-elapsed-ms" ->
                sceneOptions <- {
                    sceneOptions with
                        Limits = {
                            sceneOptions.Limits with
                                MaxElapsedMilliseconds = integer ()
                        }
                }
            | "--max-svg-bytes" -> svgLimits <- { MaxBytes = integer () }
            | _ -> fail $"Unsupported export option: {flag}"

            index <- index + 1

        match viewStart, viewSize with
        | Some first, Some size ->
            sceneOptions <- {
                sceneOptions with
                    Viewport = Some { Start = first; Size = size }
            }
        | None, None -> ()
        | _ -> fail "--view-start and --view-size must be specified together."

        let path, readerOptions =
            Inspect.parse (inspect.ToArray()) |> Result.defaultWith fail

        let source = query |> Option.defaultWith (fun () -> fail "--query is required.")
        let output = output |> Option.defaultWith (fun () -> fail "--output is required.")

        let prepared =
            Mql.prepare queryLimits source
            |> Result.defaultWith (fun errors ->
                errors
                |> List.map (fun error -> $"{error.Code} ({error.Span.Line},{error.Span.Column}): {error.Message}")
                |> String.concat "\n"
                |> fail)

        Scene.validate sceneOptions
        |> Result.defaultWith (fun errors ->
            errors
            |> List.map (fun error -> error.Code + ": " + error.Message)
            |> String.concat "\n"
            |> fail)

        Svg.validate svgLimits |> Result.defaultWith (fun error -> fail (string error))

        path,
        {
            readerOptions with
                IncludeReferences = false
                IncludeRoots = false
        },
        prepared,
        queryLimits,
        sceneOptions,
        svgLimits,
        output

    let private destination dump output =
        if String.IsNullOrWhiteSpace output then
            fail "Output path must not be empty."

        let full = Path.GetFullPath output
        let name = Path.GetFileName full

        if
            String.IsNullOrWhiteSpace name
            || name.EndsWith(' ')
            || name.EndsWith('.')
            || System.Text.Encoding.UTF8.GetByteCount(name) > 255
            || (name |> Seq.exists (fun ch -> ch < ' ' || "<>:\"/\\|?*".Contains ch))
        then
            fail
                "Output requires a portable file name of at most 255 UTF-8 bytes without controls, reserved characters or trailing dots/spaces."

        let stem = (name.Split('.')[0]).ToUpperInvariant()

        if
            [
                "CON"
                "PRN"
                "AUX"
                "NUL"
                "COM1"
                "COM2"
                "COM3"
                "COM4"
                "COM5"
                "COM6"
                "COM7"
                "COM8"
                "COM9"
                "LPT1"
                "LPT2"
                "LPT3"
                "LPT4"
                "LPT5"
                "LPT6"
                "LPT7"
                "LPT8"
                "LPT9"
            ]
            |> List.contains stem
        then
            fail "Output uses a reserved device name."

        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if String.Equals(full, Path.GetFullPath dump, comparison) then
            fail "Output must not be the input dump."

        if File.Exists full || Directory.Exists full then
            fail "Output already exists; export never overwrites."

        if not (Directory.Exists(Path.GetDirectoryName full)) then
            fail "Output directory does not exist."

        full

    let private export path options prepared queryLimits sceneOptions svgLimits (stream: Stream) token =
        match ClrMdSnapshotReader().ReadSnapshotAsync(path, options, None, token).GetAwaiter().GetResult() with
        | Error error -> fail (string error)
        | Ok snapshot ->
            let plan =
                Mql.bind snapshot.Metadata.Id prepared
                |> Result.defaultWith (fun errors -> fail (sprintf "%A" errors))

            use store =
                IndexedHeapSnapshot.Create(snapshot, SnapshotIndexLimits.defaults, token)
                |> Result.defaultWith (fun error -> fail (string error))

            let result = Mql.execute queryLimits plan store (QueryExecutionContext.create token)

            match result.Status with
            | QueryStatus.Cancelled -> 130, "Query cancelled; no SVG published."
            | QueryStatus.Failed errors -> 2, sprintf "Query failed: %A. No SVG published." errors
            | QueryStatus.Truncated reasons -> 3, sprintf "Query truncated: %A. No SVG published." reasons
            | QueryStatus.Complete when result.SourcePartial || not result.SourceAvailable ->
                3, "Source is partial or unavailable; no SVG published."
            | QueryStatus.Complete ->
                let built = Scene.build sceneOptions result (SceneExecutionContext.create token)

                match built.Status, built.Scene with
                | SceneStatus.Cancelled, _ -> 130, "Scene cancelled; no SVG published."
                | SceneStatus.Truncated reasons, _ -> 3, sprintf "Scene truncated: %A. No SVG published." reasons
                | SceneStatus.Complete, Some scene ->
                    match Svg.write svgLimits scene stream token with
                    | Ok _ -> 0, ""
                    | Error SvgError.Cancelled -> 130, "SVG writing cancelled; no SVG published."
                    | Error SvgError.OutputLimitExceeded -> 3, "SVG byte limit exceeded; no SVG published."
                    | Error error -> 2, sprintf "SVG failed: %A. No SVG published." error
                | _ -> 2, sprintf "Scene failed: %A. No SVG published." built.Diagnostics

    let internal runWith export arguments (stdout: TextWriter) (stderr: TextWriter) (token: CancellationToken) =
        let mutable temporary: string option = None
        let mutable exit = 2

        try
            try
                let path, options, prepared, queryLimits, sceneOptions, svgLimits, output =
                    parse arguments

                let output = destination path output
                token.ThrowIfCancellationRequested()

                let temp =
                    Path.Combine(
                        Path.GetDirectoryName output,
                        ".memoryvisualizer-" + Guid.NewGuid().ToString("N") + ".tmp"
                    )
                // Reserve and check directory access before native import. Never open the final path for writing.
                use stream =
                    new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                temporary <- Some temp

                let code, (message: string) =
                    export path options prepared queryLimits sceneOptions svgLimits stream token

                exit <- code

                if code = 0 then
                    token.ThrowIfCancellationRequested()
                    stream.Flush(true)
                    stream.Dispose()
                    token.ThrowIfCancellationRequested()
                    File.Move(temp, output, false)
                    temporary <- None
                    stdout.WriteLine("SVG exported.")
                else
                    stderr.WriteLine message
            with
            | ExportFailure message ->
                stderr.WriteLine message
                exit <- 2
            | :? OperationCanceledException when token.IsCancellationRequested ->
                stderr.WriteLine("Cancelled; no SVG published.")
                exit <- 130
            | :? ArgumentException as error ->
                stderr.WriteLine("Invalid export path or argument: " + error.Message)
                exit <- 2
            | :? NotSupportedException as error ->
                stderr.WriteLine("Unsupported export path or operation: " + error.Message)
                exit <- 2
            | :? IOException as error ->
                stderr.WriteLine("Export I/O failed: " + error.Message)
                exit <- 2
            | :? UnauthorizedAccessException as error ->
                stderr.WriteLine("Export access denied: " + error.Message)
                exit <- 2
        finally
            temporary
            |> Option.iter (fun path ->
                try
                    File.Delete path
                with
                | :? IOException as error -> stderr.WriteLine("Temporary output cleanup failed: " + error.Message)
                | :? UnauthorizedAccessException as error ->
                    stderr.WriteLine("Temporary output cleanup denied: " + error.Message))

        exit

    let run arguments stdout stderr token =
        runWith export arguments stdout stderr token
