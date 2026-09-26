namespace MemoryVisualizer.Query

open System
open MemoryVisualizer.Core

module internal Planner =
    let private expressionSpan =
        function
        | ExpressionSyntax.Literal value -> value.Span
        | ExpressionSyntax.Binding value -> value.Span
        | ExpressionSyntax.Property(_, value) -> value.Span

    let private index (value: Spanned<uint64>) =
        if value.Value > uint64 Int32.MaxValue then
            Diagnostics.fail "MQL204" value.Span "Runtime, heap and generation indices must fit a nonnegative int32."

        int value.Value

    let private range (first: Spanned<uint64>) (finish: Spanned<uint64>) =
        if finish.Value < first.Value then
            Diagnostics.fail
                "MQL205"
                finish.Span
                "Range end must be greater than or equal to start; ranges are half-open."

        {
            Start = first.Value
            End = finish.Value
        }

    let private width (value: Spanned<uint64>) =
        if value.Value < 1UL || value.Value > 4096UL then
            Diagnostics.fail
                "MQL206"
                value.Span
                "Width must be an integer from 1 through 4096 (abstract presentation units)."

        int value.Value

    let prepare (syntax: QuerySyntax) =
        ArgumentNullException.ThrowIfNull syntax

        Diagnostics.attempt (fun () ->
            let statements =
                syntax.Statements
                |> List.map (function
                    | StatementSyntax.Draw draw ->
                        index draw.Runtime |> ignore
                        index draw.Heap |> ignore
                        range draw.Start draw.End |> ignore
                        PlannedStatement.Draw(draw, draw.Width |> Option.map width |> Option.defaultValue 1)
                    | StatementSyntax.Match statement ->
                        let selector =
                            match statement.Selector.Value.ToUpperInvariant() with
                            | "OBJECT" -> Selector.Object
                            | "SEGMENT" -> Selector.Segment
                            | "GENERATION" -> Selector.Generation
                            | _ ->
                                Diagnostics.fail
                                    "MQL201"
                                    statement.Selector.Span
                                    "Unknown selector; use Object, Segment or Generation."

                        let binding (name: Spanned<string>) =
                            if name.Value <> statement.Binding.Value then
                                Diagnostics.fail
                                    "MQL202"
                                    name.Span
                                    $"Unknown binding '{name.Value}'; bindings are local to one statement."

                        let property (name: Spanned<string>) =
                            let allowed =
                                match name.Value.ToUpperInvariant() with
                                | "ADDRESS" -> Some Property.Address
                                | "SIZE" -> Some Property.Size
                                | "RUNTIME" -> Some Property.Runtime
                                | "HEAP" -> Some Property.Heap
                                | "HEAPKIND" -> Some Property.HeapKind
                                | "TYPE" when selector = Selector.Object -> Some Property.Type
                                | "METHODTABLE" when selector = Selector.Object -> Some Property.MethodTable
                                | "ISFREE" when selector = Selector.Object -> Some Property.IsFree
                                | "GENERATION" when selector <> Selector.Segment -> Some Property.Generation
                                | "END" when selector <> Selector.Object -> Some Property.End
                                | _ -> None

                            match allowed with
                            | Some value -> value
                            | None ->
                                Diagnostics.fail
                                    "MQL203"
                                    name.Span
                                    $"Unknown property '{name.Value}' for {selector}; object End is not representable, use Address and Size."

                        let expression =
                            function
                            | ExpressionSyntax.Literal literal -> BoundExpression.Constant literal.Value
                            | ExpressionSyntax.Binding name ->
                                binding name
                                BoundExpression.Entity
                            | ExpressionSyntax.Property(name, field) ->
                                binding name
                                BoundExpression.Property(property field)

                        let requiredProperty syntax =
                            match expression syntax with
                            | BoundExpression.Property value -> value
                            | _ ->
                                Diagnostics.fail
                                    "MQL207"
                                    (expressionSpan syntax)
                                    "A predicate requires a property on the left."

                        let rec predicate =
                            function
                            | PredicateSyntax.And predicates -> BoundPredicate.And(List.map predicate predicates)
                            | PredicateSyntax.Range(syntax, overlaps, first, finish) ->
                                if requiredProperty syntax <> Property.Address then
                                    Diagnostics.fail
                                        "MQL207"
                                        (expressionSpan syntax)
                                        "STARTS IN and OVERLAPS apply only to Address."

                                BoundPredicate.Range(overlaps, range first finish)
                            | PredicateSyntax.Compare(syntax, operation, literal) ->
                                let field = requiredProperty syntax

                                let valid =
                                    match field, literal.Value with
                                    | (Property.Type | Property.HeapKind), Literal.Text _
                                    | Property.IsFree, Literal.Boolean _ -> true
                                    | (Property.Runtime | Property.Heap | Property.Generation), Literal.Unsigned value ->
                                        index { Value = value; Span = literal.Span } |> ignore
                                        true
                                    | (Property.Address | Property.Size | Property.End | Property.MethodTable),
                                      Literal.Unsigned _ -> true
                                    | _ -> false

                                if not valid then
                                    Diagnostics.fail
                                        "MQL208"
                                        literal.Span
                                        $"Literal type does not match property {field}."

                                match field, operation.Value with
                                | (Property.Type | Property.HeapKind | Property.IsFree),
                                  (Comparison.Equal | Comparison.NotEqual) -> ()
                                | (Property.Type | Property.HeapKind | Property.IsFree), _ ->
                                    Diagnostics.fail
                                        "MQL208"
                                        operation.Span
                                        "Text and boolean properties support only = and !=."
                                | _ -> ()

                                BoundPredicate.Compare(field, operation.Value, literal.Value)

                        let projection =
                            statement.Projection
                            |> List.map (fun syntax ->
                                match syntax with
                                | ExpressionSyntax.Literal value ->
                                    Diagnostics.fail
                                        "MQL209"
                                        value.Span
                                        "RETURN accepts a binding or property, not a literal."
                                | ExpressionSyntax.Binding name -> name.Value, expression syntax
                                | ExpressionSyntax.Property(_, field) -> field.Value, expression syntax)

                        let presentation =
                            statement.Presentation
                            |> Option.map (fun (kind, styles) ->
                                let drawing =
                                    match kind.Value.ToUpperInvariant() with
                                    | "BOX" -> DrawingKind.Box
                                    | "PIN" -> DrawingKind.Pin
                                    | _ ->
                                        Diagnostics.fail
                                            "MQL120"
                                            kind.Span
                                            "Unsupported presentation; M1 supports BOX and PIN only."

                                let mutable result = {
                                    Kind = drawing
                                    Label = None
                                    LabelPosition = LabelPosition.InnerCenter
                                    Background = "Grey"
                                    Width = 1
                                }

                                let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                                for style in styles do
                                    if not (seen.Add style.Name.Value) then
                                        Diagnostics.fail "MQL210" style.Name.Span "Duplicate style property."

                                    let text () =
                                        match style.Expression with
                                        | ExpressionSyntax.Binding value -> value.Value
                                        | ExpressionSyntax.Literal { Value = Literal.Text value } -> value
                                        | _ ->
                                            Diagnostics.fail
                                                "MQL211"
                                                (expressionSpan style.Expression)
                                                "Expected a fixed named style value or string."

                                    match style.Name.Value.ToUpperInvariant() with
                                    | "LABEL" ->
                                        let value = expression style.Expression

                                        if value = BoundExpression.Entity then
                                            Diagnostics.fail
                                                "MQL211"
                                                (expressionSpan style.Expression)
                                                "Label requires a literal or property, not an entity."

                                        result <- { result with Label = Some value }
                                    | "LABELPOSITION" ->
                                        let position =
                                            match (text ()).ToUpperInvariant() with
                                            | "INNERCENTER" -> LabelPosition.InnerCenter
                                            | "OUTERLEFT" -> LabelPosition.OuterLeft
                                            | _ ->
                                                Diagnostics.fail
                                                    "MQL211"
                                                    (expressionSpan style.Expression)
                                                    "LabelPosition must be InnerCenter or OuterLeft."

                                        result <- { result with LabelPosition = position }
                                    | "BACKGROUND" ->
                                        let value = text ()

                                        let color =
                                            [ "Black"; "White"; "Grey"; "Red"; "Green"; "Blue"; "Yellow" ]
                                            |> List.tryFind (fun name ->
                                                name.Equals(value, StringComparison.OrdinalIgnoreCase))

                                        let color =
                                            match color with
                                            | Some name -> name
                                            | None when
                                                value.Length = 7
                                                && value[0] = '#'
                                                && (value.Substring(1) |> Seq.forall Char.IsAsciiHexDigit)
                                                ->
                                                value.ToUpperInvariant()
                                            | _ ->
                                                Diagnostics.fail
                                                    "MQL211"
                                                    (expressionSpan style.Expression)
                                                    "Background must be a supported color name or \"#RRGGBB\"."

                                        result <- { result with Background = color }
                                    | "WIDTH" ->
                                        let value =
                                            match style.Expression with
                                            | ExpressionSyntax.Literal {
                                                                           Value = Literal.Unsigned value
                                                                           Span = span
                                                                       } -> width { Value = value; Span = span }
                                            | _ ->
                                                Diagnostics.fail
                                                    "MQL211"
                                                    (expressionSpan style.Expression)
                                                    "Width requires an integer."

                                        result <- { result with Width = value }
                                    | _ ->
                                        Diagnostics.fail
                                            "MQL210"
                                            style.Name.Span
                                            $"Unknown style property '{style.Name.Value}'."

                                result)

                        PlannedStatement.Select(
                            {
                                Selector = selector
                                Predicate = Option.map predicate statement.Predicate
                                Projection = projection
                            },
                            presentation
                        ))

            { UnboundStatements = statements })

    let bind snapshotId (prepared: PreparedQuery) =
        ArgumentNullException.ThrowIfNull prepared

        Diagnostics.attempt (fun () ->
            if snapshotId = Unchecked.defaultof<SnapshotId> then
                Diagnostics.fail "MQL200" QueryLimits.origin "A plan requires a nonempty snapshot ID."

            {
                Snapshot = snapshotId
                Statements = prepared.UnboundStatements
            })

    let plan snapshotId syntax =
        prepare syntax |> Result.bind (bind snapshotId)
