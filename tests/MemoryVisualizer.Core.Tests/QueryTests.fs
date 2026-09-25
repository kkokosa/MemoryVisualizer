module MemoryVisualizer.Core.Tests.QueryTests

open System
open System.Threading
open MemoryVisualizer.Core
open MemoryVisualizer.Query
open Xunit

let private unwrap =
    function
    | Ok value -> value
    | Error error -> failwithf "%A" error

let private fixture = IndexedHeapSnapshotTests.fixture
let private token = CancellationToken.None
let private defaults = QueryLimits.defaults
let private context () = QueryExecutionContext.create token

let private build snapshot =
    IndexedHeapSnapshot.Create(snapshot, SnapshotIndexLimits.defaults, token)
    |> unwrap

let private compile limits source =
    Mql.compile limits (fixture ()).Metadata.Id source |> unwrap

let private runWith limits source =
    use store = build (fixture ())
    Mql.execute limits (compile limits source) store (context ())

let private run source = runWith defaults source

let private addresses result =
    result.Rows
    |> List.map (fun row -> row.Entity.Runtime.Index, row.Entity.Address)

let private allObjects = "MATCH (o: Object) RETURN o"

let private complete (result: QueryResult) =
    Assert.Equal(QueryStatus.Complete, result.Status)

let private truncated reasons (result: QueryResult) =
    Assert.Equal(QueryStatus.Truncated reasons, result.Status)

let private diagnostic source =
    match Mql.compile defaults (fixture ()).Metadata.Id source with
    | Error [ error ] -> error
    | result -> failwithf "Expected one diagnostic: %A" result

let private failure code result =
    match result.Status with
    | QueryStatus.Failed [ error ] -> Assert.Equal(code, error.Code)
    | status -> failwithf "Expected failure %s, got %A" code status

[<Fact>]
let ``Documented composition has exact typed plans and rows with one shared address system`` () =
    let source =
        "MATCH (seg: Segment) RETURN seg;\n"
        + "MATCH (gen: Generation) RETURN gen.Generation AS BOX (Label = gen.Generation, LabelPosition = InnerCenter, Background = Grey);\n"
        + "MATCH (obj: Object) WHERE obj.Generation = 2 AND obj.Type = \"Same.Display.Name\" RETURN obj.Address, obj.Size AS PIN;\n"
        + "DRAW Memory(0x100, 0x200, Runtime = 0, Heap = 0, Width = 16)"

    let plan = compile defaults source
    Assert.Equal(4, plan.PlannedStatements.Length)

    match plan.PlannedStatements with
    | [ PlannedStatement.Select(segments, None)
        PlannedStatement.Select(generations, Some boxes)
        PlannedStatement.Select(objects, Some pins)
        PlannedStatement.Draw(memory, 16) ] ->
        Assert.Equal(Selector.Segment, segments.Selector)
        Assert.Equal<(string * BoundExpression) list>([ "seg", BoundExpression.Entity ], segments.Projection)
        Assert.Equal(Selector.Generation, generations.Selector)
        Assert.Equal(Some(BoundExpression.Property Property.Generation), boxes.Label)
        Assert.Equal(LabelPosition.InnerCenter, boxes.LabelPosition)
        Assert.Equal("Grey", boxes.Background)
        Assert.Equal(DrawingKind.Box, boxes.Kind)
        Assert.Equal(Selector.Object, objects.Selector)

        Assert.Equal(
            Some(
                BoundPredicate.And [
                    BoundPredicate.Compare(Property.Generation, Comparison.Equal, Literal.Unsigned 2UL)
                    BoundPredicate.Compare(Property.Type, Comparison.Equal, Literal.Text "Same.Display.Name")
                ]
            ),
            objects.Predicate
        )

        Assert.Equal(DrawingKind.Pin, pins.Kind)
        Assert.Equal(0x100UL, memory.Start.Value)
        Assert.Equal(0x200UL, memory.End.Value)
    | other -> failwithf "Unexpected plan %A" other

    let result = run source
    complete result
    Assert.Equal(10, result.Rows.Length)
    Assert.Equal<int list>([ 0; 0; 0; 0; 1; 1; 1; 1; 2; 2 ], result.Rows |> List.map _.StatementIndex)

    Assert.Equal<(int * uint64) list>(
        [ 0, 0x100UL; 0, 0x300UL; 1, 0x100UL; 1, 9007199254740993UL ],
        result.Rows
        |> List.take 4
        |> List.map (fun row -> row.Entity.Runtime.Index, row.Entity.Address)
    )

    Assert.All(
        result.Rows |> List.skip 4 |> List.take 4,
        fun row -> Assert.Equal<(string * QueryValue) list>([ "Generation", QueryValue.Unsigned 0UL ], row.Values)
    )

    Assert.Equal<(int * uint64) list>(
        [ 0, 0x140UL; 0, 0x170UL ],
        result
        |> fun value ->
            {
                value with
                    Rows = List.skip 8 value.Rows
            }
            |> addresses
    )

    Assert.Equal(7, result.Directives.Length)

    Assert.Equal<DrawingKind list>(
        [
            DrawingKind.Box
            DrawingKind.Box
            DrawingKind.Box
            DrawingKind.Box
            DrawingKind.Pin
            DrawingKind.Pin
            DrawingKind.Memory
        ],
        result.Directives |> List.map _.Kind
    )

    Assert.Equal(0x100UL, result.Directives[6].Size)
    Assert.Equal(16, result.Directives[6].Width)
    Assert.Equal(22, result.Candidates)

[<Fact>]
let ``Documented free entries and overlap examples have exact results`` () =
    let free =
        run
            "MATCH (obj: Object) WHERE obj.IsFree = true RETURN obj AS BOX (Background = Yellow); DRAW Memory(0x100, 0x400, Runtime = 0, Heap = 0)"

    complete free
    Assert.Equal<(int * uint64) list>([ 0, 0x140UL ], addresses free)
    Assert.Equal("Yellow", free.Directives[0].Background)
    Assert.Equal(0x300UL, free.Directives[1].Size)

    let overlap =
        run "MATCH (obj: Object) WHERE obj.Address OVERLAPS [0x125, 0x130) RETURN obj.Address, obj.MethodTable"

    complete overlap
    Assert.Equal<(int * uint64) list>([ 0, 0x110UL; 0, 0x120UL ], addresses overlap)

    Assert.Equal<(string * QueryValue) list>(
        [
            "Address", QueryValue.Unsigned 0x110UL
            "MethodTable", QueryValue.Unsigned 11UL
        ],
        overlap.Rows[0].Values
    )

    Assert.Equal<(string * QueryValue) list>(
        [
            "Address", QueryValue.Unsigned 0x120UL
            "MethodTable", QueryValue.Unsigned 22UL
        ],
        overlap.Rows[1].Values
    )

[<Theory>]
[<InlineData("o.Address STARTS IN [0x120, 0x140)", 1)>]
[<InlineData("o.Address OVERLAPS [0x120, 0x140)", 2)>]
[<InlineData("o.Address OVERLAPS [0x140, 0x140)", 0)>]
[<InlineData("o.Address STARTS IN [0x140, 0x140)", 0)>]
[<InlineData("o.Address OVERLAPS [0x130, 0x140)", 1)>]
[<InlineData("o.Type = \"Same.Display.Name\"", 9)>]
[<InlineData("o.Type != \"Same.Display.Name\"", 0)>]
[<InlineData("o.MethodTable = 11", 7)>]
[<InlineData("o.Runtime = 1", 4)>]
[<InlineData("o.Heap = 1", 4)>]
[<InlineData("o.HeapKind = \"Frozen\"", 3)>]
[<InlineData("o.Generation != 0", 3)>]
[<InlineData("o.Generation > 0", 3)>]
[<InlineData("o.Generation >= 1", 3)>]
[<InlineData("o.Size < 8", 1)>]
[<InlineData("o.Size <= 8", 3)>]
[<InlineData("o.Address = 18446744073709551615", 1)>]
[<InlineData("o.Address = 0xffffffffffffffff", 1)>]
[<InlineData("(o.Runtime = 1 AND (o.Heap = 0))", 1)>]
let ``Predicates obey typed null and half-open semantics`` predicate count =
    let result = run $"MATCH (o: Object) WHERE {predicate} RETURN o.Address"
    complete result
    Assert.Equal(count, result.Rows.Length)

[<Fact>]
let ``Empty missing values type identities and object ends remain lossless`` () =
    let snapshot = fixture ()

    use store =
        build {
            snapshot with
                Types =
                    snapshot.Types
                    |> Array.map (fun typ ->
                        if typ.Identity.MethodTable = 22UL then
                            { typ with Name = None }
                        else
                            typ)
        }

    let execute text =
        Mql.execute defaults (compile defaults text) store (context ())

    let missing = execute "MATCH (o: Object) WHERE o.MethodTable = 22 RETURN o.Type"
    Assert.Equal(QueryValue.Missing, snd missing.Rows[0].Values[0])
    Assert.Empty((execute "MATCH (o: Object) WHERE o.MethodTable = 22 AND o.Type != \"A\" RETURN o").Rows)

    let sameNames =
        run "MATCH (o: Object) WHERE o.Type = \"Same.Display.Name\" RETURN o.MethodTable"

    Assert.Equal(4, sameNames.Rows |> List.map _.Entity.TypeIdentity |> Set.ofList |> Set.count)

    let last =
        run "MATCH (o: Object) WHERE o.Address = 18446744073709551615 RETURN o.Address, o.Size AS BOX"

    Assert.Equal(UInt64.MaxValue, last.Directives[0].Address)
    Assert.Equal(1UL, last.Directives[0].Size)
    Assert.Equal(None, last.Rows[0].Entity.End)

    let adjacent =
        run "MATCH (o: Object) WHERE o.Address OVERLAPS [18446744073709551614, 18446744073709551615) RETURN o.Address"

    Assert.Equal<(int * uint64) list>([ 1, UInt64.MaxValue - 15UL ], addresses adjacent)

[<Fact>]
let ``Selection projection and presentation are independent typed bindings`` () =
    let result =
        run
            "mAtCh (o: OBJECT) WhErE o.Address = 0x110 aNd o.Runtime = 1 RETURN o.Size,o.Address,o.Size AS PIN (Label=o.Type,Background=\"#ab12Cd\",Width=3,LabelPosition=OuterLeft)"

    complete result

    Assert.Equal<(string * QueryValue) list>(
        [
            "Size", QueryValue.Unsigned 16UL
            "Address", QueryValue.Unsigned 0x110UL
            "Size", QueryValue.Unsigned 16UL
        ],
        result.Rows[0].Values
    )

    Assert.Equal(Some(QueryValue.Text "Same.Display.Name"), result.Directives[0].Label)
    Assert.Equal("#AB12CD", result.Directives[0].Background)
    Assert.Equal(LabelPosition.OuterLeft, result.Directives[0].LabelPosition)

    let literals = [
        "\"escaped\\n\\\"quote\\\"\"", QueryValue.Text "escaped\n\"quote\""
        "true", QueryValue.Boolean true
        "123", QueryValue.Unsigned 123UL
    ]

    for label, expected in literals do
        let result =
            run $"MATCH (s: Segment) WHERE s.Runtime = 0 AND s.Heap = 0 RETURN s.End AS BOX (Label = {label})"

        Assert.Equal(Some expected, result.Directives[0].Label)
        Assert.Equal(QueryValue.Unsigned 0x200UL, snd result.Rows[0].Values[0])

[<Theory>]
[<InlineData("MATCH (o:Object) RETURN o.Bogus", "Bogus", "MQL203")>]
[<InlineData("MATCH (o:Object) RETURN x.Address", "x", "MQL202")>]
[<InlineData("MATCH (o:Person) RETURN o", "Person", "MQL201")>]
[<InlineData("MATCH (o:Object) WHERE o.Size = \"1\" RETURN o", "\"1\"", "MQL208")>]
[<InlineData("MATCH (o:Object) WHERE o.Type > \"A\" RETURN o", ">", "MQL208")>]
[<InlineData("MATCH (o:Object) WHERE o.Runtime = 2147483648 RETURN o", "2147483648", "MQL204")>]
[<InlineData("MATCH (o:Object) WHERE o.Address OVERLAPS [2,1) RETURN o", "1", "MQL205")>]
[<InlineData("MATCH (o:Object) WHERE o.Size OVERLAPS [1,2) RETURN o", "Size", "MQL207")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Background=Purple)", "Purple", "MQL211")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Width=0)", "0", "MQL206")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Width=4097)", "4097", "MQL206")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (White=Grey)", "White", "MQL210")>]
[<InlineData("MATCH (o:Object) RETURN o.End", "End", "MQL203")>]
[<InlineData("MATCH (o:Object) WHERE o.Address = 0xZZ RETURN o", "0xZZ", "MQL103")>]
[<InlineData("MATCH (o:Object) WHERE o.Address = 0x RETURN o", "0x", "MQL103")>]
[<InlineData("MATCH (o:Object) WHERE o.Address = 18446744073709551616 RETURN o", "18446744073709551616", "MQL103")>]
[<InlineData("MATCH (o:Object) WHERE o.Address = 0x10000000000000000 RETURN o", "0x10000000000000000", "MQL103")>]
[<InlineData("MATCH (o:Object) RETURN o AS DOT", "DOT", "MQL120")>]
[<InlineData("MATCH (o:Object)-[*]->(b:Object) RETURN o", "-", "MQL120")>]
[<InlineData("MATCH (o:Object) WHERE o.Size=1 OR o.Size=2 RETURN o", "OR", "MQL120")>]
let ``Diagnostics highlight the actual invalid token`` source text code =
    let error = diagnostic source
    Assert.Equal(code, error.Code)
    Assert.Equal(text, source.Substring(error.Span.Offset, error.Span.Length))
    Assert.Equal(1, error.Span.Line)
    Assert.Equal(error.Span.Offset + 1, error.Span.Column)
    Assert.False(String.IsNullOrWhiteSpace error.Message)

[<Theory>]
[<InlineData("MATCH (gen Generation) RETURN gen")>]
[<InlineData("MATCH (o:Object) RETURN o; MATCH (g:Generation) RETURN o")>]
[<InlineData("MATCH (o:Object) WHERE o.Address=-1 RETURN o")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Background=hash(o.Type))")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Label=o)")>]
[<InlineData("MATCH (o:Object) RETURN o AS BOX (Width=1,Width=2)")>]
[<InlineData("MATCH (o:Object) RETURN 1")>]
[<InlineData("MATCH (o:Object) WHERE o=1 RETURN o")>]
[<InlineData("MATCH (o:Object) RETURN o MATCH (s:Segment) RETURN s")>]
[<InlineData("DRAW Memory(1,2,Runtime=0,Heap=0,Width=1M)")>]
[<InlineData("DRAW Memory(1,2)")>]
[<InlineData("DRAW Memory(2,1,Runtime=0,Heap=0)")>]
[<InlineData("DRAW Memory(1,2,Runtime=0,Heap=0,Width=0)")>]
let ``Ambiguous legacy and unsupported expressions never execute`` source = diagnostic source |> ignore

[<Fact>]
let ``UTF16 CR LF CRLF and EOF spans are exact`` () =
    for newline in [ "\r\n"; "\n"; "\r" ] do
        let source =
            "MATCH (o:Object) WHERE o.Type = \"\U0001F642\" AND"
            + newline
            + "o.Unknown = 1 RETURN o"

        let error = diagnostic source
        Assert.Equal(source.IndexOf("Unknown", StringComparison.Ordinal), error.Span.Offset)
        Assert.Equal(2, error.Span.Line)
        Assert.Equal(3, error.Span.Column)
        Assert.Equal(7, error.Span.Length)

    let oneLine =
        "MATCH (o:Object) WHERE o.Type = \"\U0001F642\" AND o.Unknown=1 RETURN o"

    let error = diagnostic oneLine
    Assert.Equal(oneLine.IndexOf("Unknown", StringComparison.Ordinal) + 1, error.Span.Column)
    let eof = "MATCH (o:Object)\r\nRETURN "
    let error = diagnostic eof
    Assert.Equal(eof.Length, error.Span.Offset)
    Assert.Equal(0, error.Span.Length)
    Assert.Equal(2, error.Span.Line)
    Assert.Equal(8, error.Span.Column)

[<Fact>]
let ``Every configured limit rejects zero negative and above-ceiling settings`` () =
    let setters = [
        (fun n -> { defaults with MaxQueryLength = n }), defaults.MaxQueryLength
        (fun n -> { defaults with MaxTokens = n }), defaults.MaxTokens
        (fun n -> { defaults with MaxNesting = n }), defaults.MaxNesting
        (fun n -> { defaults with MaxStatements = n }), defaults.MaxStatements
        (fun n -> {
            defaults with
                MaxProjectionColumns = n
        }),
        defaults.MaxProjectionColumns
        (fun n -> { defaults with MaxResults = n }), defaults.MaxResults
        (fun n -> { defaults with MaxDirectives = n }), defaults.MaxDirectives
        (fun n -> { defaults with MaxCandidates = n }), defaults.MaxCandidates
        (fun n -> {
            defaults with
                MaxElapsedMilliseconds = n
        }),
        defaults.MaxElapsedMilliseconds
    ]

    use store = build (fixture ())
    let plan = compile defaults allObjects

    for setter, ceiling in setters do
        for n in [ -1; 0; ceiling + 1 ] do
            let limits = setter n
            Assert.True(Result.isError (Mql.parse limits allObjects))
            Mql.execute limits plan store (context ()) |> failure "MQL001"

        Assert.Equal(Ok(), QueryLimits.validate (setter ceiling))

[<Fact>]
let ``Parser caps are inclusive below at and above actual thresholds`` () =
    let sources = [
        allObjects, (fun n -> { defaults with MaxQueryLength = n }), allObjects.Length
        allObjects, (fun n -> { defaults with MaxTokens = n }), 8
        "MATCH (o:Object) WHERE ((o.Size=1)) RETURN o", (fun n -> { defaults with MaxNesting = n }), 2
        allObjects + ";" + allObjects, (fun n -> { defaults with MaxStatements = n }), 2
        "MATCH (o:Object) RETURN o,o.Size",
        (fun n -> {
            defaults with
                MaxProjectionColumns = n
        }),
        2
    ]

    for source, setter, exact in sources do
        Assert.True(Result.isError (Mql.parse (setter (exact - 1)) source))
        Assert.True(Result.isOk (Mql.parse (setter exact) source))
        Assert.True(Result.isOk (Mql.parse (setter (exact + 1)) source))

    let sixtyFour =
        "MATCH (o:Object) RETURN " + String.concat "," (List.replicate 64 "o.Size")

    Assert.True(Result.isOk (Mql.prepare defaults sixtyFour))
    Assert.True(Result.isError (Mql.prepare defaults (sixtyFour + ",o.Size")))

[<Fact>]
let ``Rows use exact matching lookahead and shared statement budgets`` () =
    runWith { defaults with MaxResults = 8 } allObjects
    |> truncated [ TruncationReason.Results ]

    runWith { defaults with MaxResults = 9 } allObjects |> complete
    runWith { defaults with MaxResults = 10 } allObjects |> complete
    let bounded = runWith { defaults with MaxResults = 8 } allObjects
    Assert.Equal(8, bounded.Rows.Length)
    Assert.Equal(9, bounded.Candidates)
    let sparse = "MATCH (o:Object) WHERE o.Generation=2 RETURN o"
    runWith { defaults with MaxResults = 2 } sparse |> complete

    let shared =
        runWith { defaults with MaxResults = 10 } (allObjects + ";" + allObjects)

    truncated [ TruncationReason.Results ] shared
    Assert.Equal(10, shared.Rows.Length)
    Assert.Equal(11, shared.Candidates)

    let exactShared =
        runWith { defaults with MaxResults = 18 } (allObjects + ";" + allObjects)

    complete exactShared

[<Fact>]
let ``Directive bounds cover BOX PIN DRAW and report simultaneous cutoffs atomically`` () =
    let boxes = allObjects + " AS BOX"

    let full =
        runWith
            {
                defaults with
                    MaxResults = 8
                    MaxDirectives = 8
            }
            boxes

    truncated [ TruncationReason.Results; TruncationReason.Directives ] full
    Assert.Equal(8, full.Rows.Length)
    Assert.Equal(8, full.Directives.Length)
    runWith { defaults with MaxDirectives = 9 } boxes |> complete
    runWith { defaults with MaxDirectives = 10 } boxes |> complete
    let draw = "DRAW Memory(1,2,Runtime=0,Heap=0)"

    let mixed =
        runWith { defaults with MaxDirectives = 9 } (draw + ";" + allObjects + " AS PIN")

    truncated [ TruncationReason.Directives ] mixed
    Assert.Equal(8, mixed.Rows.Length)
    Assert.Equal(9, mixed.Directives.Length)
    runWith { defaults with MaxDirectives = 1 } draw |> complete

    runWith { defaults with MaxDirectives = 1 } (draw + ";" + draw)
    |> truncated [ TruncationReason.Directives ]

    runWith { defaults with MaxDirectives = 1 } (draw + ";DRAW Memory(2,2,Runtime=0,Heap=0)")
    |> complete

    let unknown = run (draw + ";DRAW Memory(1,2,Runtime=99,Heap=0)")
    failure "MQL303" unknown
    Assert.Empty unknown.Directives
    Assert.Empty unknown.Rows

[<Fact>]
let ``Work counts nonmatches generations lane lookups and cutoff lookahead`` () =
    let none = "MATCH (o:Object) WHERE o.Type=\"Absent\" RETURN o"

    runWith { defaults with MaxCandidates = 8 } none
    |> truncated [ TruncationReason.Candidates ]

    runWith { defaults with MaxCandidates = 9 } none |> complete
    runWith { defaults with MaxCandidates = 10 } none |> complete

    let unproven =
        runWith
            {
                defaults with
                    MaxResults = 8
                    MaxCandidates = 8
            }
            allObjects

    truncated [ TruncationReason.Candidates ] unproven
    Assert.Equal(8, unproven.Rows.Length)
    Assert.Equal(8, unproven.Candidates)

    runWith { defaults with MaxCandidates = 7 } "MATCH (g:Generation) RETURN g"
    |> truncated [ TruncationReason.Candidates ]

    runWith { defaults with MaxCandidates = 8 } "MATCH (g:Generation) RETURN g"
    |> complete

    let shared =
        runWith { defaults with MaxCandidates = 10 } (allObjects + ";MATCH (s:Segment) RETURN s")

    truncated [ TruncationReason.Candidates ] shared
    Assert.Equal(10, shared.Rows.Length)

    runWith { defaults with MaxCandidates = 1 } "DRAW Memory(1,2,Runtime=0,Heap=0)"
    |> complete

    runWith { defaults with MaxCandidates = 1 } "DRAW Memory(1,2,Runtime=1,Heap=1)"
    |> truncated [ TruncationReason.Candidates ]

type private ManualClock() =
    inherit TimeProvider()
    member val Timestamp = 0L with get, set
    override _.TimestampFrequency = 1000L
    override this.GetTimestamp() = this.Timestamp

[<Theory>]
[<InlineData(9L, false)>]
[<InlineData(10L, true)>]
[<InlineData(11L, true)>]
let ``Elapsed deadline is exact using a monotonic test clock`` elapsed shouldStop =
    let clock = ManualClock()
    use store = build (fixture ())

    let ctx = {
        context () with
            TimeProvider = clock
            IsSnapshotCurrent =
                fun _ ->
                    clock.Timestamp <- elapsed
                    true
    }

    let result =
        Mql.execute
            {
                defaults with
                    MaxElapsedMilliseconds = 10
            }
            (compile defaults allObjects)
            store
            ctx

    if shouldStop then
        truncated [ TruncationReason.ElapsedTime ] result
        Assert.Equal(0, result.Candidates)
    else
        complete result

[<Fact>]
let ``Cancellation and snapshot replacement stop active work without full materialization`` () =
    use store = build (fixture ())
    let plan = compile defaults allObjects
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()

    let cancelled =
        Mql.execute defaults plan store (QueryExecutionContext.create cancellation.Token)

    Assert.Equal(QueryStatus.Cancelled, cancelled.Status)
    Assert.Equal(0, cancelled.Candidates)
    Assert.False cancelled.SourceAvailable
    use during = new CancellationTokenSource()
    let mutable checks = 0

    let ctx = {
        QueryExecutionContext.create during.Token with
            IsSnapshotCurrent =
                fun _ ->
                    checks <- checks + 1

                    if checks = 8 then
                        during.Cancel()

                    true
    }

    let result = Mql.execute defaults plan store ctx
    Assert.Equal(QueryStatus.Cancelled, result.Status)
    Assert.InRange(result.Candidates, 1, 8)
    Assert.True(result.Rows.Length < 9)
    checks <- 0

    let stale =
        Mql.execute defaults plan store {
            context () with
                IsSnapshotCurrent =
                    fun _ ->
                        checks <- checks + 1
                        checks < 8
        }

    failure "MQL301" stale
    Assert.Empty stale.Rows
    Assert.Empty stale.Directives
    let other = SnapshotId.create (Guid.NewGuid()) |> unwrap
    let foreign = Mql.compile defaults other allObjects |> unwrap
    Mql.execute defaults foreign store (context ()) |> failure "MQL301"
    store.Dispose()
    Mql.execute defaults plan store (context ()) |> failure "MQL302"

[<Fact>]
let ``Partial source remains distinct from bounded query status and retains diagnostics`` () =
    let original = fixture ()

    let warning = {
        Code = "fixture-partial"
        Message = "test"
        Stage = ExtractionStage.MemoryMap
        Runtime = None
        Address = None
    }

    use store =
        build {
            original with
                IsPartial = true
                Diagnostics = [| warning |]
        }

    let result =
        Mql.execute { defaults with MaxResults = 1 } (compile defaults allObjects) store (context ())

    truncated [ TruncationReason.Results ] result
    Assert.True result.SourceAvailable
    Assert.True result.SourcePartial
    Assert.Equal(warning, Assert.Single result.SourceDiagnostics)

    let completeResult =
        Mql.execute defaults (compile defaults allObjects) store (context ())

    complete completeResult
    Assert.True completeResult.SourcePartial

[<Fact>]
let ``Core visitors stop promptly preserve sorted metadata and reject disposed use`` () =
    let source = fixture ()

    let segments =
        source.Segments
        |> Array.rev
        |> Array.map (fun segment -> {
            segment with
                Generations = [|
                    {
                        Generation = 2
                        Range = segment.ObjectRange
                    }
                    {
                        Generation = 0
                        Range = segment.ObjectRange
                    }
                |]
        })

    let source = { source with Segments = segments }
    use store = build source
    let mutable visits = 0

    let stopped =
        store.VisitObjects(
            (fun item typ segment ->
                visits <- visits + 1
                Assert.Equal(item.Type, typ.Identity)
                Assert.Equal(item.SegmentAddress, segment.Address)
                visits < 2),
            token
        )
        |> unwrap

    Assert.False stopped
    Assert.Equal(2, visits)
    let keys = ResizeArray<int * uint64>()

    store.VisitSegments(
        (fun segment ->
            keys.Add(segment.Runtime.Index, segment.Address)
            Assert.Equal<int list>([ 0; 2 ], segment.Generations |> Seq.map _.Generation |> Seq.toList)
            true),
        token
    )
    |> unwrap
    |> Assert.True

    Assert.Equal<(int * uint64) list>([ 0, 0x100UL; 0, 0x300UL; 1, 0x100UL; 1, 9007199254740993UL ], List.ofSeq keys)
    use cancellation = new CancellationTokenSource()
    visits <- 0

    Assert.Throws<OperationCanceledException>(fun () ->
        store.VisitObjects(
            (fun _ _ _ ->
                visits <- visits + 1
                cancellation.Cancel()
                true),
            cancellation.Token
        )
        |> ignore)
    |> ignore

    Assert.Equal(1, visits)
    store.Dispose()
    Assert.Equal(Error SnapshotIndexError.Disposed, store.VisitSegments((fun _ -> failwith "Must not visit"), token))

[<Fact>]
let ``Bounded parser fuzz never throws for query data and null plans are argument errors`` () =
    let random = Random 123
    let alphabet = "MATCH()[]:,.=-*\\\"01xyz \r\n"

    for _ in 1..500 do
        let chars =
            Array.init (random.Next(0, 150)) (fun _ -> alphabet[random.Next alphabet.Length])

        Mql.prepare defaults (String chars) |> ignore

    use store = build (fixture ())

    Assert.Throws<ArgumentNullException>(fun () ->
        Mql.execute defaults Unchecked.defaultof<QueryPlan> store (context ()) |> ignore)
    |> ignore

[<Fact>]
let ``Negative generation metadata is rejected before indexing queryable ranges`` () =
    let source = fixture ()
    let segments = Array.copy source.Segments

    segments[0] <- {
        segments[0] with
            Generations = [|
                {
                    Generation = -1
                    Range = segments[0].ObjectRange
                }
            |]
    }

    match IndexedHeapSnapshot.Create({ source with Segments = segments }, SnapshotIndexLimits.defaults, token) with
    | Error(SnapshotIndexError.InvalidInput message) -> Assert.Contains("Generation range", message)
    | result -> failwithf "Expected invalid generation metadata, got %A" result

[<Fact>]
let ``Draw lane lookup and cutoff remain deterministic after extraction reorder`` () =
    let source = fixture ()
    use ordered = build source

    use reversed =
        build {
            source with
                Heaps = Array.rev source.Heaps
                Objects = Array.rev source.Objects
                Segments = Array.rev source.Segments
        }

    let plan = compile defaults "DRAW Memory(1,2,Runtime=1,Heap=0)"

    for cap in [ 2; 3; 4 ] do
        let limits = { defaults with MaxCandidates = cap }
        let left = Mql.execute limits plan ordered (context ())
        let right = Mql.execute limits plan reversed (context ())
        Assert.Equal(left.Status, right.Status)
        Assert.Equal(left.Candidates, right.Candidates)
        Assert.Equal<DrawingDirective list>(left.Directives, right.Directives)
