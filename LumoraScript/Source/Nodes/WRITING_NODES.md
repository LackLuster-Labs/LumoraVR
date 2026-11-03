# Writing a Loom node

A node is one C# class. You declare what it takes, what it gives and how it behaves over time; the registry
reads that once at startup and everything else comes from it: ports, cards, the browser entry, type checks,
the compiled call, native code, and what the runtime may skip. There is no registration, no port table, no
card layout and no WebAssembly to write.

## Browser categories

The browser groups nodes by the thing the user is working with. `[Node("Category/Subcategory/Title")]`
is the single source of the displayed route. Keep these rules when adding or moving nodes (2026-11-02).

| Script category | What belongs here |
|---|---|
| Assets | Loading, attaching and inspecting asset data; Audio, Animations, Materials, Meshes and Textures. |
| Audio | Sound generation, Analysis, Reverb and Signal Processing, including FFT and audio units. |
| Avatars | Avatar rig bones, poses and Body Nodes. |
| Color | Colour operations, Channels, Conversion and Packing. |
| Data | Lists, Dictionaries, Enums, IDs and JSON. JSON keeps its Arrays, Objects, Paths and Values families. |
| Debug | Display, Log, FPS and temporary Drawing. |
| Flow | Branches, sequencing, Loops, Timing, Functions and script lifecycle Events. |
| Graph | Packing/unpacking graphs and Wires (Flow Relay and Value Relay). |
| Input | Raw Actions, Controllers, Display, Headset, Keyboard and Mouse input. |
| Interaction | Grabbing, Pointer, Seats and Tools, with their related events. |
| Logic | Boolean operations, selection, null checks and Comparison. Per Component compares vector components. |
| Math | Arithmetic and mathematical data: vectors, rotations, matrices, bounds, interpolation, random values and other named families. |
| Media | Playback shared by audio, video and animation: play, pause, seek, loop and playback state. |
| Network | Access, HTTP, Messages between scripts, Stream Chat, URLs and WebSockets. |
| Physics | Collider operations, Forces, Queries and contact/overlap Events. |
| Rendering | View sampling, highlights and reflection probe updates. |
| Strings | Text operations, Characters and conversions between text and Collections. |
| Time | Clocks, Dates, Spans and timed Events. |
| Transform | Slot Local/Global transforms, Matrices, Directions, Bounds and space Conversion. |
| UI | Buttons, Text Input (including focus) and Drag and Drop. |
| Undo | Recording and grouping undoable edits. |
| Users | Finding people, Info, Status, Body tracking, Movement and join/leave/spawn Events. |
| Variables | Stored values, Synced variables, Scoped variables and Zones. |
| World | Slots, Components, Fields, References, Session properties and world Events. |

Placement rules:

- File by purpose, not by C# namespace, return type or node base class. A query that returns a list of
  users belongs in `Users/Searching`; a general list operation belongs in `Data/Lists`.
- Keep reads, writes and events for the same feature together. Grab events go in `Interaction/Grabbing`,
  text editing and focus in `UI/Text Input`, socket events in `Network/WebSockets`. Do not restore a
  catch-all `Events`, `Actions`, `Utility` or `Other` root. `Flow/Events` is only for script lifecycle.
- Keep slot hierarchy and metadata in `World/Slots`; put position, rotation, scale and coordinate-space
  conversions in `Transform`. Pure vector, quaternion or matrix calculations remain in `Math`.
- Keep raw device input in `Input`, in-world object interaction in `Interaction`, and panel controls in
  `UI`. Avatar rig information belongs in `Avatars`; the wearer's tracking and movement belong in `Users`.
- Use an existing category first. Add a subcategory when it describes a recognisable task or separates a
  long list into useful families. Prefer one or two folder levels; use a third for established large
  families such as `Data/JSON/Objects`. Avoid chains of folders containing only another folder.
- Keep a single canonical route per node. Do not duplicate nodes to make shortcuts. Search checks both
  the displayed route and the stable class name across the entire selected library.
- Use plain Title Case labels and familiar acronyms (UI, JSON, HTTP, URLs, IDs). Use the category names
  above exactly; do not introduce competing spellings such as Text/Strings or Web/Network.
- A folder must not contain two indistinguishable titles. Qualify the title when needed, as with
  `Graph/Wires/Flow Relay` and `Graph/Wires/Value Relay`.
- The category path is presentation; the class name is saved identity. Moving a node must not change its
  class, ports, overload order, capability or version. A category move needs no graph migration. Existing
  cards and newly opened browsers pick up the new path. Follow the migration rules below for real schema changes.
- Shader nodes use the same Math, Logic and Color families where applicable. Shader-only roots are Input,
  Output, Properties, Texture, Procedural and UV; they describe material work rather than world scripting.
  Vector/channel operations go in `Math/Vectors` or `Color/Packing`. Check both libraries when moving a
  shared `[Gpu]` node, because shader browsing can also offer that node.
- A new top-level category requires updating this table and `ScriptEditorStyle.Category`. Check the
  whole category tree, unique routes, search, breadcrumbs and saved graph resolution before shipping.

## Hover explanations

Give every node a short `Description` in its `[Node]` attribute:

```csharp
[Node("Math/Interpolation/Remap", Description = "Converts a value from one range to another.")]
```

Use one or two plain sentences explaining what the node does. Mention units or an important condition
when that changes how it is used. Keep it around 200 characters or less. Describe the operation first;
do not just repeat the title, list ports, or describe the implementation. Keep source comments for
implementation details. Shader-only help must describe shader inputs rather than script-only overloads.

The registry keeps this description in the built client. The browser and placed node headers both use
it, wrapping it beside the pointer through the normal local tooltip system. Hover delay, tooltip settings,
and hiding on click or grab are shared with other controls. These are descriptions, not saved graph data;
changing one does not need a version bump or migration.

## Search keywords

When people look for a node by words that are not in its title, give them as `Keywords` in `[Node]`,
comma-separated:

```csharp
[Node("Math/Random/Random Vector", Description = "...", Keywords = "random float2, random float3, random float4, random position")]
```

Use plain descriptive words and the script's own type names (float3, color). Never another product's
class or node names. Search (`NodeSearch`) wants every typed word to begin a word of the title, keywords,
route or class name, and splits words where letters meet digits, so "random vector3" meets the title's
"vector" and the keyword's "3". Results go best first: a title equal to the text, then a keyword equal to
it, then titles starting with it, then titles, then titles and keywords, then anything else. Keywords are
not saved graph data and need no version bump.

## The shape

```csharp
[Node("Math/Interpolation/Remap")]          // where the browser files it; the class name is what graphs call it
public sealed class Remap : ValueNode       // the base class says what kind of node it is
{
    // A public static Run is the node. Parameters are inputs, the return value is the output "result".
    // C# defaults are the input defaults shown on the card.
    public static float Run(float value, float fromMin = 0f, float fromMax = 1f, float toMin = 0f, float toMax = 1f)
        => toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);

    // More overloads are more types for the same node. The first one declared is the default.
    public static double Run(double value, double fromMin = 0, double fromMax = 1, double toMin = 0, double toMax = 1)
        => toMin + (value - fromMin) / (fromMax - fromMin) * (toMax - toMin);
}
```

Ports:

- inputs: the parameters, in order. A default value makes the input optional.
- outputs: the return value (`result`) and any `out` parameters, in that order.
- `[return: Port("name")]` or `[Port("name")]` on a parameter renames a port.
- `[Setting]` on a parameter makes it a value edited on the card that no wire can feed.
- `[Default(...)]` on a parameter gives a default a C# constant cannot: a vector, colour or rotation. The
  numbers are the lanes in order, as a graph writes the value; one number fills every lane, so `[Default(1)]`
  is one on a float3 and white on a colour, and a rotation spells all four (`[Default(0, 0, 0, 1)]` is
  none). It wins over a C# default, which a parameter in the middle may still need so the ones after it stay
  optional. The registry checks it by the rules a saved value goes through, the card shows it on an unwired
  input, and the compiler uses it there. Graphs save only the values people set, so changing a default
  changes what an unwired input does in saved graphs. A vector's float lanes may also be infinities, which
  no saved value spells: Encapsulate Bounds starts from Empty Bounds that way.
- An input that only works with a range (min and max, a and b) should not default to an empty one: give
  Random Vector's max `[Default(1)]`, not zero.
- types: anything in the script type vocabulary (numbers, vectors, colours, text, slots, users, fields,
  lists...). A port type outside it leaves the node out, listed in the registry's problems.

## What kind of node, and how it behaves over time

The base class is the declaration. Each one has an `Evaluation`, which the compiler and the runtime use to
skip work:

| Base class     | Evaluation | What it is |
|----------------|------------|------------|
| `ValueNode`    | Pure       | Same inputs, same outputs, no context, no world. Worked out once per call and shared; drives over pure nodes are left alone while their inputs hold. |
| `ReadNode`     | Changing   | Reads the world. Re-read each time a call needs it. Takes `ScriptContext ctx` first. |
| `ActionNode`   | Action     | Changes something when flow arrives. Gets `in`, `next` and `failed` for free. Call `ctx.Fail(reason)` to leave by `failed`; throw `ArgumentException` for an input you cannot use (failed when wired, a fault when not). |
| `EventNode<T>` | Event      | Starts a flow. The payload struct `T`'s properties are its outputs. |
| `FlowNode`, `MemoryNode`, `DriveNode`, `AwaitNode<T>` | Shaped | Built from a shape in `Build(FlowBuilder f)` that the compiler lowers. |

A read that changes less often than "every time" says so, and drives built on it stop being worked out every
frame:

```csharp
[Node("World/Slots/Hierarchy/Parent", Capability.WorldRead)]
[Evaluates(Evaluation.Tree)]                 // same answer until slots, components, names or tags change
public sealed class ParentSlot : ReadNode
{
    [return: Port("parent")]
    public static Slot? Run(ScriptContext ctx, Slot? slot) => ...;
}
```

- `Evaluation.Fixed`: cannot change while the script runs.
- `Evaluation.Tree`: changes only when a slot or component comes, goes or moves, children reorder, or a name
  or tag changes.
- `Evaluation.Field`: reads the field it is handed (its first object input) and changes only when that field
  does.
- `Evaluation.Changing` (the default): time, people, positions, input, media, anything else.

Never claim less change than the node really has: a wrong `[Evaluates]` leaves drives showing stale values. When
unsure, leave it out.

## Capability

`[Node(path, Capability.WorldWrite)]` says what the node reaches for. The runtime links only the capabilities a
script's author and the world allow, and the permission gate still judges every write.

## Events

An event node's payload is a `readonly record struct`; its properties become the outputs. `[SourcedEvent]`
adds a `source` binding (the script's own slot when unbound). An event node can hear its event by itself
with a static `Listen`:

```csharp
[Node("World/Slots/Events/On Child Added")]
[SourcedEvent]
public sealed class OnChildAdded : EventNode<ChildAdded>
{
    public static void Listen(EventHearing<ChildAdded> hearing)
    {
        if (hearing.Slot is not { } slot)
            return;
        Action<Slot, Slot> added = (_, child) => hearing.Raise(new ChildAdded(child));
        slot.OnChildAdded += added;
        hearing.Until(() => slot.OnChildAdded -= added);    // undone when the script stops or changes
    }
}
```

`Raise` queues the call for the script phase; it never runs the script inside your handler. Events with no
`Listen` are heard by the runtime's own sources (`ScriptEventSources`).

## Flow, memory and drives

Control flow is a closed set of shapes so a node cannot invent flow the compiler does not understand:

```csharp
[Node("Flow/If")]
public sealed class If : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Branch(f.Input<bool>("condition"), f.Out("true"), f.Out("false"));
}
```

`FlowBuilder` has Branch, Sequence, Repeat, Range, While, Each, Query, ListEach, Delay, Scope, Once, Toggle,
Cooldown, Change, Watch, Follow, FrameDelta and Drive.

A flow node whose exits are a numbered list takes them as one run with `f.Outs(name, count)`, so people
can grow and shrink it from the card's options the way a `[Variadic]` node grows its inputs:

```csharp
[Node("Flow/Sequence")]
public sealed class Sequence : FlowNode
{
    public override void Build(FlowBuilder f) => f.Sequence(f.Outs("out", 4));    // out0..out3 unless grown
}
```

The run is `FlowBuilder.MinExits` to `MaxExits` long. A graph whose run is the declared length writes no
"sig", so graphs saved before the run could grow open unchanged; any other length writes its count, "6" for
out0..out5. One run per node, on a non-generic flow node (a generic node's "sig" is its types).

## Speed

Every node runs as native code where the platform compiles generated code (desktop, Linux), and in the
interpreter elsewhere; both give the same bits. `[Wasm(op)]` and `[WasmBody]` are optional: they inline a
pure overload as WebAssembly instead of a call, which only matters for very hot math. Write the plain C#
body first.

## In shader graphs too

A `ValueNode` overload over floats, vectors or colours can carry its shader code as `[Gpu("...")]`, written like a
shader node's `[Glsl]` (ports as `{name}`, an expression for the one output). The shader browser then offers the node
with those overloads, and the same node runs its C# body in scripts and its code in a material. A shader node of the
same name wins. Ping Pong, Repeat and Sigmoid are the first.

```csharp
[Gpu("({length} == 0.0 ? 0.0 : {value} - {length} * floor({value} / {length}))")]
public static float Run(float value, float length = 1f) => ...;
```

## Changing a node later

Bump `Version` in `[Node]` when ports change, and add a static `Migrate` for older graphs. Renaming the class
needs `[FormerName("Old")]`. Keep the first-declared overload first: graphs saved without a signature pick it.
A card picked from the browser starts as a float overload; when another one is the obvious start, mark it
`[Preferred]` rather than moving it, the way Round To Int's float form is. One overload per node.
