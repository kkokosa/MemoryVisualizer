module Models

open System.Collections.Generic
open Microsoft.Diagnostics.Runtime

type HeapObject() =
    member val Address : uint64 = 0UL with get, set
    member val TypeName : string = "" with get, set
    member val Size : uint64 = 0UL with get, set
    member val Value : string = "" with get, set

type MemoryDump() =
    let heapObjects = new List<HeapObject>()

    member this.HeapObjects with get() = heapObjects

    member this.Load() = async {
        use target = DataTarget.LoadCrashDump(@"..\..\..\..\data\ConsoleDump.exe.dmp")
        target.SymbolLocator.SymbolPath <- @"SRV*http://msdl.microsoft.com/download/symbols"
        target.SymbolLocator.SymbolCache <- @"c:\symbols"
        //target.SymbolLocator.Timeout <- 1000 * 60 * 5
        for version in target.ClrVersions do
            let dacInfo = version.DacInfo
            let runtime = version.CreateRuntime()
            let heap = runtime.GetHeap()        
            for objectAddr in heap.EnumerateObjectAddresses() do
                let objType = heap.GetObjectType(objectAddr)
                let objSize = objType.GetSize(objectAddr)
                let value = if objType.Name = "System.String" then objType.GetValue(objectAddr) :?> string else ""
                let heapObject = new HeapObject(Address = objectAddr, 
                                                TypeName = objType.Name, 
                                                Size = objSize, 
                                                Value = value)
                heapObjects.Add(heapObject)
                ignore()
        ()
    }



