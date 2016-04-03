module ViewModels

open System
open System.Windows
open System.Windows.Input

type FuncCommand (canExec:(obj -> bool), doExec:(obj -> unit)) =     
        let theEvent = new DelegateEvent<EventHandler>()     
        interface ICommand with         
            [<CLIEvent>]         
            member x.CanExecuteChanged = theEvent.Publish         
            member x.CanExecute arg = canExec(arg)         
            member x.Execute arg = doExec(arg)

type MainViewModel() = 
    let mutable name = "Hello from F# + WPF + FsXaml"
    
    member x.Name 
        with get () = name
        and set value = name <- value

    member x.ExecuteCommand = 
        new FuncCommand(
            ( fun _ -> true ),
            ( fun _ -> MessageBox.Show(x.Name) |> ignore )
        )