namespace MemoryVisualizer.Cli

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Core
open MemoryVisualizer.Query

/// Explicit CLI schema, independent of both F# union serialization and worker protocol v1.
[<RequireQualifiedAccess>]
module QueryJson =
    let private number (value: uint64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private address (value: uint64) =
        "0x" + value.ToString("x16", CultureInfo.InvariantCulture)

    let private text (writer: Utf8JsonWriter) (name: string) (value: string) = writer.WriteString(name, value)

    let private entity (writer: Utf8JsonWriter) (value: SelectedEntity) =
        writer.WriteStartObject()
        text writer "kind" (string value.Kind)
        text writer "snapshotId" (SnapshotId.format value.Runtime.SnapshotId)
        writer.WriteNumber("runtime", value.Runtime.Index)
        writer.WriteNumber("heap", value.Heap)
        text writer "segmentAddress" (address value.SegmentAddress)
        text writer "address" (address value.Address)
        text writer "size" (number value.Size)
        value.End |> Option.iter (address >> text writer "end")

        value.TypeIdentity
        |> Option.iter (fun typ -> text writer "methodTable" (address typ.MethodTable))

        value.TypeName |> Option.iter (text writer "type")

        value.Generation
        |> Option.iter (fun generation -> writer.WriteNumber("generation", generation))

        value.IsFree |> Option.iter (fun free -> writer.WriteBoolean("isFree", free))

        text
            writer
            "heapKind"
            (match value.HeapKind with
             | HeapKind.Unknown name -> name
             | kind -> string kind)

        writer.WriteEndObject()

    let private value (writer: Utf8JsonWriter) item =
        writer.WriteStartObject()

        match item with
        | QueryValue.Unsigned value ->
            text writer "kind" "uint64"
            text writer "value" (number value)
        | QueryValue.Text value ->
            text writer "kind" "text"
            text writer "value" value
        | QueryValue.Boolean value ->
            text writer "kind" "boolean"
            writer.WriteBoolean("value", value)
        | QueryValue.Missing ->
            text writer "kind" "missing"
            writer.WriteNull("value")
        | QueryValue.Entity item ->
            text writer "kind" "entity"
            writer.WritePropertyName("value")
            entity writer item

        writer.WriteEndObject()

    let private diagnostics (writer: Utf8JsonWriter) (items: QueryDiagnostic list) =
        writer.WriteStartArray("diagnostics")

        for item in items do
            writer.WriteStartObject()
            text writer "code" item.Code
            text writer "message" item.Message
            writer.WriteStartObject("span")
            writer.WriteNumber("offset", item.Span.Offset)
            writer.WriteNumber("length", item.Span.Length)
            writer.WriteNumber("line", item.Span.Line)
            writer.WriteNumber("column", item.Span.Column)
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndArray()

    let private output (stdout: TextWriter) write =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        write writer
        writer.WriteEndObject()
        writer.Flush()
        stdout.WriteLine(Encoding.UTF8.GetString(stream.ToArray()))

    let writeDiagnostics stdout items =
        output stdout (fun writer ->
            text writer "status" "failed"
            writer.WriteNull("snapshotId")
            diagnostics writer items)

    let write stdout (result: QueryResult) =
        output stdout (fun writer ->
            text writer "snapshotId" (SnapshotId.format result.SnapshotId)

            let status, reasons, errors =
                match result.Status with
                | QueryStatus.Complete -> "complete", [], []
                | QueryStatus.Truncated reasons -> "truncated", reasons, []
                | QueryStatus.Cancelled -> "cancelled", [], []
                | QueryStatus.Failed errors -> "failed", [], errors

            text writer "status" status
            writer.WriteBoolean("sourceAvailable", result.SourceAvailable)
            writer.WriteBoolean("sourcePartial", result.SourcePartial)
            text writer "candidates" (number (uint64 result.Candidates))
            writer.WriteStartArray("truncationReasons")

            for reason in reasons do
                writer.WriteStringValue(string reason)

            writer.WriteEndArray()
            diagnostics writer errors
            text writer "sourceDiagnosticCount" (number (uint64 result.SourceDiagnostics.Count))
            // Source diagnostics may be numerous. Never silently imply this sample is the full set.
            writer.WriteBoolean("sourceDiagnosticsTruncated", result.SourceDiagnostics.Count > 64)
            writer.WriteStartArray("sourceDiagnostics")

            for index in 0 .. min 64 result.SourceDiagnostics.Count - 1 do
                let diagnostic = result.SourceDiagnostics[index]
                writer.WriteStartObject()
                text writer "code" diagnostic.Code
                text writer "stage" (string diagnostic.Stage)
                text writer "message" diagnostic.Message
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("rows")

            for row in result.Rows do
                writer.WriteStartObject()
                writer.WriteNumber("statementIndex", row.StatementIndex)
                writer.WritePropertyName("entity")
                entity writer row.Entity
                writer.WriteStartArray("values")

                for name, item in row.Values do
                    writer.WriteStartObject()
                    text writer "name" name
                    writer.WritePropertyName("value")
                    value writer item
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("directives")

            for directive in result.Directives do
                writer.WriteStartObject()
                writer.WriteNumber("statementIndex", directive.StatementIndex)
                text writer "kind" (string directive.Kind)
                writer.WriteNumber("runtime", directive.Runtime.Index)
                writer.WriteNumber("heap", directive.Heap)
                text writer "address" (address directive.Address)
                text writer "size" (number directive.Size)
                writer.WriteNumber("width", directive.Width)
                text writer "background" directive.Background
                text writer "labelPosition" (string directive.LabelPosition)

                directive.Label
                |> Option.iter (fun item ->
                    writer.WritePropertyName("label")
                    value writer item)

                directive.Entity
                |> Option.iter (fun item ->
                    writer.WritePropertyName("entity")
                    entity writer item)

                writer.WriteEndObject()

            writer.WriteEndArray())

[<RequireQualifiedAccess>]
module QueryCommand =
    let usage =
        "query <dump-path> --query <MQL-text> [--dac <trusted-absolute-path>] [--cache <trusted-absolute-directory>] [--allow-network] [--max-results <n>] [--max-directives <n>] [--max-candidates <n>] [--max-elapsed-ms <n>]"

    let private parse (arguments: string array) =
        let remaining = ResizeArray<string>()
        let mutable source = None
        let mutable limits = QueryLimits.defaults
        let mutable error = None
        let mutable index = 0
        let seen = Collections.Generic.HashSet<string>()

        while index < arguments.Length && error.IsNone do
            let flag = arguments[index]

            if flag = "--query" || flag.StartsWith("--max-", StringComparison.Ordinal) then
                if not (seen.Add flag) then
                    error <- Some $"Duplicate option: {flag}"
                elif index + 1 >= arguments.Length then
                    error <- Some $"Missing value for {flag}."
                else
                    let value = arguments[index + 1]

                    if flag = "--query" then
                        source <- Some value
                    else
                        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
                        | true, count ->
                            match flag with
                            | "--max-results" -> limits <- { limits with MaxResults = count }
                            | "--max-directives" -> limits <- { limits with MaxDirectives = count }
                            | "--max-candidates" -> limits <- { limits with MaxCandidates = count }
                            | "--max-elapsed-ms" ->
                                limits <- {
                                    limits with
                                        MaxElapsedMilliseconds = count
                                }
                            | _ -> error <- Some $"Unsupported query option: {flag}"
                        | _ -> error <- Some $"Expected a positive integer for {flag}."

                    index <- index + 1
            else
                remaining.Add flag

            index <- index + 1

        match error, source with
        | Some message, _ -> Error message
        | _, None -> Error usage
        | _, Some source ->
            Inspect.parse (remaining.ToArray())
            |> Result.map (fun (path, options) ->
                path,
                {
                    options with
                        IncludeReferences = false
                        IncludeRoots = false
                },
                source,
                limits)

    let run arguments (stdout: TextWriter) (stderr: TextWriter) (token: CancellationToken) =
        match parse arguments with
        | Error message ->
            stderr.WriteLine message
            2
        | Ok(path, options, source, limits) ->
            let error diagnostics =
                QueryJson.writeDiagnostics stdout diagnostics
                stderr.WriteLine("MQL failed; see source-spanned diagnostics in stdout.")
                2
            // Compile once before native work, and bind the actual snapshot identity after import.
            match Mql.prepare limits source with
            | Error diagnostics -> error diagnostics
            | Ok prepared ->
                try
                    match
                        ClrMdSnapshotReader().ReadSnapshotAsync(path, options, None, token).GetAwaiter().GetResult()
                    with
                    | Error failure ->
                        stderr.WriteLine(string failure)
                        2
                    | Ok snapshot ->
                        match Mql.bind snapshot.Metadata.Id prepared with
                        | Error diagnostics -> error diagnostics
                        | Ok plan ->
                            match IndexedHeapSnapshot.Create(snapshot, SnapshotIndexLimits.defaults, token) with
                            | Error failure ->
                                stderr.WriteLine(string failure)
                                2
                            | Ok index ->
                                use store = index
                                let result = Mql.execute limits plan store (QueryExecutionContext.create token)
                                QueryJson.write stdout result

                                match result.Status with
                                | QueryStatus.Complete -> if result.SourcePartial then 3 else 0
                                | QueryStatus.Truncated _ -> 3
                                | QueryStatus.Cancelled -> 130
                                | QueryStatus.Failed _ -> 2
                with :? OperationCanceledException when token.IsCancellationRequested ->
                    stderr.WriteLine("Cancelled. Native resources have been released.")
                    130
