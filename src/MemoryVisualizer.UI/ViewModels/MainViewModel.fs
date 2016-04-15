module ViewModels

open System
open System.Windows
open System.Windows.Input

open Models
open System.ComponentModel
open System.Text

type FuncCommand (canExec:(obj -> bool), doExec:(obj -> unit)) =     
        let theEvent = new DelegateEvent<EventHandler>()     
        interface ICommand with         
            [<CLIEvent>]         
            member x.CanExecuteChanged = theEvent.Publish         
            member x.CanExecute arg = canExec(arg)         
            member x.Execute arg = doExec(arg)

type MainViewModel() = 
    let propertyChangedEvent = new DelegateEvent<PropertyChangedEventHandler>()

    let mutable name = "Hello from F# + WPF + FsXaml"
    
    member x.Name 
        with get () = name
        and set value = name <- value

    member val Graph : string = "Test" with get, set

    member x.ExecuteCommand = 
        new FuncCommand(
            ( fun _ -> true ),
            ( fun _ -> x.ExecuteQuery )
        )

    member x.ExecuteQuery =
        let dump = new MemoryDump()
        dump.Load()
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
