module Program

open System
open System.Windows
open FsXaml

open ViewModels

type MainWindow = XAML<"MainWindow.xaml", true>

[<STAThread>]
[<EntryPoint>]
let main argv = 
    let window = new MainWindow()
    window.Root.DataContext <- new MainViewModel()
    (new Application()).Run(window.Root)