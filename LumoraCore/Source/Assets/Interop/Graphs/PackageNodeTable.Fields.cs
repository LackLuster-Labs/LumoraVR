// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Utility;
using Lumora.Script;

namespace Lumora.Core.Assets.Interop;

// Fields and stores: drives, values a graph keeps, and writes. A drive becomes a Drive Field on the field its
// proxy held. A synced, saved store becomes a holder component on the script's slot, seeded with what the
// package stored, read with Get Field and written with Set Field, so it stays synced and saved. A store kept
// only for a chain becomes a Stored Value. A write becomes whichever write fits what it writes into. -xlinka
internal static partial class PackageNodeTable
{
    private abstract record Variable;
    private sealed record FieldVar(IWorldElement Field) : Variable;
    private sealed record FieldFrom(PackageNode Node, string Input) : Variable;
    private sealed record ReferenceVar(IWorldElement Reference) : Variable;
    private sealed record ReferenceFrom(PackageNode Node, string Input) : Variable;
    private sealed record StoredVar(string OurNode) : Variable;

    private static void Fields()
    {
        Same(s =>
        {
            var proxy = s.Helper("FieldDriveBase+Proxy");
            var field = proxy == null ? null : s.Index.World(proxy.Ref("Drive")) ?? InGraphHolder(s, proxy.Ref("Drive"));
            // Aimed at nothing in the package, it drives nothing there either.
            if (proxy != null && proxy.Ref("Drive") == null)
                return;
            if (field == null)
            {
                s.Problem($"drives {s.Index.Describe(proxy?.Ref("Drive"))}; the drive is left out");
                return;
            }
            // An id field is a reference here, so a drive of one aims the reference at the element fed in.
            if (field is ISyncRef reference && s.Node.Args.Count > 0 && s.Node.Args[0] == "RefID")
            {
                if (!ScriptTypes.TryGet(reference.TargetType, out var held) || !held.IsHandle)
                {
                    s.Fail("drives a reference whose type has no counterpart here");
                    return;
                }
                string aim = s.Add("DriveReference", held.Name);
                s.Bind(aim, "reference", field, "reference");
                s.In("Value", aim, "target");
                return;
            }
            if ((FieldType(field) ?? s.Arg(0)) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            // The package finds a drive's target by id AND type, so a stale id now naming a field of
            // another shape (a colour drive on a float) drives nothing there either.
            if (s.Arg(0) is { } carried && carried != type && ScriptTypes.TryGet(carried, out var from)
                && ScriptTypes.TryGet(type, out var to) && from.LaneCount > 0 && to.LaneCount > 0 && from.LaneCount != to.LaneCount)
            {
                s.Problem($"drives {s.Index.Describe(proxy!.Ref("Drive"))}, a {type} where it carries a {carried}; the drive is left out");
                return;
            }
            string drive = s.Add("DriveField", type);
            s.Bind(drive, "field", field, $"field<{type}>");
            if (UvPair(field) is { } uv)
            {
                UvInto(s, uv, "Value", drive, "value");
                if (!uv.IsOffset)
                    KeepOffset(s, uv, "Value");
                return;
            }
            s.In("Value", drive, "value");
        }, "ValueFieldDrive", "ObjectFieldDrive");

        // A reference drive aimed at a global reference in the graph is that reference's value, which the
        // global itself reads; only one aimed at the world becomes a drive.
        On("ReferenceDrive", s =>
        {
            var proxy = s.Helper("ReferenceDrive+Proxy");
            string? target = proxy?.Ref("Drive");
            if (target == null || s.Index.TryNode(target, out _, out _))
                return;
            var reference = s.Index.World(target);
            if (reference == null)
            {
                s.Problem("drives a reference that did not come across; the drive is left out");
                return;
            }
            if (ReferencedType(s) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            string drive = s.Add("DriveReference", type);
            s.Bind(drive, "reference", reference, "reference");
            s.In("Target", drive, "target");
        });

        // A hook holds its field at the value fed in from start drive to stop drive and lets it go otherwise; whether it
        // holds is kept on the script's slot, so every machine's drive agrees. Meanwhile every other write is
        // refused and heard: the value tried and a count of tries are kept beside it, and a new try runs on hook.
        On("ValueFieldHook", s =>
        {
            var field = Hooked(s);
            string? type = field != null ? FieldType(field) : null;
            if (field == null || type == null)
            {
                s.Fail("hooks a field that did not come across");
                return;
            }
            var holding = s.Home.AttachComponent<Components.Utility.ValueHolder<bool>>();
            var state = PackageNodeIndex.MemberOf(holding, "Value", -1)!;
            IWorldElement? heard = null;
            try
            {
                var tried = s.Home.AttachComponent(typeof(Components.Utility.ValueHolder<>).MakeGenericType(ScriptTypes.Get(type).ClrType));
                heard = PackageNodeIndex.MemberOf(tried, "Value", -1);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
            var tries = PackageNodeIndex.MemberOf(s.Home.AttachComponent<Components.Utility.ValueHolder<int>>(), "Value", -1)!;
            string on = s.Add("GetField", "bool");
            s.Bind(on, "field", state, "field<bool>");
            string drive = s.Add("DriveField", type);
            s.Bind(drive, "field", field, $"field<{type}>");
            s.In("Source", drive, "value");
            s.Wire(on, "result", drive, "on");
            s.Out("IsDriving", on, "result");
            if (heard != null)
            {
                s.Bind(drive, "heard", heard, $"field<{type}>");
                s.Bind(drive, "heardCount", tries, "field<int>");
                string last = s.Add("GetField", type);
                s.Bind(last, "field", heard, $"field<{type}>");
                s.Out("HookedValue", last, "result");
                if (s.Wired("OnHook"))
                {
                    string tick = s.Add("OnUpdate");
                    string count = s.Add("GetField", "int");
                    s.Bind(count, "field", tries, "field<int>");
                    string watch = s.Add("Watch", "int");
                    s.Flow(tick, "next", watch);
                    s.Wire(count, "result", watch, "value");
                    s.Exit("OnHook", watch, "changed");
                }
            }
            else
                s.OutConstant("HookedValue", null);
            foreach (var (operation, after, value) in new[] { ("StartDrive", "OnStartDrive", true), ("StopDrive", "OnStopDrive", false) })
            {
                string write = s.Add("SetField", "bool");
                s.Bind(write, "field", state, "field<bool>");
                s.Set(write, "value", value);
                s.Op(operation, write);
                s.Exit(after, write, "next");
            }
        });

        Same(s =>
        {
            if (s.Arg(0) is not { } type || Holder(s, s.Node) is not { } field)
            {
                s.Fail("its value's type cannot be kept in a field here");
                return;
            }
            string read = s.Add("GetField", type);
            s.Bind(read, "field", field, $"field<{type}>");
            s.Out("", read, "result");
        }, "DataModelValueFieldStore", "DataModelObjectFieldStore");

        On("DataModelObjectRefStore", s =>
        {
            // A list is no world object to keep a synced reference to; the graph keeps it for itself.
            if (KeepsList(s.Node) is { } list)
            {
                string kept = s.Add("StoredValue", list);
                s.Out("", kept, "value");
                return;
            }
            if (ReferencedType(s) is not { } type || Holder(s, s.Node) is not { } reference)
            {
                s.Fail("its reference's type cannot be kept here");
                return;
            }
            string read = s.Add("ReferenceTarget", type);
            s.Bind(read, "reference", reference, "reference");
            s.Out("", read, "target");
        });

        // A synced switch with set, reset and toggle: each writes the holder and goes on by on set or on reset.
        On("DataModelBooleanToggle", s =>
        {
            if (Holder(s, s.Node) is not { } field)
            {
                s.Fail("its value could not be kept");
                return;
            }
            string read = s.Add("GetField", "bool");
            s.Bind(read, "field", field, "field<bool>");
            s.Out("", read, "result");

            string WriteBool(bool value, string after)
            {
                string write = s.Add("SetField", "bool");
                s.Bind(write, "field", field, "field<bool>");
                s.Set(write, "value", value);
                s.Exit(after, write, "next");
                return write;
            }
            s.Op("Set", WriteBool(true, "OnSet"));
            s.Op("Reset", WriteBool(false, "OnReset"));
            string flip = s.Add("If");
            string current = s.Add("GetField", "bool");
            s.Bind(current, "field", field, "field<bool>");
            s.Wire(current, "result", flip, "condition");
            s.Op("Toggle", flip);
            s.Flow(flip, "true", WriteBool(false, "OnReset"));
            s.Flow(flip, "false", WriteBool(true, "OnSet"));
        });

        Same(s =>
        {
            var type = s.Node.Class == "StoredObject" ? ObjectType(s.Node.Args.Count > 0 ? s.Node.Args[0] : null) : s.Arg(0);
            if (type == null)
            {
                s.Fail("its value's type has no counterpart here");
                return;
            }
            string kept = s.Add("StoredValue", type);
            s.Out("", kept, "value");
        }, "StoredValue", "StoredObject");

        // Only ever the target of a write, which reads through them.
        Same(_ => { }, "FieldAsVariable", "ReferenceAsVariable");

        Same(s =>
        {
            var target = VariableOf(s, "Variable");
            var type = WrittenType(s);
            if ((target == null || type == null) && s.Lost("Variable"))
            {
                s.Problem("writes into something the package had already lost, so it always fails");
                s.OpThrough("", "OnFail");
                return;
            }
            if (target == null || type == null)
            {
                s.Fail("writes into something that did not come across");
                return;
            }
            var (write, reports) = Write(s, target, "Value", type);
            s.Op("", write);
            AfterWrite(s, write, reports, "OnWritten", "OnFail");
        }, "ValueWrite", "ObjectWrite", "ValueIndirectWrite", "ObjectIndirectWrite");

        Same(s =>
        {
            var target = VariableOf(s, "Variable");
            var type = WrittenType(s);
            if (target == null || type == null)
            {
                s.Fail("writes into something that did not come across");
                return;
            }
            var (set, setReports) = Write(s, target, "SetValue", type);
            s.Op("Set", set);
            AfterWrite(s, set, setReports, "OnSet", "OnFail");
            var (reset, resetReports) = Write(s, target, "ResetValue", type);
            s.Op("Reset", reset);
            AfterWrite(s, reset, resetReports, "OnReset", "OnFail");
        }, "ValueWriteLatch", "ObjectWriteLatch", "ValueIndirectWriteLatch", "ObjectIndirectWriteLatch");
    }

    // The field a hook holds: its target input, through sources and globals, else what its link held when saved.
    private static IWorldElement? Hooked(PackageNodeScope s)
    {
        string? id = s.Node.Ref("Target");
        for (int depth = 0; depth < 16 && id != null; depth++)
        {
            if (!s.Index.TryNode(id, out var node, out _))
                return s.Index.World(id);
            id = node.Class switch
            {
                "GlobalReference" => node.Ref("Reference"),
                "ElementSource" or "ObjectRelay" => node.Ref(node.Class == "ObjectRelay" ? "Input" : "Source"),
                _ => null,
            };
        }
        return s.Index.World(s.Helper("+Proxy")?.Ref("Drive"));
    }

    // The field a synced store keeps its value in: a holder on the script's slot, seeded with what the package
    // stored. Made once per store.
    private static IWorldElement? Holder(PackageNodeScope s, PackageNode store)
    {
        if (s.Writer.Holders.TryGetValue(store, out var made))
            return made;
        var proxy = s.Index.Helper(store, "+Store");
        bool reference = store.Class == "DataModelObjectRefStore";
        string? type = store.Class == "DataModelBooleanToggle" ? "bool"
            : reference ? ObjectType(store.Args.Count > 0 ? store.Args[0] : null)
            : store.Arg(0);
        if (type == null || !ScriptTypes.TryGet(type, out var scriptType))
            return null;

        Component holder;
        try
        {
            var open = reference ? typeof(ReferenceHolder<>) : typeof(ValueHolder<>);
            holder = s.Home.AttachComponent(open.MakeGenericType(scriptType.ClrType));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        IWorldElement? member;
        if (reference)
        {
            member = PackageNodeIndex.MemberOf(holder, "Reference", -1);
            if (member is ISyncRef syncRef && proxy != null && s.Index.World(proxy.Ref("Target")) is { } target)
                syncRef.TrySet(type == "slot" && target is Component component ? component.Slot : target);
        }
        else
        {
            if (proxy != null)
                PackageMemberMapper.Apply(holder, proxy.Data, (_, _) => { }, (_, _, _) => { }, _ => null);
            member = PackageNodeIndex.MemberOf(holder, "Value", -1);
        }
        if (member != null)
            s.Writer.Holders[store] = member;
        return member;
    }

    // The type a field holds, as ours: what decides a field node's type, whatever the package called it.
    private static string? FieldType(IWorldElement element)
    {
        foreach (var face in element.GetType().GetInterfaces())
        {
            if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IField<>)
                && ScriptTypes.TryGet(face.GetGenericArguments()[0], out var type))
                return type.Name;
        }
        return null;
    }

    // What a write writes into, from the member that names it.
    private static Variable? VariableOf(PackageNodeScope s, string member)
    {
        if (!s.Index.TryNode(s.Node.Ref(member), out var target, out _))
            return s.WorldOf(member) is { } world ? world is ISyncRef ? new ReferenceVar(world) : new FieldVar(world) : null;
        switch (target.Class)
        {
            case "DataModelValueFieldStore":
            case "DataModelObjectFieldStore":
            case "DataModelBooleanToggle":
                return Holder(s, target) is { } field ? new FieldVar(field) : null;
            case "DataModelObjectRefStore":
                if (KeepsList(target) != null)
                    return s.Writer.Main.TryGetValue(target, out var list) ? new StoredVar(list) : null;
                return Holder(s, target) is { } reference ? new ReferenceVar(reference) : null;
            case "StoredValue":
            case "StoredObject":
                return s.Writer.Main.TryGetValue(target, out var kept) ? new StoredVar(kept) : null;
            case "ValueSource":
            case "ObjectValueSource":
                return (WorldOf(s.Index, target, "Source") ?? InGraphHolder(s, GlobalTarget(s.Index, target, "Source"))) is { } source
                    ? new FieldVar(source) : null;
            case "ReferenceSource":
                return (WorldOf(s.Index, target, "Source") ?? InGraphHolder(s, GlobalTarget(s.Index, target, "Source"))) is { } held
                    ? new ReferenceVar(held) : null;
            case "FieldAsVariable":
                return new FieldFrom(target, "Field");
            case "ReferenceAsVariable":
                return new ReferenceFrom(target, "Reference");
        }
        return null;
    }

    // The world object another node's member points at, through a global reference node when it names one.
    private static IWorldElement? WorldOf(PackageNodeIndex index, PackageNode node, string member)
    {
        string? id = node.Ref(member);
        if (index.TryNode(id, out var global, out _))
            return global.Class.StartsWith("GlobalReference", StringComparison.Ordinal) ? index.World(global.Ref("Reference")) : null;
        return index.World(id);
    }

    // The id a node's member names, through the global reference holding it.
    private static string? GlobalTarget(PackageNodeIndex index, PackageNode node, string member)
    {
        string? id = node.Ref(member);
        if (index.TryNode(id, out var global, out _) && global.Class.StartsWith("GlobalReference", StringComparison.Ordinal))
            return global.Ref("Reference");
        return id;
    }

    // A member inside the graph named as a field: a store's value is the store's holder, and a constant's value,
    // which the graph then writes into or reads as a field, is a holder seeded with the constant.
    public static IWorldElement? InGraphHolder(PackageNodeScope s, string? id)
    {
        if (!s.Index.TryNode(id, out var node, out var member))
            return null;
        if (node.Class.EndsWith("+Store", StringComparison.Ordinal) && node.Ref("Node") is { } served
            && s.Index.Nodes.TryGetValue(served, out var store))
            return Holder(s, store);
        if (node.Class is "ValueInput" or "ValueObjectInput" && member == "Value")
            return ConstantHolder(s, node);
        return null;
    }

    // Made once per constant.
    private static IWorldElement? ConstantHolder(PackageNodeScope s, PackageNode constant)
    {
        if (s.Writer.Holders.TryGetValue(constant, out var made))
            return made;
        if (constant.Arg(0) is not { } type || !ScriptTypes.TryGet(type, out var scriptType))
            return null;
        Component holder;
        try
        {
            holder = s.Home.AttachComponent(typeof(ValueHolder<>).MakeGenericType(scriptType.ClrType));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        PackageMemberMapper.Apply(holder, constant.Data, (_, _) => { }, (_, _, _) => { }, _ => null);
        var member = PackageNodeIndex.MemberOf(holder, "Value", -1);
        if (member != null)
            s.Writer.Holders[constant] = member;
        return member;
    }

    // Whether something in the graph names a constant's value as a field, to write into or read live.
    private static bool NamedAsField(PackageNodeScope s, PackageNode constant)
        => constant.Data.TryGetNode("Value") is Persistence.DataTreeDictionary member
            && (member.TryGetNode("ID") as Persistence.DataTreeValue)?.Value is string id && s.Index.Read.Contains(id);

    // A write's value type: the last generic argument, the first being its context where there are two.
    private static string? WrittenType(PackageNodeScope s)
    {
        if (s.Node.Args.Count == 0)
            return null;
        string arg = s.Node.Args[^1];
        return s.Node.Class.StartsWith("Object", StringComparison.Ordinal) ? ObjectType(arg) : PackageNode.OurType(arg);
    }

    // The list type a reference store keeps, or null for a store of a world object.
    private static string? KeepsList(PackageNode store)
        => store.Class == "DataModelObjectRefStore" && store.Args.Count > 0 && ObjectType(store.Args[^1]) is { } type
            && type.StartsWith("list<", StringComparison.Ordinal) ? type : null;

    // Our handle type for an object type of the package's, where a script can hold one.
    private static string? ObjectType(string? their) => their switch
    {
        null => null,
        "IGrabbable" or "IPlayable" or "IAvatarAnchor" or "AvatarAnchor" => "slot",
        _ => PackageNode.OurType(their),
    };

    // Our write for one target; answers the node and whether it says if the write was taken.
    private static (string Node, bool Reports) Write(PackageNodeScope s, Variable target, string valueInput, string type)
    {
        switch (target)
        {
            case FieldVar field:
            {
                type = FieldType(field.Field) ?? type;
                string write = s.Add("SetField", type);
                s.Bind(write, "field", field.Field, $"field<{type}>");
                if (UvPair(field.Field) is { } uv)
                    UvInto(s, uv, valueInput, write, "value");
                else
                    s.In(valueInput, write, "value");
                return (write, true);
            }
            case FieldFrom from:
            {
                string write = s.Add("SetField", type);
                s.InFrom(from.Node, from.Input, write, "field");
                s.In(valueInput, write, "value");
                return (write, true);
            }
            case ReferenceVar reference:
            {
                string write = s.Add("SetReferenceTarget", type);
                s.Bind(write, "reference", reference.Reference, "reference");
                s.In(valueInput, write, "target");
                return (write, true);
            }
            case ReferenceFrom from:
            {
                string write = s.Add("SetReferenceTarget", type);
                s.InFrom(from.Node, from.Input, write, "reference");
                s.In(valueInput, write, "target");
                return (write, true);
            }
            case StoredVar stored:
            {
                string write = s.Add("WriteValue", type);
                s.Set(write, "var", stored.OurNode);
                s.In(valueInput, write, "value");
                return (write, false);
            }
        }
        throw new InvalidOperationException("no write for " + target);
    }

    // A material's texture transform the import turned over for our upright V (PackageMemberMapper.FlippedUvPair):
    // an offset is counted from the other end of the texture, v' = 1 - scale - v, so a value a graph writes or
    // drives into one is turned over the same way, against the scale the material has as it is written; a scale
    // leaves the offset's turn wrong once it moves, so a drive of one keeps the offset where the package had it.
    // -xlinka
    private sealed record UvTarget(bool IsOffset, IWorldElement Scale, IWorldElement Offset);

    private static UvTarget? UvPair(IWorldElement field)
    {
        if (field is not Sync<Lumora.Core.Math.float2> || field is not Networking.Sync.SyncElement { Parent: Component owner })
            return null;
        string? name = null;
        for (int i = 0; i < owner.SyncMemberCount && name == null; i++)
        {
            if (ReferenceEquals(owner.GetSyncMember(i), field))
                name = owner.GetSyncMemberName(i);
        }
        // A fan-out passing the value on as it is: when everything it drives is a turned-over offset, the value is
        // turned over before it, against the first target's scale; the package fans one value out to transforms of
        // one size.
        if (name == "Value" && owner is ValueMultiDriver<Lumora.Core.Math.float2> fan)
        {
            UvTarget? first = null;
            foreach (var drive in fan.Drives)
            {
                if (drive?.Field is not IWorldElement target)
                    continue;
                if (UvPair(target) is not { IsOffset: true } turned)
                    return null;
                first ??= turned;
            }
            return first == null ? null : new UvTarget(true, first.Scale, field);
        }
        if (name == null || !PackageMemberMapper.FlippedUvPair(owner, name, out var scaleName, out var offsetName))
            return null;
        IWorldElement? scale = null, offset = null;
        for (int i = 0; i < owner.SyncMemberCount; i++)
        {
            string member = owner.GetSyncMemberName(i);
            if (member == scaleName)
                scale = owner.GetSyncMember(i) as IWorldElement;
            else if (member == offsetName)
                offset = owner.GetSyncMember(i) as IWorldElement;
        }
        return scale is Sync<Lumora.Core.Math.float2> && offset is Sync<Lumora.Core.Math.float2> ? new UvTarget(name == offsetName, scale, offset) : null;
    }

    // What feeds the package input into our port, turned over when it is an offset.
    private static void UvInto(PackageNodeScope s, UvTarget uv, string theirInput, string node, string port)
    {
        if (!uv.IsOffset)
        {
            s.In(theirInput, node, port);
            return;
        }
        string value = s.Add("SplitVector", "float2");
        s.In(theirInput, value, "vector");
        string scale = s.Add("GetField", "float2");
        s.Bind(scale, "field", uv.Scale, "field<float2>");
        string scaleParts = s.Add("SplitVector", "float2");
        s.Wire(scale, "result", scaleParts, "vector");
        string rest = s.Add("Subtract", "float,float");
        s.Set(rest, "a", 1d);
        s.Wire(scaleParts, "y", rest, "b");
        string y = s.Add("Subtract", "float,float");
        s.Wire(rest, "result", y, "a");
        s.Wire(value, "y", y, "b");
        string made = s.Add("MakeVector", "float,float");
        s.Wire(value, "x", made, "x");
        s.Wire(y, "result", made, "y");
        s.Wire(made, "vector", node, port);
    }

    // A drive of the scale alone: the offset the package stored is kept as the scale moves, unless something
    // in the package drives that offset itself.
    private static void KeepOffset(PackageNodeScope s, UvTarget uv, string theirInput)
    {
        if (uv.Offset is not Sync<Lumora.Core.Math.float2> offset || uv.Scale is not Sync<Lumora.Core.Math.float2> scaleNow || offset.IsDriven)
            return;
        foreach (var helpers in s.Index.Helpers.Values)
        {
            foreach (var helper in helpers)
            {
                if (ReferenceEquals(s.Index.World(helper.Ref("Drive")), uv.Offset))
                    return;
            }
        }
        // The package's own offset, from the turned one and the scale it was turned against.
        float stored = 1f - scaleNow.Value.y - offset.Value.y;
        string drive = s.Add("DriveField", "float2");
        s.Bind(drive, "field", uv.Offset, "field<float2>");
        string scale = s.Add("SplitVector", "float2");
        s.In(theirInput, scale, "vector");
        string rest = s.Add("Subtract", "float,float");
        s.Set(rest, "a", 1d);
        s.Wire(scale, "y", rest, "b");
        string y = s.Add("Subtract", "float,float");
        s.Wire(rest, "result", y, "a");
        s.Set(y, "b", (double)stored);
        string made = s.Add("MakeVector", "float,float");
        s.Set(made, "x", (double)offset.Value.x);
        s.Wire(y, "result", made, "y");
        s.Wire(made, "vector", drive, "value");
    }

    // Goes on by done, or by fail when the write failed: a field or reference write fails exactly when it is
    // not taken. A stored value's write cannot fail, so a fail call there is never made.
    private static void AfterWrite(PackageNodeScope s, string write, bool reports, string done, string fail)
    {
        s.Exit(done, write, ActionNode.NextExit);
        if (reports && s.Wired(fail))
            s.Exit(fail, write, ActionNode.FailedExit);
    }
}
