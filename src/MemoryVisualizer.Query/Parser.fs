namespace MemoryVisualizer.Query

open System
open System.Globalization
open System.Text

module internal Parser =
    type TokenKind =
        | Word of string
        | Number of uint64
        | Text of string
        | Symbol of string
        | Eof

    type Token = { Kind: TokenKind; Span: SourceSpan }

    let private lex limits (source: string) =
        let tokens = ResizeArray<Token>()
        let mutable offset = 0
        let mutable line = 1
        let mutable column = 1
        let mutable nesting = 0

        let span start startLine startColumn = {
            Offset = start
            Length = offset - start
            Line = startLine
            Column = startColumn
        }

        let advance () =
            let c = source[offset]

            if c = '\r' then
                line <- line + 1
                column <- 1
            elif c = '\n' then
                if offset = 0 || source[offset - 1] <> '\r' then
                    line <- line + 1

                column <- 1
            else
                column <- column + 1

            offset <- offset + 1

        let letter c = Char.IsAsciiLetter c || c = '_'

        while offset < source.Length do
            if Char.IsWhiteSpace source[offset] then
                advance ()
            else
                let start, startLine, startColumn = offset, line, column
                let here () = span start startLine startColumn

                if tokens.Count = limits.MaxTokens then
                    Diagnostics.fail "MQL102" { (here ()) with Length = 1 } "Token limit exceeded."

                let c = source[offset]

                let kind =
                    if letter c then
                        advance ()

                        while offset < source.Length
                              && (letter source[offset] || Char.IsAsciiDigit source[offset]) do
                            advance ()

                        Word(source.Substring(start, offset - start))
                    elif Char.IsAsciiDigit c then
                        advance ()

                        while offset < source.Length
                              && (Char.IsAsciiLetterOrDigit source[offset] || source[offset] = '_') do
                            advance ()

                        let text = source.Substring(start, offset - start)
                        let hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        let digits = if hex then text.Substring 2 else text

                        let style =
                            if hex then
                                NumberStyles.AllowHexSpecifier
                            else
                                NumberStyles.None

                        match UInt64.TryParse(digits, style, CultureInfo.InvariantCulture) with
                        | true, value -> Number value
                        | _ ->
                            Diagnostics.fail
                                "MQL103"
                                (here ())
                                "Expected a uint64 decimal or hexadecimal integer; malformed or overflowing literal."
                    elif c = '"' then
                        advance ()
                        let text = StringBuilder()
                        let mutable closed = false

                        while offset < source.Length && not closed do
                            let next = source[offset]
                            advance ()

                            if next = '"' then
                                closed <- true
                            elif next = '\\' then
                                if offset = source.Length then
                                    Diagnostics.fail "MQL104" (here ()) "Unterminated string escape."

                                let escaped = source[offset]
                                advance ()

                                text.Append(
                                    match escaped with
                                    | '"' -> '"'
                                    | '\\' -> '\\'
                                    | 'n' -> '\n'
                                    | 'r' -> '\r'
                                    | 't' -> '\t'
                                    | _ -> Diagnostics.fail "MQL104" (here ()) "Unsupported string escape."
                                )
                                |> ignore
                            elif next = '\r' || next = '\n' then
                                Diagnostics.fail "MQL104" (here ()) "Use escaped newlines inside strings."
                            else
                                text.Append next |> ignore

                        if not closed then
                            Diagnostics.fail "MQL104" (here ()) "Unterminated string."

                        Text(text.ToString())
                    else
                        advance ()
                        let mutable symbol = string c

                        if
                            offset < source.Length
                            && source[offset] = '='
                            && (c = '!' || c = '<' || c = '>')
                        then
                            advance ()
                            symbol <- symbol + "="

                        if c = '(' || c = '[' then
                            nesting <- nesting + 1

                            if nesting > limits.MaxNesting then
                                Diagnostics.fail "MQL105" (here ()) "Nesting limit exceeded."
                        elif c = ')' || c = ']' then
                            nesting <- max 0 (nesting - 1)

                        if not ("():.,;[]=!<>-*|{}".Contains c) then
                            Diagnostics.fail "MQL106" (here ()) $"Unexpected character '{c}'."

                        Symbol symbol

                tokens.Add { Kind = kind; Span = here () }

        tokens.Add {
            Kind = Eof
            Span = span offset line column
        }

        tokens.ToArray()

    let parse limits (source: string) =
        QueryLimits.validate limits
        |> Result.bind (fun () ->
            Diagnostics.attempt (fun () ->
                if isNull source then
                    Diagnostics.fail "MQL100" QueryLimits.origin "Query source must not be null."

                if source.Length > limits.MaxQueryLength then
                    Diagnostics.fail
                        "MQL101"
                        QueryLimits.origin
                        $"Query length exceeds {limits.MaxQueryLength} UTF-16 code units."

                let tokens = lex limits source
                let mutable position = 0
                let current () = tokens[position]

                let take () =
                    let token = current () in
                    position <- position + 1
                    token

                let isWord expected =
                    match (current ()).Kind with
                    | Word word -> String.Equals(word, expected, StringComparison.OrdinalIgnoreCase)
                    | _ -> false

                let isSymbol expected = (current ()).Kind = Symbol expected

                let unsupported () =
                    match (current ()).Kind with
                    | Word word when
                        List.exists (fun other -> String.Equals(word, other, StringComparison.OrdinalIgnoreCase)) [
                            "OR"
                            "WITH"
                            "ORDER"
                            "LIMIT"
                            "SKIP"
                            "OPTIONAL"
                            "CREATE"
                            "DELETE"
                            "SET"
                            "UNION"
                            "CALL"
                            "DOT"
                            "relationships"
                            "hash"
                        ]
                        ->
                        Diagnostics.fail
                            "MQL120"
                            (current ()).Span
                            $"'{word}' is unsupported in M1; use the documented bounded selection and BOX/PIN vocabulary."
                    | Symbol("-" | "*" | "|" | "{" | "}" | "]") ->
                        Diagnostics.fail
                            "MQL120"
                            (current ()).Span
                            "Reference/path patterns and executable expressions are unsupported in M1; bounded graph analysis is deferred to M2."
                    | _ -> ()

                let expected text =
                    unsupported ()
                    Diagnostics.fail "MQL110" (current ()).Span $"Expected {text}."

                let word expectedWord =
                    if not (isWord expectedWord) then
                        expected expectedWord

                    take () |> ignore

                let symbol expectedSymbol =
                    if not (isSymbol expectedSymbol) then
                        expected $"'{expectedSymbol}'"

                    take () |> ignore

                let name () =
                    match (current ()).Kind with
                    | Word value -> let token = take () in { Value = value; Span = token.Span }
                    | _ -> expected "an identifier"

                let number () =
                    match (current ()).Kind with
                    | Number value -> let token = take () in { Value = value; Span = token.Span }
                    | Symbol "-" ->
                        Diagnostics.fail "MQL103" (current ()).Span "Unsigned integers cannot be negative."
                    | _ -> expected "an unsigned integer"

                let literal () =
                    let token = current ()

                    let value =
                        match token.Kind with
                        | Number value -> Literal.Unsigned value
                        | Symbol "-" -> Diagnostics.fail "MQL103" token.Span "Unsigned integers cannot be negative."
                        | Text value -> Literal.Text value
                        | Word value when value.Equals("true", StringComparison.OrdinalIgnoreCase) ->
                            Literal.Boolean true
                        | Word value when value.Equals("false", StringComparison.OrdinalIgnoreCase) ->
                            Literal.Boolean false
                        | _ -> expected "a uint64, string or boolean literal"

                    take () |> ignore
                    { Value = value; Span = token.Span }

                let expression () =
                    match (current ()).Kind with
                    | Number _
                    | Text _ -> ExpressionSyntax.Literal(literal ())
                    | Word _ when isWord "true" || isWord "false" -> ExpressionSyntax.Literal(literal ())
                    | Word _ ->
                        let binding = name ()

                        if isSymbol "." then
                            symbol "."
                            ExpressionSyntax.Property(binding, name ())
                        else
                            ExpressionSyntax.Binding binding
                    | _ -> expected "a binding, property or literal"

                let rec predicate () =
                    let parts = ResizeArray<PredicateSyntax>()
                    parts.Add(term ())

                    while isWord "AND" do
                        word "AND"
                        parts.Add(term ())

                    PredicateSyntax.And(List.ofSeq parts)

                and term () =
                    if isSymbol "(" then
                        symbol "("
                        let nested = predicate ()
                        symbol ")"
                        nested
                    else
                        let property = expression ()

                        if isWord "STARTS" || isWord "OVERLAPS" then
                            let overlaps = isWord "OVERLAPS"

                            if overlaps then
                                word "OVERLAPS"
                            else
                                word "STARTS"
                                word "IN"

                            symbol "["
                            let first = number ()
                            symbol ","
                            let finish = number ()
                            symbol ")"
                            PredicateSyntax.Range(property, overlaps, first, finish)
                        else
                            let token = current ()

                            let operation =
                                match token.Kind with
                                | Symbol "=" -> Comparison.Equal
                                | Symbol "!=" -> Comparison.NotEqual
                                | Symbol "<" -> Comparison.Less
                                | Symbol "<=" -> Comparison.LessOrEqual
                                | Symbol ">" -> Comparison.Greater
                                | Symbol ">=" -> Comparison.GreaterOrEqual
                                | _ -> expected "a comparison, STARTS IN, or OVERLAPS"

                            take () |> ignore
                            PredicateSyntax.Compare(property, { Value = operation; Span = token.Span }, literal ())

                let style () =
                    let key = name ()
                    symbol "="

                    {
                        Name = key
                        Expression = expression ()
                    }

                let statement () =
                    if isWord "MATCH" then
                        word "MATCH"
                        symbol "("
                        let binding = name ()
                        symbol ":"
                        let selector = name ()
                        symbol ")"

                        let filter =
                            if isWord "WHERE" then
                                word "WHERE"
                                Some(predicate ())
                            else
                                None

                        word "RETURN"
                        let projection = ResizeArray<ExpressionSyntax>()

                        let addProjection () =
                            if projection.Count = limits.MaxProjectionColumns then
                                Diagnostics.fail "MQL108" (current ()).Span "Projection column limit exceeded."

                            projection.Add(expression ())

                        addProjection ()

                        while isSymbol "," do
                            symbol ","
                            addProjection ()

                        let presentation =
                            if isWord "AS" then
                                word "AS"
                                let kind = name ()
                                let styles = ResizeArray<StyleSyntax>()

                                if isSymbol "(" then
                                    symbol "("
                                    styles.Add(style ())

                                    while isSymbol "," do
                                        symbol ","
                                        styles.Add(style ())

                                    symbol ")"

                                Some(kind, List.ofSeq styles)
                            else
                                None

                        StatementSyntax.Match {
                            Binding = binding
                            Selector = selector
                            Predicate = filter
                            Projection = List.ofSeq projection
                            Presentation = presentation
                        }
                    elif isWord "DRAW" then
                        let start = (take ()).Span
                        word "Memory"
                        symbol "("
                        let first = number ()
                        symbol ","
                        let finish = number ()
                        symbol ","
                        word "Runtime"
                        symbol "="
                        let runtime = number ()
                        symbol ","
                        word "Heap"
                        symbol "="
                        let heap = number ()

                        let width =
                            if isSymbol "," then
                                symbol ","
                                word "Width"
                                symbol "="
                                Some(number ())
                            else
                                None

                        symbol ")"

                        StatementSyntax.Draw {
                            Span = start
                            Start = first
                            End = finish
                            Runtime = runtime
                            Heap = heap
                            Width = width
                        }
                    else
                        expected "MATCH or DRAW Memory"

                let statements = ResizeArray<StatementSyntax>()
                let mutable finished = false

                while not finished do
                    if statements.Count = limits.MaxStatements then
                        Diagnostics.fail "MQL107" (current ()).Span "Statement limit exceeded."

                    statements.Add(statement ())

                    if (current ()).Kind = Eof then
                        finished <- true
                    else
                        symbol ";"
                        finished <- (current ()).Kind = Eof

                { Statements = List.ofSeq statements }))
