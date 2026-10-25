// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop;

// Values that come from nowhere else: constants, the world objects a graph names, relays and casts that pass
// a value on unchanged, cards that only show one, and live reads of a field or an element. None of them is a
// node of ours: a constant lands on the port that reads it, a world object becomes a binding on that port, and
// a relay hands its reader whatever feeds it. -xlinka
internal static partial class PackageNodeTable
{
    private static void Values()
    {
        // A constant the graph names as a field is kept in one, so what is written into it is what is read.
        Same(s =>
        {
            if (s.Node.Class != "GlobalValue" && NamedAsField(s, s.Node) && ConstantHolder(s, s.Node) is { } field
                && FieldType(field) is { } type)
            {
                string read = s.Add("GetField", type);
                s.Bind(read, "field", field, $"field<{type}>");
                s.Out("", read, "result");
                return;
            }
            s.OutConstant("", s.Node.Stored("Value"));
        }, "ValueInput", "ValueObjectInput", "GlobalValue");
        On("RefObjectInput", s => s.OutWorld("", s.Index.World(s.Node.Ref("Target"))));

        // A global reference is the object it holds, unless a reference drive in the graph aims it: then it is
        // whatever that drive shows, worked out where it is read.
        On("GlobalReference", s =>
        {
            if (s.Node.Has("Reference") && s.Node.Data.TryGetNode("Reference") is Persistence.DataTreeDictionary member
                && (member.TryGetNode("ID") as Persistence.DataTreeValue)?.Value is string memberId
                && s.Index.DriverOf(memberId) is { } proxy && proxy.Ref("Node") is { } driveId
                && s.Index.Nodes.TryGetValue(driveId, out var drive))
            {
                s.OutFrom("", drive, "Target");
                return;
            }
            s.OutWorld("", s.Index.World(s.Node.Ref("Reference")) ?? InGraphHolder(s, s.Node.Ref("Reference")));
        });

        Same(s => s.OutThrough("", "Input"), "ValueRelay", "ObjectRelay", "ContinuouslyChangingValueRelay",
            "ContinuouslyChangingObjectRelay", "ValueToObjectCast", "ObjectCast");
        On("ContinuationRelay", s => s.OpThrough("", "Next"));

        // Cards that show a value; nothing to carry.
        Same(_ => { }, "ValueDisplay", "ObjectDisplay");

        // Served by the node beside them, which reads what they hold.
        Same(_ => { }, "FieldDriveBase+Proxy", "ReferenceDrive+Proxy", "GetActiveUserSelf+Proxy", "OnLoaded+Proxy",
            "DataModelValueFieldStore+Store", "DataModelObjectFieldStore+Store", "DataModelObjectRefStore+Store", "DynamicImpulseReceiver+Proxy",
            "DynamicImpulseReceiverWithObject+Proxy", "ElapsedTime+Proxy", "OnDuplicate+Proxy", "FieldHookBase+Proxy");

        Same(s =>
        {
            var field = s.WorldOf("Source");
            var type = (field == null ? null : FieldType(field)) ?? s.Arg(0);
            if (type == null)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            // An entry of the package's interface palette is a fixed colour, read as that constant.
            if (field == null && s.Index.PaletteEntry(GlobalTarget(s.Index, s.Node, "Source"), out var code, out bool hex))
            {
                if (hex)
                    s.OutConstant("", code);
                else
                {
                    var paint = colorHDR.FromHex(code);
                    s.OutConstant("", new double[] { paint.r, paint.g, paint.b, paint.a });
                }
                return;
            }
            if (field == null)
            {
                s.OutConstant("", null);
                return;
            }
            string get = s.Add("GetField", type);
            s.Bind(get, "field", field, $"field<{type}>");
            s.Out("", get, "result");
        }, "ValueSource", "ObjectValueSource");

        Same(s => s.OutWorld("", s.WorldOf("Source")), "ElementSource", "SlotSource");

        On("ReferenceSource", s =>
        {
            var type = ReferencedType(s);
            var reference = s.WorldOf("Source");
            if (type == null || reference == null)
            {
                s.OutConstant("", null);
                return;
            }
            string read = s.Add("ReferenceTarget", type);
            s.Bind(read, "reference", reference, "reference");
            s.Out("", read, "target");
        });

        // The person whose body this is: whoever's body the script's own slot is in.
        On("GetActiveUserSelf", s =>
        {
            string self = s.Add("SelfSlot");
            string user = s.Add("UserOf", "slot");
            s.Wire(self, "slot", user, "slot");
            s.Out("", user, "user");
        });
    }

    // What a reference holds, from a SyncRef<T> type argument: our handle type for T.
    private static string? ReferencedType(PackageNodeScope s)
    {
        if (s.Node.Args.Count == 0)
            return null;
        string arg = s.Node.Args[0];
        if (arg.StartsWith("SyncRef<", StringComparison.Ordinal))
            arg = arg[8..^1];
        return PackageNode.OurType(arg);
    }
}
