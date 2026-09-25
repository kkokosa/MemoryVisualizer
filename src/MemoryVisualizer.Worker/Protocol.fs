namespace MemoryVisualizer.Worker

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

exception ProtocolFailure of string

type Operation =
    | Capabilities
    | Load of delayMs: int
    | Dispose
    | Query of pageSize: int * cursor: uint64 option
    | Details of objectId: uint64 * pageSize: int * cursor: uint64 option
    | Scene of maxItems: int
    | ValidateRecipe
    | Export

type Request = {
    Id: uint64
    Snapshot: string option
    Operation: Operation
}

type Inbound =
    | Hello of versions: int64 array * extensions: string array
    | Request of Request
    | Cancel of uint64
    | Shutdown

[<RequireQualifiedAccess>]
module Protocol =
    [<Literal>]
    let MaxFrameBytes = 65536

    [<Literal>]
    let MaxOutstanding = 8

    let utf8 = UTF8Encoding(false, true)

    let operations = [|
        "capabilities"
        "snapshot.load"
        "snapshot.dispose"
        "query"
        "scene"
        "details"
        "recipe.validate"
        "export"
    |]

    let capabilities = {|
        backend = "fake"
        operations = operations
        extensions = Array.empty<string>
        limits = {|
            maxFrameBytes = MaxFrameBytes
            maxOutstanding = MaxOutstanding
            maxPageSize = 128
            maxSceneItems = 128
            progressIntervalMs = 100
        |}
    |}

    let private invalid () = raise (ProtocolFailure "InvalidFrame")

    let private require condition =
        if not condition then
            invalid ()

    let private fields (names: string list) (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)
        let actual = value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
        require (actual = Set.ofList names)

    let private field (name: string) (value: JsonElement) = value.GetProperty(name)

    let private scalarLength (text: string) =
        let mutable index = 0
        let mutable count = 0

        while index < text.Length do
            if Char.IsHighSurrogate text[index] then
                require (index + 1 < text.Length && Char.IsLowSurrogate text[index + 1])
                index <- index + 2
            else
                require (not (Char.IsLowSurrogate text[index]))
                index <- index + 1

            count <- count + 1

        count

    let private text minLength maxLength (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.String)
        let result = value.GetString()
        let length = scalarLength result
        require (length >= minLength && length <= maxLength)
        result

    let private literal expected value = require (text 0 4096 value = expected)

    let private boolValue (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False)

    let private digits (value: string) =
        value.Length > 0 && value |> Seq.forall (fun c -> c >= '0' && c <= '9')

    let private integer minimum maximum (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Number)
        let raw = value.GetRawText()
        require (digits raw)
        let mutable parsed = 0L
        require (Int64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, &parsed))
        require (parsed >= minimum && parsed <= maximum)
        parsed

    let private decimalString minimum value =
        let raw = text 1 20 value
        require (digits raw && (raw.Length = 1 || raw[0] <> '0'))
        let mutable parsed = 0UL
        require (UInt64.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, &parsed))
        require (parsed >= minimum)
        parsed

    let private cursor (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Null then
            None
        else
            Some(decimalString 0UL value)

    let private snapshot (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Null then
            None
        else
            let raw = text 36 36 value
            let mutable parsed = Guid.Empty
            require (Guid.TryParseExact(raw, "D", &parsed))
            require (parsed <> Guid.Empty && raw = parsed.ToString("D"))
            Some raw

    let private array maximum (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Array && value.GetArrayLength() <= maximum)
        value.EnumerateArray() |> Seq.toArray

    let private version value = integer 1L 1L value |> ignore

    let private pageArgs value =
        let size = field "pageSize" value |> integer 1L 128L |> int
        let position = field "cursor" value |> cursor
        size, position

    let private parseRequest value =
        fields [ "tag"; "version"; "requestId"; "snapshotId"; "operation"; "args" ] value
        field "version" value |> version
        let id = field "requestId" value |> decimalString 1UL
        let scope = field "snapshotId" value |> snapshot
        let args = field "args" value

        let operation =
            match field "operation" value |> text 1 64 with
            | "capabilities" ->
                require scope.IsNone
                fields [] args
                Capabilities
            | "snapshot.load" ->
                require scope.IsNone
                fields [ "source"; "delayMs" ] args
                field "source" args |> literal "fixture:tiny"
                Load(field "delayMs" args |> integer 0L 5000L |> int)
            | "snapshot.dispose" ->
                require scope.IsSome
                fields [] args
                Dispose
            | "query" ->
                require scope.IsSome
                fields [ "text"; "pageSize"; "cursor" ] args
                field "text" args |> text 1 4096 |> ignore
                Query(pageArgs args)
            | "details" ->
                require scope.IsSome
                fields [ "objectId"; "pageSize"; "cursor" ] args
                let objectId = field "objectId" args |> decimalString 1UL
                let size, position = pageArgs args
                Details(objectId, size, position)
            | "scene" ->
                require scope.IsSome
                fields [ "maxItems" ] args
                Scene(field "maxItems" args |> integer 1L 128L |> int)
            | "recipe.validate" ->
                require scope.IsNone
                fields [ "recipe" ] args
                let recipe = field "recipe" args
                fields [ "schemaVersion"; "query" ] recipe
                field "schemaVersion" recipe |> version
                field "query" recipe |> text 1 4096 |> ignore
                ValidateRecipe
            | "export" ->
                require scope.IsSome
                fields [ "format"; "maxBytes" ] args
                field "format" args |> literal "svg"
                field "maxBytes" args |> integer 1L 32768L |> ignore
                Export
            | _ -> invalid ()

        {
            Id = id
            Snapshot = scope
            Operation = operation
        }

    let private parseInboundElement (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)

        match field "tag" value |> text 1 32 with
        | "hello" ->
            fields [ "tag"; "versions"; "extensions" ] value

            let versions =
                field "versions" value |> array 8 |> Array.map (integer 1L 9007199254740991L)

            require (versions.Length > 0 && (Array.distinct versions).Length = versions.Length)
            let extensions = field "extensions" value |> array 8 |> Array.map (text 1 64)
            require ((Array.distinct extensions).Length = extensions.Length)

            for extension in extensions do
                require (Regex.IsMatch(extension, "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+\\z"))

            Hello(versions, extensions)
        | "request" -> Request(parseRequest value)
        | "cancel" ->
            fields [ "tag"; "version"; "requestId" ] value
            field "version" value |> version
            Cancel(field "requestId" value |> decimalString 1UL)
        | "shutdown" ->
            fields [ "tag"; "version" ] value
            field "version" value |> version
            Shutdown
        | _ -> invalid ()

    let private validateCapabilities value =
        fields [ "backend"; "operations"; "extensions"; "limits" ] value
        field "backend" value |> literal "fake"

        let actual =
            field "operations" value |> array operations.Length |> Array.map (text 1 64)

        require (actual = operations)
        field "extensions" value |> array 0 |> ignore
        let limits = field "limits" value

        fields
            [
                "maxFrameBytes"
                "maxOutstanding"
                "maxPageSize"
                "maxSceneItems"
                "progressIntervalMs"
            ]
            limits

        for name, expected in
            [
                "maxFrameBytes", 65536L
                "maxOutstanding", 8L
                "maxPageSize", 128L
                "maxSceneItems", 128L
                "progressIntervalMs", 100L
            ] do
            field name limits |> integer expected expected |> ignore

    let private validateItem value =
        fields [ "objectId"; "address"; "size" ] value
        field "objectId" value |> decimalString 1UL |> ignore
        let address = field "address" value |> text 18 18
        require (Regex.IsMatch(address, "^0x[0-9a-f]{16}\\z"))
        field "size" value |> decimalString 0UL |> ignore

    let private validateResult (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)

        match field "tag" value |> text 1 32 with
        | "capabilities" ->
            fields [ "tag"; "value" ] value
            field "value" value |> validateCapabilities
        | "snapshot" ->
            fields [ "tag"; "objectCount" ] value
            field "objectCount" value |> decimalString 0UL |> ignore
        | "disposed" -> fields [ "tag" ] value
        | "page" ->
            fields [ "tag"; "items"; "nextCursor"; "truncated" ] value
            field "items" value |> array 128 |> Array.iter validateItem
            field "nextCursor" value |> cursor |> ignore
            field "truncated" value |> boolValue
        | "scene" ->
            fields [ "tag"; "schemaVersion"; "items"; "truncated" ] value
            field "schemaVersion" value |> version
            field "items" value |> array 128 |> Array.iter validateItem
            field "truncated" value |> boolValue
        | "recipe" ->
            fields [ "tag"; "schemaVersion"; "valid" ] value
            field "schemaVersion" value |> version
            require ((field "valid" value).ValueKind = JsonValueKind.True)
        | "export" ->
            fields [ "tag"; "format"; "artifactId"; "byteLength" ] value
            field "format" value |> literal "svg"
            field "artifactId" value |> literal "fixture:svg"
            field "byteLength" value |> decimalString 0UL |> ignore
        | _ -> invalid ()

    let private validateOutboundElement (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)

        let correlation () =
            field "version" value |> version
            field "requestId" value |> decimalString 1UL |> ignore
            field "snapshotId" value |> snapshot |> ignore

        match field "tag" value |> text 1 32 with
        | "ready" ->
            fields [ "tag"; "version"; "capabilities" ] value
            field "version" value |> version
            field "capabilities" value |> validateCapabilities
        | "bye" ->
            fields [ "tag"; "version" ] value
            field "version" value |> version
        | "fatal" ->
            fields [ "tag"; "code"; "message" ] value
            let code = field "code" value |> text 1 64

            require (
                List.contains code [
                    "UnsupportedVersion"
                    "UnsupportedExtension"
                    "InvalidFrame"
                    "ProtocolViolation"
                    "TransportTimeout"
                    "InternalError"
                ]
            )

            field "message" value |> text 1 256 |> ignore
        | "progress" ->
            fields [ "tag"; "version"; "requestId"; "snapshotId"; "phase"; "completed"; "total" ] value
            correlation ()
            field "phase" value |> literal "working"
            field "completed" value |> integer 0L 100L |> ignore
            field "total" value |> integer 100L 100L |> ignore
        | "error" ->
            fields [ "tag"; "version"; "requestId"; "snapshotId"; "error" ] value
            correlation ()
            let error = field "error" value
            fields [ "code"; "message"; "retryable" ] error
            let code = field "code" error |> text 1 64

            require (
                List.contains code [
                    "Busy"
                    "Cancelled"
                    "SnapshotNotFound"
                    "InvalidRequest"
                    "NotImplemented"
                    "InternalError"
                    "StaleSnapshot"
                    "WorkerExited"
                    "Timeout"
                    "ProtocolError"
                ]
            )

            field "message" error |> text 1 256 |> ignore
            field "retryable" error |> boolValue
        | "success" ->
            fields [ "tag"; "version"; "requestId"; "snapshotId"; "result" ] value
            correlation ()
            let result = field "result" value
            validateResult result
            let scope = field "snapshotId" value |> snapshot

            match field "tag" result |> text 1 32 with
            | "capabilities"
            | "recipe" -> require scope.IsNone
            | _ -> require scope.IsSome
        | _ -> invalid ()

    let private withDocument validator (bytes: byte array) =
        try
            require (bytes.Length > 0 && bytes.Length <= MaxFrameBytes)
            require (not (bytes |> Array.exists (fun b -> b = 10uy || b = 13uy)))
            utf8.GetString(bytes) |> ignore

            use document =
                JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), JsonDocumentOptions(MaxDepth = 16))

            require (document.RootElement.ValueKind = JsonValueKind.Object)

            let rec validateTree (value: JsonElement) =
                match value.ValueKind with
                | JsonValueKind.Object ->
                    let names = HashSet<string>(StringComparer.Ordinal)

                    for property in value.EnumerateObject() do
                        scalarLength property.Name |> ignore
                        require (names.Add property.Name)
                        validateTree property.Value
                | JsonValueKind.Array ->
                    for child in value.EnumerateArray() do
                        validateTree child
                | JsonValueKind.String -> value.GetString() |> scalarLength |> ignore
                | JsonValueKind.Number -> integer 0L 9007199254740991L value |> ignore
                | _ -> ()

            validateTree document.RootElement
            validator document.RootElement
        with
        | :? JsonException
        | :? DecoderFallbackException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? KeyNotFoundException -> invalid ()

    let validateJson bytes = withDocument ignore bytes

    let parseInbound bytes = withDocument parseInboundElement bytes

    let validateOutbound bytes =
        withDocument validateOutboundElement bytes

    let private encode (value: obj) =
        let bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType())
        validateOutbound bytes
        bytes

    let scopeValue (scope: string option) = Option.toObj scope

    let idValue (id: uint64) =
        id.ToString(CultureInfo.InvariantCulture)

    let ready () =
        encode {|
            tag = "ready"
            version = 1
            capabilities = capabilities
        |}

    let bye () = encode {| tag = "bye"; version = 1 |}

    let message =
        function
        | "UnsupportedVersion" -> "No supported protocol version."
        | "UnsupportedExtension" -> "Requested extension is not supported."
        | "InvalidFrame" -> "Invalid protocol frame."
        | "ProtocolViolation" -> "Protocol sequence violation."
        | "TransportTimeout" -> "Transport deadline exceeded."
        | "Busy" -> "Request limit reached."
        | "Cancelled" -> "Request cancelled."
        | "SnapshotNotFound" -> "Snapshot is not active."
        | "InvalidRequest" -> "Invalid request."
        | _ -> "Internal worker error."

    let fatal code =
        encode {|
            tag = "fatal"
            code = code
            message = message code
        |}

    let error (request: Request) code =
        encode {|
            tag = "error"
            version = 1
            requestId = idValue request.Id
            snapshotId = scopeValue request.Snapshot
            error = {|
                code = code
                message = message code
                retryable = code = "Busy"
            |}
        |}

    let success (request: Request) (scope: string option) (result: obj) =
        encode {|
            tag = "success"
            version = 1
            requestId = idValue request.Id
            snapshotId = scopeValue scope
            result = result
        |}

    let progress (request: Request) (completed: int) =
        encode {|
            tag = "progress"
            version = 1
            requestId = idValue request.Id
            snapshotId = scopeValue request.Snapshot
            phase = "working"
            completed = completed
            total = 100
        |}
