namespace MemoryVisualizer.Worker

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open MemoryVisualizer.Scene

type NativeSettings = { Scene: SceneOptions; MaxResults: int }

[<RequireQualifiedAccess>]
type NativeOperation =
    | Load of path: string * dac: string option * cache: string option * network: bool
    | Dispose
    | Run of text: string * settings: NativeSettings
    | Rows of query: uint64 * cursor: uint64 option * size: int
    | Elements of scene: uint64 * cursor: uint64 option * size: int
    | Details of runtime: int * address: uint64 * cursor: uint64 option * size: int
    | Export of scene: uint64 * path: string

type NativeRequest = {
    Id: uint64
    Snapshot: string option
    Operation: NativeOperation
}

[<RequireQualifiedAccess>]
type NativeInbound =
    | Hello
    | Request of NativeRequest
    | Cancel of uint64
    | Shutdown

exception NativeFailure of string

/// Serializers cannot grow a complete result in memory before discovering its byte limit.
type internal BoundedBuffer(limit: int) =
    inherit MemoryStream(limit)

    override this.Write(bytes: byte array, offset: int, count: int) =
        if this.Position + int64 count > int64 limit then
            raise (NativeFailure "OutputLimit")

        base.Write(bytes, offset, count)

    override this.Write(bytes: ReadOnlySpan<byte>) =
        if this.Position + int64 bytes.Length > int64 limit then
            raise (NativeFailure "OutputLimit")

        base.Write(bytes)

    override this.WriteByte(value) =
        if this.Position >= int64 limit then
            raise (NativeFailure "OutputLimit")

        base.WriteByte(value)

[<RequireQualifiedAccess>]
module NativeProtocol =
    let private invalid () = raise (ProtocolFailure "InvalidFrame")

    let private require condition =
        if not condition then
            invalid ()

    let private field (name: string) (value: JsonElement) = value.GetProperty(name)

    let private fields names (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)
        require ((value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList names)

    let private text minimum maximum (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.String)
        let result = value.GetString()
        require (result.Length >= minimum && result.Length <= maximum)
        result

    let private digits (raw: string) =
        raw.Length > 0 && Seq.forall (fun c -> c >= '0' && c <= '9') raw

    let private number minimum maximum (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Number)
        let raw = value.GetRawText()
        let mutable parsed = 0UL
        require (digits raw && (raw.Length = 1 || raw[0] <> '0'))
        require (UInt64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, &parsed))
        require (parsed >= minimum && parsed <= maximum)
        int parsed

    let private unsigned minimum value =
        let raw = text 1 20 value
        let mutable parsed = 0UL
        require (digits raw && (raw.Length = 1 || raw[0] <> '0'))
        require (UInt64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, &parsed))
        require (parsed >= minimum)
        parsed

    let private optional parse (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Null then
            None
        else
            Some(parse value)

    let private boolean (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False)
        value.GetBoolean()

    let private scope value =
        optional
            (fun value ->
                let raw = text 36 36 value
                let mutable guid = Guid.Empty

                require (
                    Guid.TryParseExact(raw, "D", &guid)
                    && guid <> Guid.Empty
                    && guid.ToString("D") = raw
                )

                raw)
            value

    let private address value =
        let raw = text 18 18 value
        require (raw.StartsWith("0x", StringComparison.Ordinal))

        require (
            raw.Substring(2)
            |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
        )

        UInt64.Parse(raw.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)

    let private settings value =
        fields [ "plotWidth"; "viewport"; "redaction"; "maxResults"; "maxElements" ] value
        let redact = field "redaction" value
        fields [ "addresses"; "strings"; "paths"; "labels" ] redact

        let viewport =
            field "viewport" value
            |> optional (fun view ->
                fields [ "start"; "size" ] view
                let start = field "start" view |> unsigned 0UL
                let size = field "size" view |> unsigned 0UL
                require (bigint start + bigint size <= bigint UInt64.MaxValue + 1I)
                { Start = start; Size = size })

        {
            MaxResults = field "maxResults" value |> number 1UL 4096UL
            Scene = {
                SceneOptions.defaults with
                    PlotWidth = field "plotWidth" value |> number 64UL 4096UL
                    Viewport = viewport
                    Redaction = {
                        Addresses = field "addresses" redact |> boolean
                        Strings = field "strings" redact |> boolean
                        Paths = field "paths" redact |> boolean
                        Labels = field "labels" redact |> boolean
                    }
                    Limits = {
                        SceneLimits.defaults with
                            MaxElements = field "maxElements" value |> number 1UL 1024UL
                    }
            }
        }

    let private document allowCoordinates action (bytes: byte array) =
        try
            require (bytes.Length > 0 && bytes.Length <= Protocol.MaxFrameBytes)
            require (not (bytes |> Array.exists (fun value -> value = 10uy || value = 13uy)))
            Protocol.utf8.GetString(bytes) |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 16))

            let unicode (raw: string) =
                let mutable index = 0

                while index < raw.Length do
                    if Char.IsHighSurrogate raw[index] then
                        require (index + 1 < raw.Length && Char.IsLowSurrogate raw[index + 1])
                        index <- index + 2
                    else
                        require (not (Char.IsLowSurrogate raw[index]))
                        index <- index + 1

            let rec tree (value: JsonElement) =
                match value.ValueKind with
                | JsonValueKind.Object ->
                    let seen = HashSet<string>(StringComparer.Ordinal)

                    for item in value.EnumerateObject() do
                        unicode item.Name
                        require (seen.Add(item.Name))
                        tree item.Value
                | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter tree
                | JsonValueKind.String -> unicode (value.GetString())
                | JsonValueKind.Number ->
                    if allowCoordinates then
                        require (Double.IsFinite(value.GetDouble()))
                    else
                        number 0UL 9007199254740991UL value |> ignore
                | _ -> ()

            require (document.RootElement.ValueKind = JsonValueKind.Object)
            tree document.RootElement
            action document.RootElement
        with
        | :? JsonException
        | :? Text.DecoderFallbackException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? KeyNotFoundException
        | :? FormatException
        | :? OverflowException -> invalid ()

    let parseInbound bytes =
        document
            false
            (fun root ->
                let version () =
                    field "version" root |> number 2UL 2UL |> ignore

                match field "tag" root |> text 1 32 with
                | "hello" ->
                    fields [ "tag"; "versions"; "extensions" ] root
                    let versions = field "versions" root
                    require (versions.ValueKind = JsonValueKind.Array)

                    if versions.GetArrayLength() <> 1 || versions[0].GetRawText() <> "2" then
                        raise (ProtocolFailure "UnsupportedVersion")

                    let extensions = field "extensions" root
                    require (extensions.ValueKind = JsonValueKind.Array)

                    if extensions.GetArrayLength() <> 0 then
                        raise (ProtocolFailure "UnsupportedExtension")

                    NativeInbound.Hello
                | "cancel" ->
                    fields [ "tag"; "version"; "requestId" ] root
                    version ()
                    NativeInbound.Cancel(field "requestId" root |> unsigned 1UL)
                | "shutdown" ->
                    fields [ "tag"; "version" ] root
                    version ()
                    NativeInbound.Shutdown
                | "request" ->
                    fields [ "tag"; "version"; "requestId"; "snapshotId"; "operation"; "args" ] root
                    version ()
                    let id = field "requestId" root |> unsigned 1UL
                    let snapshot = field "snapshotId" root |> scope
                    let args = field "args" root

                    let page () =
                        field "cursor" args |> optional (unsigned 0UL), field "pageSize" args |> number 1UL 32UL

                    let operation =
                        match field "operation" root |> text 1 64 with
                        | "snapshot.load" ->
                            require snapshot.IsNone
                            fields [ "path"; "dacPath"; "cachePath"; "allowNetwork" ] args

                            NativeOperation.Load(
                                field "path" args |> text 1 4096,
                                field "dacPath" args |> optional (text 1 4096),
                                field "cachePath" args |> optional (text 1 4096),
                                field "allowNetwork" args |> boolean
                            )
                        | operation ->
                            require snapshot.IsSome

                            match operation with
                            | "snapshot.dispose" ->
                                fields [] args
                                NativeOperation.Dispose
                            | "query.run" ->
                                fields [ "text"; "settings" ] args

                                NativeOperation.Run(
                                    field "text" args |> text 1 16384,
                                    field "settings" args |> settings
                                )
                            | "query.page" ->
                                fields [ "queryId"; "cursor"; "pageSize" ] args
                                let cursor, size = page ()
                                NativeOperation.Rows(field "queryId" args |> unsigned 1UL, cursor, size)
                            | "scene.page" ->
                                fields [ "sceneId"; "cursor"; "pageSize" ] args
                                let cursor, size = page ()
                                NativeOperation.Elements(field "sceneId" args |> unsigned 1UL, cursor, size)
                            | "details" ->
                                fields [ "runtime"; "address"; "cursor"; "pageSize" ] args
                                let cursor, size = page ()

                                NativeOperation.Details(
                                    field "runtime" args |> number 0UL (uint64 Int32.MaxValue),
                                    field "address" args |> address,
                                    cursor,
                                    size
                                )
                            | "export" ->
                                fields [ "sceneId"; "path" ] args

                                NativeOperation.Export(
                                    field "sceneId" args |> unsigned 1UL,
                                    field "path" args |> text 1 4096
                                )
                            | _ -> invalid ()

                    NativeInbound.Request {
                        Id = id
                        Snapshot = snapshot
                        Operation = operation
                    }
                | _ -> invalid ())
            bytes

    let validateOutbound bytes =
        document
            true
            (fun root ->
                match field "tag" root |> text 1 32 with
                | "fatal" -> fields [ "tag"; "code"; "message" ] root
                | tag ->
                    field "version" root |> number 2UL 2UL |> ignore

                    match tag with
                    | "ready" -> fields [ "tag"; "version"; "capabilities" ] root
                    | "bye" -> fields [ "tag"; "version" ] root
                    | "success" -> fields [ "tag"; "version"; "requestId"; "snapshotId"; "result" ] root
                    | "error" -> fields [ "tag"; "version"; "requestId"; "snapshotId"; "error" ] root
                    | "progress" -> fields [ "tag"; "version"; "requestId"; "snapshotId"; "phase"; "completed" ] root
                    | _ -> invalid ())
            bytes

    let serialize limit (value: obj) =
        use stream = new BoundedBuffer(limit)
        JsonSerializer.Serialize(stream, value, value.GetType())
        stream.ToArray()

    let encode value =
        let bytes = serialize Protocol.MaxFrameBytes value
        validateOutbound bytes
        bytes

    let ready () =
        encode {|
            tag = "ready"
            version = 2
            capabilities = {|
                backend = "native"
                operations = [|
                    "snapshot.load"
                    "snapshot.dispose"
                    "query.run"
                    "query.page"
                    "scene.page"
                    "details"
                    "export"
                |]
                limits = {|
                    maxFrameBytes = 65536
                    maxOutstanding = 8
                    maxPageSize = 32
                    maxSceneItems = 1024
                    maxResults = 4096
                    maxQueryLength = 16384
                    progressIntervalMs = 100
                |}
            |}
        |}

    let bye () = encode {| tag = "bye"; version = 2 |}

    let message =
        function
        | "OutputLimit" -> "Output exceeds the byte limit."
        | "IncompleteResult" -> "Only complete results can be exported."
        | "QueryNotFound" -> "Query is not current."
        | "SceneNotFound" -> "Scene is not current."
        | "SceneFailed" -> "The positioned scene could not be built."
        | "SceneTruncated" -> "The scene deadline prevented publication."
        | "SnapshotInvariant" -> "Snapshot detail relationships are inconsistent."
        | "FileNotFound" -> "The selected file does not exist."
        | "AccessDenied" -> "Access to the selected file was denied."
        | "UnsupportedTarget" -> "A matching supported worker host is required."
        | "InvalidDump" -> "The selected dump is invalid or unsupported."
        | "DacNotFound" -> "Select a trusted matching DAC or offline cache."
        | "DacLoadFailed" -> "The trusted DAC could not be loaded."
        | "HeapUnavailable" -> "The dump contains no usable heap."
        | "ReadFailed" -> "The selected file could not be read."
        | "ExportFailed" -> "The SVG could not be published to a new file."
        | code -> Protocol.message code

    let fatal code =
        encode {|
            tag = "fatal"
            code = code
            message = message code
        |}

    let error (request: NativeRequest) code =
        encode {|
            tag = "error"
            version = 2
            requestId = Protocol.idValue request.Id
            snapshotId = Option.toObj request.Snapshot
            error = {|
                code = code
                message = message code
                retryable = code = "Busy"
            |}
        |}

    let success (request: NativeRequest) scope (result: obj) =
        encode {|
            tag = "success"
            version = 2
            requestId = Protocol.idValue request.Id
            snapshotId = Option.toObj scope
            result = result
        |}

    let progress (request: NativeRequest) phase completed =
        encode {|
            tag = "progress"
            version = 2
            requestId = Protocol.idValue request.Id
            snapshotId = Option.toObj request.Snapshot
            phase = phase
            completed = Protocol.idValue completed
        |}
