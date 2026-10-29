// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The links a running script holds its drives with, kept by the host. One drive component per drive node
// sits on the script's slot, aimed at the field the node is bound to, made in the author's range as the
// author: the link is the author's, so the host grants it only where the author could write by hand and
// never where another drive already holds the field. They are not saved. A script that starts makes them
// again; one that stops takes them away, which hands every field back the last value it showed. -xlinka
internal static class ScriptDriveLinks
{
    public static string? Reconcile(ScriptHost host, CompileResult compiled, IReadOnlyList<object?> bindings, ScriptContext context)
    {
        var world = host.World;
        var slot = host.Slot;
        if (world == null || slot == null || slot.IsDestroyed || !world.IsAuthority)
            return null;
        var existing = Owned(host);
        if (compiled.Drives.Count == 0)
        {
            Release(host);
            return null;
        }
        if (!context.MayUse(Capability.WorldWrite))
        {
            Release(host);
            return "this script may not drive fields";
        }

        var kept = new HashSet<IScriptDrive>(ReferenceEqualityComparer.Instance);
        string? refused = null;
        var principal = context.Principal;
        using (world.DataModelPermissions?.EnterActor(context.Actor))
        {
            foreach (var entry in compiled.Drives)
            {
                var target = entry.Binding >= 0 && entry.Binding < bindings.Count ? bindings[entry.Binding] as IWorldElement : null;
                // A shader property is held through its float4 by a link of its own.
                var type = entry.FieldType == ScriptTypes.Get(typeof(ISyncRef))
                    ? typeof(ScriptReferenceDrive<>).MakeGenericType(entry.ValueType.ClrType)
                    : target is IFieldView
                        ? typeof(ScriptShaderPropertyDrive<>).MakeGenericType(entry.ValueType.ClrType)
                        : typeof(ScriptFieldDrive<>).MakeGenericType(entry.ValueType.ClrType);
                IScriptDrive? drive = null;
                foreach (var candidate in existing)
                {
                    if (!kept.Contains(candidate) && candidate.GetType() == type && candidate.NodeId == entry.NodeId)
                    {
                        drive = candidate;
                        break;
                    }
                }
                try
                {
                    if (drive == null)
                    {
                        using (ScriptWrites.RangeOf(world, principal))
                        {
                            var made = slot.AttachComponent(type);
                            made.Persistent = false;
                            drive = (IScriptDrive)made;
                            drive.Owner = host;
                            drive.NodeId = entry.NodeId;
                        }
                    }
                    drive.Aim(target);
                    drive.Report(Bound(bindings, entry.HeardBinding), Bound(bindings, entry.HeardCountBinding));
                    kept.Add(drive);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
                {
                    refused ??= $"drive {entry.NodeId} could not be linked: {ex.Message}";
                }
            }
        }

        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var drive in existing)
            {
                if (!kept.Contains(drive))
                    ((Component)drive).Destroy();
            }
        }
        return refused;
    }

    private static IWorldElement? Bound(IReadOnlyList<object?> bindings, int index)
        => index >= 0 && index < bindings.Count ? bindings[index] as IWorldElement : null;

    public static void Release(ScriptHost host)
    {
        var world = host.World;
        if (world == null || !world.IsAuthority || host.Slot is not { IsDestroyed: false })
            return;
        var owned = Owned(host);
        if (owned.Count == 0)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var drive in owned)
                ((Component)drive).Destroy();
        }
    }

    public static List<IScriptDrive> Owned(ScriptHost host)
    {
        var owned = new List<IScriptDrive>();
        if (host.Slot is not { IsDestroyed: false } slot)
            return owned;
        foreach (var component in slot.Components)
        {
            if (component is IScriptDrive drive && !drive.IsGone && ReferenceEquals(drive.Owner, host))
                owned.Add(drive);
        }
        return owned;
    }
}
