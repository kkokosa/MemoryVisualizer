module Program

open System
open System.Windows
open FsXaml

open ViewModels
open Neo4j

type MainWindow = XAML<"MainWindow.xaml", true>

[<STAThread>]
[<EntryPoint>]
let main argv = 
    let neo4j = start (Neo4j.Create "Main")

    let window = new MainWindow()
    window.Root.DataContext <- new MainViewModel()
    let result = (new Application()).Run(window.Root)

    stop neo4j
    result