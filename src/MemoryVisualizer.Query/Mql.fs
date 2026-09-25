namespace MemoryVisualizer.Query

open MemoryVisualizer.Core

[<RequireQualifiedAccess>]
module Mql =
    let parse limits source = Parser.parse limits source

    let plan snapshotId syntax = Planner.plan snapshotId syntax

    let prepare limits source =
        parse limits source |> Result.bind Planner.prepare

    let bind snapshotId prepared = Planner.bind snapshotId prepared

    let compile limits snapshotId source =
        parse limits source |> Result.bind (plan snapshotId)

    let execute limits plan (store: IndexedHeapSnapshot) context =
        Execution.execute limits plan store context
