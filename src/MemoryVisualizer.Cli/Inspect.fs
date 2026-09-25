namespace MemoryVisualizer.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis

[<RequireQualifiedAccess>]
module Inspect =
    let usage =
        "inspect <dump-path> [--dac <trusted-absolute-path>] [--cache <trusted-absolute-directory>] [--allow-network] [--memory-map-only]"

    let private parse (arguments: string array) =
        if arguments.Length = 0 || arguments[0].StartsWith("--", StringComparison.Ordinal) then
            Error usage
        else
            let mutable options = SnapshotReaderOptions.defaults
            let mutable index = 1
            let mutable error = None
            let seen = Collections.Generic.HashSet<string>()

            while index < arguments.Length && error.IsNone do
                let flag = arguments[index]

                if not (seen.Add flag) then
                    error <- Some $"Duplicate option: {flag}"
                elif flag = "--dac" || flag = "--cache" then
                    if index + 1 >= arguments.Length then
                        error <- Some $"Missing value for {flag}."
                    else
                        let value = arguments[index + 1]

                        options <-
                            if flag = "--dac" then
                                {
                                    options with
                                        Dac = {
                                            options.Dac with
                                                TrustedPaths = Map.ofList [ 0, value ]
                                        }
                                }
                            else
                                {
                                    options with
                                        Dac = {
                                            options.Dac with
                                                CacheDirectory = Some value
                                        }
                                }

                        index <- index + 1
                elif flag = "--allow-network" then
                    options <- {
                        options with
                            Dac = { options.Dac with AllowNetwork = true }
                    }
                elif flag = "--memory-map-only" then
                    options <- {
                        options with
                            IncludeReferences = false
                            IncludeRoots = false
                    }
                else
                    error <- Some $"Unsupported inspect option: {flag}"

                index <- index + 1

            match error with
            | Some error -> Error error
            | None -> Ok(arguments[0], options)

    let run arguments (stdout: TextWriter) (stderr: TextWriter) (token: CancellationToken) =
        match parse arguments with
        | Error message ->
            stderr.WriteLine message
            2
        | Ok(path, options) ->
            try
                match ClrMdSnapshotReader().ReadSnapshotAsync(path, options, None, token).GetAwaiter().GetResult() with
                | Error error ->
                    stderr.WriteLine(string error)
                    2
                | Ok snapshot ->
                    let number (value: uint64) =
                        value.ToString(CultureInfo.InvariantCulture)

                    let count length = number (uint64 length)

                    let summary = {|
                        schemaVersion = 1
                        snapshotId = SnapshotId.format snapshot.Metadata.Id
                        targetOS = snapshot.Target.OperatingSystem
                        architecture = snapshot.Target.Architecture
                        pointerSizeBytes = snapshot.Target.PointerSizeBytes
                        objectCount = number snapshot.Metadata.ObjectCount
                        freeEntryCount = snapshot.Objects |> Array.filter _.IsFree |> Array.length |> count
                        typeCount = count snapshot.Types.Length
                        segmentCount = count snapshot.Segments.Length
                        edgeCount = count snapshot.Edges.Length
                        rootCount = count snapshot.Roots.Length
                        handleCount = count snapshot.Handles.Length
                        partial = snapshot.IsPartial
                        runtimes =
                            snapshot.Runtimes
                            |> Array.map (fun runtime -> {|
                                index = runtime.Identity.Index
                                version = runtime.Version
                                versionSource = runtime.VersionSource
                                flavor = runtime.Flavor
                                memoryMap = string runtime.MemoryMap
                                references = string runtime.References
                                roots = string runtime.Roots
                                handles = string runtime.Handles
                            |})
                        diagnostics =
                            snapshot.Diagnostics
                            |> Array.map (fun diagnostic -> {|
                                code = diagnostic.Code
                                stage = string diagnostic.Stage
                                message = diagnostic.Message
                            |})
                    |}

                    stdout.WriteLine(JsonSerializer.Serialize summary)
                    if snapshot.IsPartial then 3 else 0
            with :? OperationCanceledException ->
                stderr.WriteLine("Cancelled. Native resources have been released.")
                130
