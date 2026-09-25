namespace MemoryVisualizer.Analysis.ClrMd

open System
open System.IO
open System.Net
open System.Net.Http
open System.Runtime.InteropServices
open System.Threading
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Core.Analysis

exception internal AnalysisFailure of AnalysisError

/// Disable file-locator fallback; DAC selection uses the separate trusted policy.
type internal NoFileLocator() =
    interface IFileLocator with
        member _.FindPEImage(_, _, _, _) = null
        member _.FindPEImage(_, _, _, _, _) = null

module internal DacResolver =
    let private fail error = raise (AnalysisFailure error)

    let validate (policy: DacPolicy) =
        let validatePath path =
            if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
                fail (AnalysisError.InvalidInput "DAC and cache paths must be explicit absolute trusted local paths.")

            if
                path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal)
            then
                fail (AnalysisError.InvalidInput "UNC DAC/cache paths are not accepted; copy the trusted file locally.")

        policy.TrustedPaths
        |> Map.iter (fun index path ->
            if index < 0 then
                fail (AnalysisError.InvalidInput "DAC runtime indexes must be nonnegative.")

            validatePath path)

        policy.CacheDirectory |> Option.iter validatePath

        if policy.AllowNetwork && policy.CacheDirectory.IsNone then
            fail (
                AnalysisError.InvalidInput
                    "Network DAC resolution requires an explicit trusted per-user cache directory."
            )

        if policy.AllowNetwork && not (OperatingSystem.IsWindows()) then
            fail (
                AnalysisError.UnsupportedTarget
                    "Automatic DAC downloads are Windows-only. On Unix, obtain the matching runtime DAC through a trusted channel and specify its absolute path."
            )

    let private key (library: DebugLibraryInfo) =
        let name =
            library.FileName.Replace('\\', '/') |> fun path -> path.Split('/') |> Array.last

        if
            name = ""
            || name = "."
            || name = ".."
            || name
               |> Seq.exists (fun character ->
                   not (
                       (character >= 'a' && character <= 'z')
                       || (character >= 'A' && character <= 'Z')
                       || (character >= '0' && character <= '9')
                       || character = '.'
                       || character = '-'
                       || character = '_'
                   ))
        then
            fail (AnalysisError.InvalidDump "Runtime advertised an invalid DAC filename.")

        let name = name.ToLowerInvariant()

        if not library.IndexBuildId.IsDefaultOrEmpty then
            let platform =
                if library.Platform = OSPlatform.OSX then
                    "mach-uuid"
                else
                    "elf-buildid"

            let buildId = Convert.ToHexString(library.IndexBuildId.AsSpan()).ToLowerInvariant()

            let prefix =
                if library.ArchivedUnder = SymbolProperties.Coreclr then
                    "coreclr-"
                else
                    ""

            Some [| name; $"{platform}-{prefix}{buildId}"; name |]
        elif library.IndexTimeStamp <> 0 && library.IndexFileSize <> 0 then
            Some [|
                name
                $"{uint32 library.IndexTimeStamp:x8}{uint32 library.IndexFileSize:x}"
                name
            |]
        else
            None

    let downloadWithClient
        (client: HttpClient)
        timeout
        (parts: string array)
        (destination: string)
        (token: CancellationToken)
        =
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
        deadline.CancelAfter(timeout: TimeSpan)
        let downloadToken = deadline.Token

        let uri =
            "https://msdl.microsoft.com/download/symbols/"
            + String.Join("/", parts |> Array.map Uri.EscapeDataString)

        use request = new HttpRequestMessage(HttpMethod.Get, uri)

        use response =
            client.Send(request, HttpCompletionOption.ResponseHeadersRead, downloadToken)

        if response.StatusCode = HttpStatusCode.NotFound then
            false
        else
            response.EnsureSuccessStatusCode() |> ignore
            Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
            let temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp"

            try
                use source = response.Content.ReadAsStream(downloadToken)

                use output =
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                let buffer = Array.zeroCreate<byte> 65536
                let mutable total = 0L
                let mutable reading = true

                while reading do
                    downloadToken.ThrowIfCancellationRequested()

                    let count =
                        source.ReadAsync(buffer.AsMemory(), downloadToken).AsTask().GetAwaiter().GetResult()

                    if count = 0 then
                        reading <- false
                    else
                        total <- total + int64 count

                        if total > 64L * 1024L * 1024L then
                            fail (AnalysisError.DacLoadFailed "DAC download exceeded the 64 MiB safety limit.")

                        output.Write(buffer, 0, count)

                output.Dispose()
                downloadToken.ThrowIfCancellationRequested()
                File.Move(temporary, destination, true)
                true
            finally
                if File.Exists temporary then
                    File.Delete temporary

    let private download parts destination token =
        use client = new HttpClient(Timeout = Timeout.InfiniteTimeSpan)
        downloadWithClient client (TimeSpan.FromSeconds(60.0)) parts destination token

    let resolve (policy: DacPolicy) index (info: ClrInfo) (token: CancellationToken) =
        token.ThrowIfCancellationRequested()

        match policy.TrustedPaths |> Map.tryFind index with
        | Some path ->
            if not (File.Exists path) then
                fail (
                    AnalysisError.DacNotFound
                        $"The trusted DAC for runtime {index} does not exist. Supply its matching runtime DAC file."
                )

            path
        | None ->
            let hostPlatform =
                if OperatingSystem.IsWindows() then OSPlatform.Windows
                elif OperatingSystem.IsLinux() then OSPlatform.Linux
                else OSPlatform.OSX

            let candidates =
                info.DebuggingLibraries
                |> Seq.filter (fun library ->
                    library.Kind = DebugLibraryKind.Dac
                    && library.Platform = hostPlatform
                    && library.TargetArchitecture = RuntimeInformation.ProcessArchitecture)
                |> Seq.choose key

            let resolved =
                policy.CacheDirectory
                |> Option.bind (fun cache ->
                    candidates
                    |> Seq.tryPick (fun parts ->
                        token.ThrowIfCancellationRequested()
                        let path = Path.Combine(Array.append [| cache |] parts)

                        if File.Exists path then
                            Some path
                        elif policy.AllowNetwork && download parts path token then
                            Some path
                        else
                            None))

            match resolved with
            | Some path -> path
            | None ->
                fail (
                    AnalysisError.DacNotFound
                        $"No matching trusted DAC for runtime {index} ({info.Version}). Supply an explicit DAC from that exact runtime build, or configure a trusted symbol cache. Network lookup is off by default."
                )
