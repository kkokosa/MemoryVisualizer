namespace MemoryVisualizer.Analysis.ClrMd

open System.Threading.Tasks
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Core.Analysis

type ClrMdSnapshotReader() =
    static member DependencyVersion = typeof<DataTarget>.Assembly.GetName().Version.ToString()

    interface ISnapshotReader with
        member _.ReadMetadataAsync(_, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            Task.FromResult(
                Error(
                    AnalysisError.NotImplemented
                        "Dump analysis is not implemented in the foundation scaffold. No dump was opened."
                )
            )
