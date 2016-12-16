module Neo4j

open System.Diagnostics

type Neo4jState = 
    | Starting
    | Started of int
    | Stopping
    | Stopped

type Neo4j = { 
    Name : string 
    State : Neo4jState
}

let Create name =
    { Name = name; State = Stopped }
    
let start (instance : Neo4j) =
    printfn "Starting"
    let pi = new ProcessStartInfo("""..\..\..\..\paket-files\neo4j.com\neo4j-community-3.0.1\bin\neo4j.bat""", "console")
    let process = Process.Start(pi)
    { instance with State = Started(process.Id) }

let stop (instance : Neo4j) =
    match instance.State with
    | Started id -> printf "Stopping"
                    let process = Process.GetProcessById(id)
                    process.CloseMainWindow()
                    { instance with State = Stopped }
    | _ -> printf "Error"
           instance