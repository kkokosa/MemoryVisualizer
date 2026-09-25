namespace MemoryVisualizer.Analysis.ClrMd

open System
open System.Runtime.InteropServices
open MemoryVisualizer.Core.Analysis

[<RequireQualifiedAccess>]
module Compatibility =
    let check (hostOS: string) (hostArchitecture: string) (targetOS: string) (targetArchitecture: string) =
        if hostOS <> targetOS then
            Error(
                AnalysisError.UnsupportedTarget
                    $"Dump OS {targetOS} differs from host {hostOS}. Use a matching-OS worker and DAC; cross-OS analysis is not supported."
            )
        elif hostArchitecture <> targetArchitecture then
            Error(
                AnalysisError.UnsupportedTarget
                    $"Dump architecture {targetArchitecture} differs from worker {hostArchitecture}. Run a matching-architecture worker and DAC."
            )
        elif hostArchitecture <> "X64" && not (hostOS = "OSX" && hostArchitecture = "Arm64") then
            Error(
                AnalysisError.UnsupportedTarget
                    "Initial support is Windows/Linux x64 and macOS x64/arm64. x86, ARM32, and other targets require a dedicated worker strategy."
            )
        else
            Ok()

    let internal hostOS =
        if OperatingSystem.IsWindows() then
            OSPlatform.Windows.ToString()
        elif OperatingSystem.IsLinux() then
            OSPlatform.Linux.ToString()
        else
            OSPlatform.OSX.ToString()

module internal ReferenceCoverage =
    let canEnumerate containsPointers sizeBytes hasGcDescriptor =
        not containsPointers || (sizeBytes <= uint64 Int32.MaxValue && hasGcDescriptor)
