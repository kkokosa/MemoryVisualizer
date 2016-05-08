module ViewModels

open System
open System.Windows
open System.Windows.Input

open Models
open System.ComponentModel
open System.Text

type CommandHandler (action:(obj -> unit), canExecute:(obj -> bool)) =   
    member x.event = new DelegateEvent<EventHandler>() 
    interface ICommand with         
        [<CLIEvent>]         
        member x.CanExecuteChanged = x.event.Publish
        member x.CanExecute arg = canExecute(arg)         
        member x.Execute arg = action(arg)

type MainViewModel() = 
    let propertyChangedEvent = new DelegateEvent<PropertyChangedEventHandler>()

    let mutable name = "Hello from F# + WPF + FsXaml"
    
    member x.Name 
        with get () = name
        and set value = name <- value

    member val Graph : string = "Test" with get, set

    member x.ExecuteCommand = new CommandHandler((fun _ -> x.ExecuteQuery), ( fun _ -> true ))

    member x.ExecuteQuery =
        let dump = new MemoryDump()
        dump.Load() |> Async.RunSynchronously 
        let sb = new StringBuilder()
        for obj in dump.HeapObjects do
            sb.AppendFormat("[{0:X}] {1} ({2}): {3}\r\n", obj.Address, obj.TypeName, obj.Size, obj.Value) |> ignore
        x.Graph <- sb.ToString()
        x.OnPropertyChanged "Graph"

    interface INotifyPropertyChanged with
        // public event PropertyChangedEventHandler PropertyChanged;
        [<CLIEvent>]
        member x.PropertyChanged = propertyChangedEvent.Publish
    member x.OnPropertyChanged propertyName = 
        propertyChangedEvent.Trigger([| x; new PropertyChangedEventArgs(propertyName) |])
