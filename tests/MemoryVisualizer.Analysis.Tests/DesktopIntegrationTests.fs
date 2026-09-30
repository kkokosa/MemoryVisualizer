namespace MemoryVisualizer.Analysis.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Cli
open Xunit

type DesktopFactAttribute() as this =
    inherit FactAttribute()

    do
        if Environment.GetEnvironmentVariable("MEMORYVISUALIZER_DESKTOP_E2E") <> "1" then
            this.Skip <- "Enable MEMORYVISUALIZER_DESKTOP_E2E=1 after building the desktop and installing Electron."

type DesktopIntegrationTests(fixture: GeneratedDump) =
    interface IClassFixture<GeneratedDump>

    [<DesktopFact>]
    member _.``Sandboxed desktop opens generated WithHeap dump through trusted native dialogs``() =
        task {
            let root =
                DesktopTestPaths.repositoryRoot
                    AppContext.BaseDirectory
                    (Environment.GetEnvironmentVariable("MEMORYVISUALIZER_REPOSITORY_ROOT"))

            let start =
                ProcessStartInfo(
                    "node",
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )

            start.ArgumentList.Add(Path.Combine(root, "scripts", "native-desktop-test.mjs"))
            start.ArgumentList.Add(fixture.Path)
            start.ArgumentList.Add(fixture.Options.Dac.TrustedPaths[0])
            use child = new Process(StartInfo = start)
            Assert.True(child.Start())
            let stdout = ProcessOutput(8192)
            let stderr = ProcessOutput(8192)

            let drains =
                Task.WhenAll(stdout.DrainAsync child.StandardOutput, stderr.DrainAsync child.StandardError)

            try
                do! child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4.0))
                let! _ = drains.WaitAsync(TimeSpan.FromSeconds(10.0))
                Assert.True(child.ExitCode = 0, $"Desktop integration failed: {stdout.Tail}\n{stderr.Tail}")
                Assert.Contains("Native desktop integration passed", stdout.Tail)
                let exported = Regex.Match(stdout.Tail, "Native desktop SVG SHA256: ([0-9a-f]{64})")
                Assert.True(exported.Success, "Desktop did not report its shared-engine export hash.")
                let output = Path.Combine(fixture.Directory, "desktop-cli-equivalence.svg")
                use cliOutput = new StringWriter()
                use cliError = new StringWriter()

                try
                    let code =
                        ExportCommand.run
                            [|
                                fixture.Path
                                "--dac"
                                fixture.Options.Dac.TrustedPaths[0]
                                "--query"
                                "MATCH (seg: Segment) RETURN seg AS BOX (Background = Blue, Width = 40);\n"
                                + "MATCH (gen: Generation) RETURN gen.Generation AS BOX "
                                + "(Label = gen.Generation, LabelPosition = InnerCenter, Background = Grey, Width = 24);"
                                "--layout"
                                "compact"
                                "--max-elements"
                                "1024"
                                "--output"
                                output
                            |]
                            cliOutput
                            cliError
                            CancellationToken.None

                    Assert.True((code = 0), cliError.ToString())
                    let hash = SHA256.HashData(File.ReadAllBytes output) |> Convert.ToHexStringLower
                    Assert.Equal(exported.Groups[1].Value, hash)
                finally
                    File.Delete output
            finally
                if not child.HasExited then
                    child.Kill(true)
                    child.WaitForExit(10000) |> ignore
        }
