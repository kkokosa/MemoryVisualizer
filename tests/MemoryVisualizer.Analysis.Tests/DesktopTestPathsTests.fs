namespace MemoryVisualizer.Analysis.Tests

open System
open System.IO
open Xunit

type DesktopTestPathsTests() =
    let directory =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-desktop-paths-" + Guid.NewGuid().ToString("N"))

    let repository = Path.Combine(directory, "repository")

    do
        Directory.CreateDirectory(Path.Combine(repository, "scripts")) |> ignore
        File.WriteAllText(Path.Combine(repository, "MemoryVisualizer.slnx"), "")
        File.WriteAllText(Path.Combine(repository, "scripts", "native-desktop-test.mjs"), "")

    interface IDisposable with
        member _.Dispose() = Directory.Delete(directory, true)

    [<Fact>]
    member _.``Runtime output finds repository beyond incomplete ancestor markers``() =
        let intermediate = Path.Combine(repository, ".tools", "ci-build")

        let output =
            Path.Combine(intermediate, "bin", "Analysis.Tests", "Release", "net11.0")

        Directory.CreateDirectory output |> ignore
        File.WriteAllText(Path.Combine(intermediate, "MemoryVisualizer.slnx"), "")
        Assert.Equal(repository, DesktopTestPaths.repositoryRoot output null)

    [<Fact>]
    member _.``Explicit repository supports test output outside the checkout``() =
        let output = Path.Combine(directory, "external-build", "bin")
        Directory.CreateDirectory output |> ignore
        Assert.Equal(repository, DesktopTestPaths.repositoryRoot output repository)

    [<Fact>]
    member _.``Invalid explicit repository is not replaced by an ancestor fallback``() =
        let missing = Path.Combine(directory, "missing")

        let error =
            Assert.Throws<DirectoryNotFoundException>(fun () ->
                DesktopTestPaths.repositoryRoot repository missing |> ignore)

        Assert.Contains("MEMORYVISUALIZER_REPOSITORY_ROOT", error.Message)

    [<Fact>]
    member _.``Relative repository overrides are rejected``() =
        let error =
            Assert.Throws<ArgumentException>(fun () ->
                DesktopTestPaths.repositoryRoot repository "relative-checkout" |> ignore)

        Assert.Equal("MEMORYVISUALIZER_REPOSITORY_ROOT", error.ParamName)
