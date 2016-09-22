# MemoryVisualizer

![Example results](/doc/visualizer_figures.png)

Often there is a need to understand what is inside .NET memory process - probably because of some kind of memory leak. Nevertheless, .NET memory management is also very interesting piece of software. No matter what is the reason you need to look inside, there is a huge amount of data to be analyzed. And as we all know that a picture is worth a thousand words, this tool is dedicated specifically to visualize .NET memory - both from memory dumps and from attached processes.

Of course there is also a plenty of astonishing tools like ANTS Memory Profiler, jetBrains dotMemory or .NET Memory Profiler but... still, they are general, all-in-one great tools for analyzing memory, based on sophisticated general purpose GUI. This tool, in opposite, is particulary designed to visually analyze memory and **draw nice images** that later on can be used for your **articles, presentations, workshops**. And instead of providing all-for-everyone GUI, it provides its own query language for telling what and how should be shown. No hard coded views, all is in your hand.

The goal of this tool is to produce figures like in the opening (exemplary) picture. The program itself will be very simple, with the main window containing query window and the results:

![Main window](/doc/visualizer_window.png)

Queries in MemoryVisualizer are written in the custom language MQL (Memory Query Language) which is based on [Cypher](https://neo4j.com/developer/cypher-query-language/) (neo4j query language) with extensions allowing for formatting and drawing information. Let's look at some examples. Note: those examples impose certain simplifications, not to get lost in the complexity of the memory management problem itself. For example, I assume Workstation GC mode to have only one managed heap.

We can ask for memory segments only:

```
MATCH (seg: Segment)
RETURN seg
```

![MQL1](/doc/mql1.png)

The above-mentioned extensions to Cypher allows to specify how results should be drawn, for example:

```
MATCH (seg: Segment)
RETURN seg AS BOX (Label = seg.Address,
                   LabelPosition = OuterLeft)
```
![MQL1](/doc/mql2.png)

We can draw only generations:

```
MATCH (gen Generation)
RETURN gen AS BOX (Label = gen.Name,
                  LabelPosition = InnerCenter,
                  White = Grey)
```
![MQL1](/doc/mql3.png)

But as one command will contain two queries returning both segments and generations, the engine drawing must be wise and put one on the second line of addresses. This is the very important principle of drawing query results - if the command contains several queries, the results of these queries are drawn in the "overlapping" mode in terms of address space:

```
MATCH (seg: Segment)
RETURN seg

MATCH (gen Generation)
RETURN gen AS BOX (Label = gen.Name,
                   LabelPosition = InnerCenter,
                   Background = hash (seg.Generation))
```
![MQL1](/doc/mql4.png)

Going forward, as Cypher (so MQL) is excellent in querying graphs, it allows perfectly to query for object references:

```
MATCH (parent: Object) - [ref] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN parent, ref, obj
```
![MQL1](/doc/mql5.png)

And with the AS operator we can impose on a further way of drawing:

```
MATCH (parent: Object) - [ref] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN parent AS CIRCLE (Radius = parent.Size)
       ref
       obj AS CIRCLE (Label = obj.Address + "\r\n" + obj.Type,
                      Radius = obj.Size)
```
![MQL1](/doc/mql6.png)

We want to see what roots keeps a reference to the object? Nothing easier thanks to Cypher capabilities (`relationships`):

```
MATCH p = (root: Object) - [*] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN root AS DOT (Label = root.Type)
       relationships (p)
       obj AS DOT (Label = obj.Size)
```

In addition, there is a structure of relationships between the various entities representing memory. Eg. the *Segment* will have a relationship to its *Generations*. Those relations together with overlapping semantics can be easily consumed by MQL. To draw all segments containing generation 2:

```
MATCH (seg: Segment) -> (gen Generation)
WHERE gen.Generation = 2
RETURN seg, gen AS BOX (Label = gen.Name,
                        LabelPosition = InnerCenter,
                        Background = Yellow)
```
![MQL1](/doc/mql7.png)

We can then for example draw additionaly objects of given type:

```
MATCH (seg: Segment) -> (gen Generation) -> (obj: Object)
WHERE gen.Generation = 2 AND obj.Type = "SomeClass"
RETURN seg
       gen AS BOX (Label = gen.Name, LabelPosition = InnerCenter)
       obj AS PIN
```
![MQL1](/doc/mql8.png)

We can also combine this altogether:

```
MATCH (seg: Segment) RETURN seg

MATCH (gen: Generation)
RETURN gen AS BOX (Label = gen.Name, LabelPosition = InnerCenter)

MATCH (obj: Object) - [ref] -> (child: Object)
WHERE child.Address = 0xDDE51018
RETURN obj, ref, child
```
![MQL1](/doc/mql9.png)

For illustrational purposes there is also an addional command `DRAW`. It can take a variety of input functions but at the beginning it be only a `Memory` function, which draws symbolically a given block of memory:

```
DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```
![MQL1](/doc/mql10.png)

Then you could use it with the rest of other queries thanks to the "overlapping semantics":

```
MATCH (gen: Generation)
RETURN gen AS BOX (Background = hash (gen.Generation), Label = gen.Name, LabelPosition = InnerCenter)

DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```
![MQL1](/doc/mql11.png)

Thanks to DRAW command and expressiveness of MQL, drawing fragmentation is as easy as: 

```
MATCH (obj: Object)
WHERE obj.Type = "Free"
RETURN obj AS BOX

DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```
![MQL1](/doc/mql12.png)

## Current status

- [x] Initial commit with project description
- [x] Initial GUI
- [x] Design query language
- [ ] Implement query language
- [ ] Implement .NET memory analysis
- [ ] Implement graph rendering
