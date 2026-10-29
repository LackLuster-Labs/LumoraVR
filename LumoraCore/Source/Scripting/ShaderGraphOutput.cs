// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Scripting;

// What a shader graph's Material Output does with the materials around it. The graph's own material is the one
// its source names, whose properties its cards' texture slots are; any other CustomShaderMaterial pointed at the
// same source wears the same shader with values of its own. A graph with no material yet (a loose one made with
// the node tool) gets one the first time it needs one: a material orb beside its card carrying the source and the
// material, the way a new shader graph orb carries them. Everything here is done as the person doing it, judged by
// the gate like the same change by hand, and each action is one undo step. -xlinka
public static class ShaderGraphOutput
{
    public static readonly LocaleText SpawnStep = LocaleText.Keyed("Loom.Undo.SpawnOrb", "Spawn Material Orb");
    public static readonly LocaleText DriveStep = LocaleText.Keyed("Loom.Undo.DriveMaterial", "Drive Material");
    public static readonly LocaleText SwapStep = LocaleText.Keyed("Loom.Undo.SwapMaterial", "Swap In Graph Material");

    // Between orbs laid down the side of a card, in metres.
    private const float OrbSpacing = 0.11f;
    private const float OrbGap = 0.07f;

    private static readonly ConditionalWeakTable<ScriptGraphNode, ShaderGraphSwapOffer> Offers = new();

    public static bool IsOutput(ScriptGraphNode? node)
        => node is { IsDestroyed: false } && node.Overload?.Definition.ClrType == typeof(Shading.MaterialOutput)
            && node.Space is { Kind.Value: GraphKind.Shader };

    public static ShaderGraphSource? SourceOf(ScriptGraphSpace? space)
        => space?.ShaderSource.Target is { IsDestroyed: false } source ? source : null;

    // The graph's own material: what its cards' texture slots and a spawned orb are.
    public static CustomShaderMaterial? MaterialOf(ScriptGraphSpace? space)
        => SourceOf(space)?.Material.Target is { IsDestroyed: false } material ? material : null;

    // Every material wearing the graph's shader, its own first, the rest in the order they were made.
    public static List<CustomShaderMaterial> Driven(ScriptGraphSpace? space)
    {
        var found = new List<CustomShaderMaterial>();
        if (SourceOf(space) is not { } source)
            return found;
        if (MaterialOf(space) is { } own && ReferenceEquals(own.Shader.Target, source))
            found.Add(own);
        int first = found.Count;
        try
        {
            foreach (var reference in source.References)
            {
                if (reference is SyncElement { Parent: CustomShaderMaterial { IsDestroyed: false } material }
                    && ReferenceEquals(material.Shader.Target, source) && !found.Contains(material))
                    found.Add(material);
            }
        }
        catch (InvalidOperationException)
        {
            // The set changed under the walk; the next look has it.
        }
        found.Sort(first, found.Count - first, Comparer<CustomShaderMaterial>.Create((a, b) => a.ReferenceID.RawValue.CompareTo(b.ReferenceID.RawValue)));
        return found;
    }

    // A pending offer for this output on this machine, or null.
    public static ShaderGraphSwapOffer? OfferOf(ScriptGraphNode output)
    {
        if (!Offers.TryGetValue(output, out var offer))
            return null;
        if (!offer.IsGone)
            return offer;
        Offers.Remove(output);
        return null;
    }

    public static void ClearOffer(ScriptGraphNode output) => Offers.Remove(output);

    // Where the nth orb for this output goes: down the right side of its card, level with its material well.
    public static float3 Beside(ScriptGraphNode output, int index)
    {
        var layout = output.Space?.View?.CardOf(output)?.Layout ?? ScriptCardLayout.For(output, 0);
        float scale = ScriptEditorStyle.CardScale;
        var local = new float3(layout.Width * scale, -(layout.PreviewTop + ScriptCardLayout.PreviewSize * 0.5f) * scale, 0f);
        var at = output.Slot.LocalPointToGlobal(local);
        var right = output.Slot.GlobalRotation * float3.Right;
        var down = output.Slot.GlobalRotation * float3.Down;
        return at + right * (OrbGap + MaterialOrb.Radius) + down * (OrbSpacing * index);
    }

    // The first free spot down the card's side, so orbs made one after another stand in a column.
    private static float3 FreeSpot(ScriptGraphNode output)
    {
        var root = output.World?.RootSlot;
        for (int index = 0; index < 8; index++)
        {
            var at = Beside(output, index);
            bool taken = false;
            if (root != null)
            {
                foreach (var child in root.Children)
                {
                    if (child.GetComponent<MaterialOrb>() != null && float3.Distance(child.GlobalPosition, at) < MaterialOrb.Radius * 1.5f)
                    {
                        taken = true;
                        break;
                    }
                }
            }
            if (!taken)
                return at;
        }
        return Beside(output, 0);
    }

    // An orb of one of the materials the graph drives, its own when none is named, beside the card. A graph with no
    // material yet gets its own in the orb.
    public static Slot? SpawnOrb(ScriptGraphNode output, CustomShaderMaterial? which, out LocaleText message)
    {
        var records = new List<IUndoBatch>();
        Slot? orb = null;
        if (which == null)
        {
            if (!Ensure(output, records, out var made, out orb, out message))
                return null;
            if (orb != null)
            {
                Record(output, SpawnStep, records);
                message = LocaleText.Keyed("Loom.Shader.OrbMade", "an orb of {0} is beside the card", NameOf(made));
                return orb;
            }
            which = made;
        }
        orb = NewOrb(output, which.Slot?.SlotName.Value ?? ShaderGraphMaterials.OrbName, out message);
        if (orb == null)
            return null;
        try
        {
            MaterialOrb.Build(orb, which);
        }
        catch (UnauthorizedAccessException)
        {
            orb.Destroy();
            message = LocaleText.Keyed("Loom.Shader.NoRoom", "you may not add objects here");
            return null;
        }
        records.Add(SlotExistenceUndoBatch.Created(orb.World, new[] { orb }, SpawnStep)!);
        Record(output, SpawnStep, records);
        message = LocaleText.Keyed("Loom.Shader.OrbMade", "an orb of {0} is beside the card", NameOf(which));
        return orb;
    }

    // The graph's material on every surface of an object, as the material tool's object swap puts one on: never on
    // an orb, a tool, a protected object, a person or a surface a paint policy closes.
    public static int ApplyTo(ScriptGraphNode output, Slot target, out LocaleText message)
    {
        if (!MaterialSurfaces.Paintable(target))
        {
            message = LocaleText.Keyed("Loom.Shader.KeepsOwn", "{0} keeps its own material", target.SlotName.Value ?? "that");
            return 0;
        }
        var renderers = MaterialSurfaces.RenderersUnder(target, MaterialSurfaces.Paintable);
        if (renderers.Count == 0)
        {
            message = LocaleText.Keyed("Loom.Shader.NothingToPaint", "{0} has nothing to paint", target.SlotName.Value ?? "that");
            return 0;
        }
        return Paint(output, target, renderers, -1, out message);
    }

    // The graph's material on the surface under a point, as the material tool's ray puts one on.
    public static int ApplyAt(ScriptGraphNode output, Slot hit, float3 point, out LocaleText message)
    {
        var renderer = MaterialSurfaces.FindRenderer(hit, MaterialSurfaces.Paintable);
        if (renderer == null || !MaterialPaintPolicy.Allows(renderer.Slot))
        {
            message = LocaleText.Keyed("Loom.Shader.NothingThere", "nothing there takes a material");
            return 0;
        }
        return Paint(output, renderer.Slot, new List<Component> { renderer }, MaterialSurfaces.SurfaceUnder(renderer, hit, point), out message);
    }

    private static int Paint(ScriptGraphNode output, Slot target, List<Component> renderers, int surface, out LocaleText message)
    {
        var records = new List<IUndoBatch>();
        if (!Ensure(output, records, out var material, out _, out message))
            return 0;
        int painted = 0;
        foreach (var renderer in renderers)
        {
            try
            {
                MaterialSurfaces.Apply(renderer, surface, material, record =>
                {
                    records.Add(record);
                    painted++;
                });
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Record(output, UndoLocale.ApplyMaterial, records);
        string name = target.SlotName.Value ?? "that";
        message = painted > 0
            ? LocaleText.Keyed("Loom.Shader.Applied", "{0} wears it now ({1} surfaces)", name, painted)
            : AlreadyOrRefused(renderers, material, name);
        return painted;
    }

    private static LocaleText AlreadyOrRefused(List<Component> renderers, IAssetProvider<MaterialAsset> material, string name)
    {
        foreach (var renderer in renderers)
        {
            for (int i = 0; i < MaterialSurfaces.SurfaceCount(renderer); i++)
            {
                if (!ReferenceEquals(MaterialSurfaces.MaterialAt(renderer, i), material))
                    return LocaleText.Keyed("Loom.Shader.Refused", "you may not change {0}", name);
            }
        }
        return LocaleText.Keyed("Loom.Shader.AlreadyWears", "{0} already wears it", name);
    }

    // A material dropped on the output. A graph or shader material is pointed at this graph's shader and keeps its
    // own values for the properties both have (CustomShaderMaterial.EnsureUniforms); a texture this graph has that it
    // lacks starts with the graph's own. One of the built-in kinds cannot wear a shader of ours, so a graph material
    // is made beside the card with its colour and main texture, and offered in its place where it is worn: on the
    // object a reference card was pulled from, else everywhere. Nothing of anyone's is rewritten without the offer
    // being taken.
    public static bool Drive(ScriptGraphNode output, IAssetProvider<MaterialAsset> dropped, AssetRef<MaterialAsset>? pulledFrom,
        out LocaleText message)
    {
        ClearOffer(output);
        var space = output.Space;
        string name = NameOf(dropped);
        if (ReferenceEquals(dropped, MaterialOf(space)))
        {
            message = LocaleText.Keyed("Loom.Shader.OwnMaterial", "that is this graph's own material");
            return false;
        }
        if (dropped is CustomShaderMaterial custom)
            return Repoint(output, custom, name, out message);
        if (dropped is not MaterialProvider builtin || builtin.IsDestroyed)
        {
            message = LocaleText.Keyed("Loom.Shader.NotAMaterial", "that is not a material this can drive");
            return false;
        }

        var records = new List<IUndoBatch>();
        if (!Ensure(output, records, out var own, out _, out message))
            return false;
        var source = SourceOf(output.Space)!;
        var orb = NewOrb(output, LocaleText.Keyed("Loom.Shader.CopyName", "{0} (graph)", name).Resolve(), out message);
        if (orb == null)
        {
            Record(output, DriveStep, records);
            return false;
        }
        CustomShaderMaterial made;
        try
        {
            made = orb.AttachComponent<CustomShaderMaterial>();
            made.Shader.Target = source;
            made.BlendMode.Value = own.BlendMode.Value;
            made.Culling.Value = own.Culling.Value;
            CarryLook(source, builtin, made);
            SeedTextures(own, made);
            MaterialOrb.Build(orb, made);
        }
        catch (UnauthorizedAccessException)
        {
            orb.Destroy();
            Record(output, DriveStep, records);
            message = LocaleText.Keyed("Loom.Shader.NoRoom", "you may not add objects here");
            return false;
        }
        records.Add(SlotExistenceUndoBatch.Created(orb.World, new[] { orb }, DriveStep)!);
        Record(output, DriveStep, records);

        var offer = MakeOffer(builtin, made, pulledFrom);
        if (offer.Count > 0)
        {
            Offers.AddOrUpdate(output, offer);
            message = LocaleText.Keyed("Loom.Shader.Offer", "{0} cannot wear a graph, so a graph copy is beside the card. Swap it in on {1}?",
                name, offer.ObjectList());
        }
        else
        {
            message = LocaleText.Keyed("Loom.Shader.Copied", "{0} cannot wear a graph, so a graph copy is beside the card", name);
        }
        return true;
    }

    private static bool Repoint(ScriptGraphNode output, CustomShaderMaterial custom, string name, out LocaleText message)
    {
        var current = SourceOf(output.Space);
        if (current != null && ReferenceEquals(custom.Shader.Target, current))
        {
            message = LocaleText.Keyed("Loom.Shader.AlreadyDriven", "{0} already wears this graph", name);
            return false;
        }
        if (custom.Shader.Target is ShaderGraphSource { IsDestroyed: false } other && ReferenceEquals(other.Material.Target, custom)
            && other.Graph.Target is { IsDestroyed: false })
        {
            message = LocaleText.Keyed("Loom.Shader.OtherGraph", "{0} is another graph's own material: change it in that graph", name);
            return false;
        }
        if (!custom.Shader.IsWriteAllowed() || !custom.ShaderPath.IsWriteAllowed())
        {
            message = LocaleText.Keyed("Loom.Shader.Refused", "you may not change {0}", name);
            return false;
        }

        var records = new List<IUndoBatch>();
        if (!Ensure(output, records, out var own, out _, out message))
            return false;
        var source = SourceOf(output.Space)!;
        try
        {
            if (!string.IsNullOrEmpty(custom.ShaderPath.Value))
            {
                var path = custom.ShaderPath.Value;
                custom.ShaderPath.Value = string.Empty;
                if (string.IsNullOrEmpty(custom.ShaderPath.Value))
                    records.Add(new FieldEditUndoBatch(custom.ShaderPath, path, string.Empty, DriveStep));
            }
            var before = custom.Shader.Value;
            custom.Shader.Target = source;
            if (ReferenceEquals(custom.Shader.Target, source))
            {
                records.Add(new FieldEditUndoBatch(custom.Shader, before, custom.Shader.Value, DriveStep));
                SeedTextures(own, custom);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        bool done = ReferenceEquals(custom.Shader.Target, source);
        Record(output, DriveStep, records);
        message = done
            ? LocaleText.Keyed("Loom.Shader.Driven", "{0} wears this graph now, with its own values", name)
            : LocaleText.Keyed("Loom.Shader.Refused", "you may not change {0}", name);
        return done;
    }

    // The graph's source and own material, made on an orb beside the card when it has none: everything the orb
    // holds is one undo record, the graph pointed at its source another.
    private static bool Ensure(ScriptGraphNode output, List<IUndoBatch> records, out CustomShaderMaterial material, out Slot? made,
        out LocaleText message)
    {
        material = null!;
        made = null;
        message = default;
        var space = output.Space;
        if (space == null || space.IsDestroyed || space.Kind.Value != GraphKind.Shader)
        {
            message = LocaleText.Keyed("Loom.Shader.NoGraph", "this output is in no shader graph");
            return false;
        }
        var source = SourceOf(space);
        if (source != null && MaterialOf(space) is { } own)
        {
            material = own;
            return true;
        }

        var orb = NewOrb(output, ShaderGraphMaterials.OrbName, out message);
        if (orb == null)
            return false;
        try
        {
            bool newSource = source == null;
            source ??= orb.AttachComponent<ShaderGraphSource>();
            material = orb.AttachComponent<CustomShaderMaterial>();
            material.Shader.Target = source;
            if (newSource)
            {
                source.Graph.Target = space;
                source.Material.Target = material;
            }
            else
            {
                var before = source.Material.Value;
                source.Material.Target = material;
                records.Add(new FieldEditUndoBatch(source.Material, before, source.Material.Value, SpawnStep));
            }
            MaterialOrb.Build(orb, material);
            records.Insert(0, SlotExistenceUndoBatch.Created(orb.World, new[] { orb }, SpawnStep)!);
            if (!ReferenceEquals(space.ShaderSource.Target, source))
            {
                var before = space.ShaderSource.Value;
                space.ShaderSource.Target = source;
                if (!ReferenceEquals(space.ShaderSource.Target, source))
                    throw new UnauthorizedAccessException();
                records.Add(new FieldEditUndoBatch(space.ShaderSource, before, space.ShaderSource.Value, SpawnStep));
            }
        }
        catch (UnauthorizedAccessException)
        {
            records.Clear();
            orb.Destroy();
            message = LocaleText.Keyed("Loom.Shader.NotYours", "you may not give this graph a material");
            return false;
        }
        made = orb;
        return true;
    }

    private static Slot? NewOrb(ScriptGraphNode output, string name, out LocaleText message)
    {
        message = default;
        var world = output.World;
        if (world == null)
            return null;
        Slot orb;
        try
        {
            orb = world.RootSlot.AddSlot(name);
        }
        catch (UnauthorizedAccessException)
        {
            message = LocaleText.Keyed("Loom.Shader.NoRoom", "you may not add objects here");
            return null;
        }
        orb.GlobalPosition = FreeSpot(output);
        orb.GlobalRotation = output.Slot.GlobalRotation;
        return orb;
    }

    // Textures this graph reads that a material it now drives has none of start as the graph's own material has them.
    private static void SeedTextures(CustomShaderMaterial own, CustomShaderMaterial driven)
    {
        if (ReferenceEquals(own, driven))
            return;
        foreach (var param in own.Parameters)
        {
            if (param.Type.Value != ShaderUniformType.Texture2D || param.Texture.Target is not { IsDestroyed: false } texture
                || string.IsNullOrEmpty(param.Name.Value))
                continue;
            ShaderUniformParam? existing = null;
            foreach (var other in driven.Parameters)
            {
                if (other.Name.Value == param.Name.Value)
                {
                    existing = other;
                    break;
                }
            }
            if (existing != null && (existing.Type.Value != ShaderUniformType.Texture2D || existing.Texture.Target != null))
                continue;
            existing ??= driven.Parameters.Add();
            existing.Name.Value = param.Name.Value;
            existing.Type.Value = ShaderUniformType.Texture2D;
            existing.Texture.Target = texture;
        }
    }

    // A built-in material's look on a graph material: its main colour into the graph's main colour property, its main
    // texture into the graph's first texture. The properties are made here from the graph's shader, as the host
    // makes them, so the copy has its values from the start.
    private static void CarryLook(ShaderGraphSource source, MaterialProvider builtin, CustomShaderMaterial made)
    {
        // A source made this moment has not compiled here yet.
        if (string.IsNullOrWhiteSpace(source.Source))
            source.CompileNow();
        if (string.IsNullOrWhiteSpace(source.Source))
            return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in ShaderUniformParser.Parse(source.Source))
        {
            if (!names.Add(def.Name))
                continue;
            var param = made.Parameters.Add();
            param.Name.Value = def.Name;
            param.Type.Value = def.Type;
            param.IsColor.Value = def.IsColor;
            param.HasRange.Value = def.HasRange;
            param.Range.Value = def.Range;
            if (def.HasDefault)
                param.Value.Value = def.DefaultValue;
        }
        if (builtin.TryGetPrimaryColor(out var color) && ShaderPropertyFields.MainColor(made) is { } main)
            ShaderPropertyFields.Of<colorHDR>(main).Value = color;
        if (builtin is ICommonMaterial { MainTexture: { IsDestroyed: false } texture })
        {
            foreach (var param in made.Parameters)
            {
                if (param.Type.Value != ShaderUniformType.Texture2D)
                    continue;
                param.Texture.Target = texture;
                break;
            }
        }
    }

    private static ShaderGraphSwapOffer MakeOffer(IAssetProvider<MaterialAsset> old, CustomShaderMaterial made, AssetRef<MaterialAsset>? pulledFrom)
    {
        var world = made.World;
        Slot? scope = world?.RootSlot;
        if (pulledFrom is { IsDestroyed: false } && ObjectOfMember(pulledFrom) is { } pulled)
            scope = pulled;
        var uses = new List<AssetRef<MaterialAsset>>();
        var objects = new List<string>();
        if (scope != null)
        {
            var all = new List<AssetRef<MaterialAsset>>();
            MaterialSurfaces.CollectReferences(scope, null, all);
            var seen = new HashSet<Slot>();
            foreach (var use in all)
            {
                if (!ReferenceEquals(use.Target, old) || use is ILinkable { IsDriven: true })
                    continue;
                uses.Add(use);
                if (SlotOf(use) is { } slot && MaterialSurfaces.ObjectOf(slot) is { } obj && seen.Add(obj))
                    objects.Add(obj.SlotName.Value ?? "object");
            }
        }
        return new ShaderGraphSwapOffer(old, made, uses, objects);
    }

    private static Slot? ObjectOfMember(IWorldElement member)
        => SlotOf(member) is { } slot ? MaterialSurfaces.ObjectOf(slot) : null;

    private static Slot? SlotOf(IWorldElement? element)
    {
        for (int depth = 0; element != null && depth < 16; depth++)
        {
            switch (element)
            {
                case Slot slot:
                    return slot;
                case Component component:
                    return component.Slot;
                case SyncElement member:
                    element = member.Parent;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static string NameOf(IAssetProvider<MaterialAsset> material)
        => (material as Component)?.Slot?.SlotName.Value is { Length: > 0 } name ? name : material.GetType().Name;

    private static void Record(ScriptGraphNode output, LocaleText step, List<IUndoBatch> records)
    {
        if (records.Count > 0 && output.World is { } world)
            InspectorUndo.Record(world, CompositeUndoBatch.Combine(step, records));
    }
}
