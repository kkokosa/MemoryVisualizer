namespace MemoryVisualizer.Analysis.Tests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
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
            finally
                if not child.HasExited then
                    child.Kill(true)
                    child.WaitForExit(10000) |> ignore
        }
