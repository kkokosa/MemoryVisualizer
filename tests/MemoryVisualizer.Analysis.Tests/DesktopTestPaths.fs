namespace MemoryVisualizer.Analysis.Tests

open System
open System.IO

module internal DesktopTestPaths =
    let private isRepository root =
        File.Exists(Path.Combine(root, "MemoryVisualizer.slnx"))
        && File.Exists(Path.Combine(root, "scripts", "native-desktop-test.mjs"))

    let repositoryRoot runtimeDirectory explicitRoot =
        if not (String.IsNullOrWhiteSpace explicitRoot) then
            if not (Path.IsPathFullyQualified explicitRoot) then
                invalidArg "MEMORYVISUALIZER_REPOSITORY_ROOT" "Use an absolute repository root."

            let root = Path.GetFullPath explicitRoot

            if not (isRepository root) then
                raise (
                    DirectoryNotFoundException(
                        "MEMORYVISUALIZER_REPOSITORY_ROOT must contain MemoryVisualizer.slnx and scripts/native-desktop-test.mjs."
                    )
                )

            root
        else
            let rec find (directory: DirectoryInfo) =
                if isNull directory then
                    raise (
                        DirectoryNotFoundException(
                            "Cannot find the repository above the test output. Set MEMORYVISUALIZER_REPOSITORY_ROOT to its absolute path."
                        )
                    )
                elif isRepository directory.FullName then
                    directory.FullName
                else
                    find directory.Parent

            find (DirectoryInfo runtimeDirectory)
