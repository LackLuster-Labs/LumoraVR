// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// World and rendering components: lights, layers, ambient light and the value types they carry.
public static partial class PackageTypeMap
{
    static partial void AliasesRendering(Dictionary<string, string> aliases)
    {
        // A light's glowing bulb: its emissive colour follows the light, and a touch switches the light.
        aliases["LightOrb"] = "LightBulb";
        // The layer no eye or camera draws; ours is the render layer override, which starts on it.
        aliases["HiddenLayer"] = "RenderLayerOverride";
        // The world's ambient light as nine coefficients, and what reads them off a probe.
        aliases["AmbientLightSH2"] = "AmbientLight";
        aliases["ReflectionProbeSH2"] = "ProbeHarmonics";
        // The spawn point's glowing floor disc and ring.
        aliases["LegacyGlowCircle"] = "GlowCircle";
        // Somebody who was standing in the saved world. The bodies here are built for the people here, by the
        // spawn; one brought in would be a body nobody is in, which everything that looks for users finds.
        Carried.Add("UserRoot");
    }
}

public static partial class PackageMemberMapper
{
    static partial void ApplyRendering(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl);

    // Lands nowhere: a member worked out at run time here, never stored.
    private const string IgnoredRendering = "\u0000rendering";

    static partial void MemberAliasesRendering(Dictionary<string, Dictionary<string, string>> table)
    {
        AliasRendering(table, "LightBulb", "FlipOnTouch", "SwitchOnTouch");
        AliasRendering(table, "LightBulb", "EmissionColor", "Glow");
        AliasRendering(table, "LightBulb", "AcceptPhysicalTouch", "AcceptFingertipTouch");
        AliasRendering(table, "AmbientLight", "AmbientLight", "Harmonics");
        AliasRendering(table, "AmbientLight", "IsActive", IgnoredRendering);
        AliasRendering(table, "ProbeHarmonics", "AmbientLight", "Harmonics");
        AliasRendering(table, "ProbeHarmonics", "Order0Scale", "Band0Scale");
        AliasRendering(table, "ProbeHarmonics", "Order1Scale", "Band1Scale");
        AliasRendering(table, "ProbeHarmonics", "Order2Scale", "Band2Scale");
        AliasRendering(table, "ReflectionProbe", "SkyboxOnly", "SkyOnly");
        // A camera's clip planes, clear colour, target and post pass under our names; the clear modes and
        // projections agree value for value.
        AliasRendering(table, "Camera", "NearClipping", "NearClip");
        AliasRendering(table, "Camera", "FarClipping", "FarClip");
        AliasRendering(table, "Camera", "ClearColor", "BackgroundColor");
        AliasRendering(table, "Camera", "RenderTexture", "TargetTexture");
        AliasRendering(table, "Camera", "Postprocessing", "RenderPostProcessing");
        AliasRendering(table, "Camera", "UseTransformScale", "ScaleWithSlot");
        // Stored as half the height; read in ApplyRendering.
        AliasRendering(table, "Camera", "OrthographicSize", IgnoredRendering);
    }

    private static void AliasRendering(Dictionary<string, Dictionary<string, string>> table, string ours, string stored, string mine)
    {
        if (!table.TryGetValue(ours, out var names))
            table[ours] = names = new Dictionary<string, string>(StringComparer.Ordinal);
        names[stored] = mine;
    }

    static partial void ApplyRendering(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        switch (component)
        {
            // A picture on a canvas drawn with a material of its own, and the materials a selector swaps onto one:
            // see DrawInCanvasOrder.
            case Helio.UI.Image when (data.TryGetNode("Material") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string material }:
                queueReference(new FoundRendering(found =>
                {
                    DrawInCanvasOrder(found);
                    return true;
                }), material);
                break;
            case Components.Utility.ReferenceSelector<IAssetProvider<MaterialAsset>> selector
                when (data.TryGetNode("Target") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string target }:
                queueReference(new FoundRendering(found =>
                {
                    if (found is Networking.Sync.SyncElement { Parent: Helio.UI.Image } && !selector.IsDestroyed)
                    {
                        foreach (var entry in selector.References)
                            DrawInCanvasOrder(entry);
                    }
                    return true;
                }), target);
                break;
            // A package light shines, and a package camera looks, along its slot's +Z, where ours face -Z. The half
            // turn goes on the component, so the slot and whatever else sits on it keep the pose they came with.
            case Components.Light light:
                light.RotationOffset.Value = floatQ.AxisAngle(float3.Up, MathF.PI);
                break;
            // A package camera stores its orthographic size as half the picture's height; ours is the whole of it.
            case Components.Camera camera:
                camera.RotationOffset.Value = floatQ.AxisAngle(float3.Up, MathF.PI);
                if ((data.TryGetNode("OrthographicSize") as DataTreeDictionary)?.TryGetNode("Data") is { } halfHeight
                    && TryConvert(halfHeight, typeof(float), out var half, remapUrl, "OrthographicSize")
                    && half is float halfValue && float.IsFinite(halfValue))
                    camera.OrthographicSize.Value = halfValue * 2f;
                break;
            // A glow marker stored with the disc and ring it drives; it takes those up rather than its own, and
            // one colour tints both.
            case Components.GlowCircle glow:
                glow.DropBuiltParts();
                glow.RingColor.Value = glow.Color.Value;
                foreach (var (stored, part) in GlowParts)
                {
                    if ((data.TryGetNode(stored) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string { Length: > 0 } id })
                        queueReference(new FoundRendering(found => !glow.IsDestroyed && glow.Adopt(part, found)), id);
                }
                break;
            // A grid there counts the points along each side, ours the spans between them.
            case Components.Meshes.GridMesh grid when (data.TryGetNode("Points") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeList points:
                var counts = ReadNumbers(points);
                if (counts.Length >= 2)
                {
                    grid.SegmentsX.Value = System.Math.Max(1, Whole(counts[0]) - 1);
                    grid.SegmentsY.Value = System.Math.Max(1, Whole(counts[1]) - 1);
                }
                break;
        }
    }

    // Members whose stored value is read some other way on import, so the name table sends their value
    // nowhere, but which a drive can still land on: under this name, or under their own when it is not here.
    private static readonly Dictionary<string, Dictionary<string, string>> DriveAliases = new(StringComparer.Ordinal)
    {
        // A sprite's border scale; the mirror rounds its corners while it sits small and squares them open.
        ["Sprite"] = new(StringComparer.Ordinal) { ["Scale"] = "BorderScale" },
    };

    // What a drive or a pointer at a package member lands on when the name table sends the member's value
    // elsewhere: a mirror switching the material its picture is drawn with drives the image's own material.
    internal static IWorldElement? DriveTargetOf(IWorldElement owner, string member, int index)
    {
        if (owner is not Component component || component.IsDestroyed)
            return null;
        string name;
        if (DriveAliases.TryGetValue(component.GetType().Name, out var names) && names.TryGetValue(member, out var mine))
            name = mine;
        else if (ResolveMemberName(component, member) is { Length: > 0 } resolved && resolved[0] == '\u0000')
            name = member;
        else
            return null;
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            if (component.GetSyncMemberName(i) != name)
                continue;
            var found = component.GetSyncMember(i);
            if (index < 0)
                return found as IWorldElement;
            return found is Networking.Sync.ISyncList list && index < list.Count ? list.GetElement(index) as IWorldElement : null;
        }
        return null;
    }

    // A canvas there draws every picture in its own order, a picture's own material included. Ours orders the
    // canvas's see-through surfaces, and an opaque material draws before all of them, so a mirror drawn on a
    // canvas sat under the panel behind it and showed as that panel. Drawn see-through at full opacity it looks
    // the same and takes its place in the canvas's order. -xlinka
    private static void DrawInCanvasOrder(object? found)
    {
        if (found is not MaterialProvider material || material.IsDestroyed || material is UIUnlitMaterial or UITextMaterial)
            return;
        if (material.GetType().GetField("BlendMode")?.GetValue(material) is Sync<BlendMode> { Value: BlendMode.Opaque } blend)
            blend.Value = BlendMode.Alpha;
    }

    private static readonly (string Stored, Components.GlowCircle.Part Part)[] GlowParts =
    {
        ("_circleQuadSize", Components.GlowCircle.Part.Disc),
        ("_cylinderRadius", Components.GlowCircle.Part.Ring),
        ("_cylinderOffset", Components.GlowCircle.Part.RingSlot),
        ("_circleTint", Components.GlowCircle.Part.DiscMaterial),
        ("_cylinderTint", Components.GlowCircle.Part.RingMaterial),
    };

    // A reference the importer resolves like any other, its target handed on.
    private sealed class FoundRendering : ISyncRef
    {
        private readonly Func<IWorldElement, bool> _then;

        public FoundRendering(Func<IWorldElement, bool> then) => _then = then;

        public RefID Value { get => default; set { } }
        public IWorldElement Target { get => null!; set { } }
        public IWorldElement RawTarget => null!;
        public Type TargetType => typeof(IWorldElement);
        public ReferenceState State => default;
        public void Clear() { }
        public bool TrySet(IWorldElement target) => target != null && _then(target);
    }

    // A whole number stored as a float in the vector reader; rounded so 1023.9999 is 1024, not 1023.
    private static int Whole(float value) => (int)System.Math.Round(value);

    // Nine colours, each [r, g, b, a, profile], are our nine linear RGB coefficients. A package is left-handed,
    // so the coefficients whose basis has an odd power of x (x, xy and xz) change sign with the mirror.
    private static bool TryHarmonics(DataTreeNode node, out object? value)
    {
        value = null;
        if (node is not DataTreeList entries || entries.Count < SphericalHarmonicsL2.Count)
            return false;
        var sh = default(SphericalHarmonicsL2);
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
        {
            var entry = entries[i] is DataTreeDictionary held ? held.TryGetNode("Data") : entries[i];
            if (entry is not DataTreeList parts)
                return false;
            var rgb = ReadNumbers(parts);
            if (rgb.Length < 3)
                return false;
            var coefficient = new float3(rgb[0], rgb[1], rgb[2]);
            if (!IsLinearProfile(parts) && HasProfile(parts))
                coefficient = new float3(Linear(rgb[0]), Linear(rgb[1]), Linear(rgb[2]));
            if (i is 3 or 4 or 7)
                coefficient = -coefficient;
            sh[i] = coefficient;
        }
        value = sh;
        return true;
    }

    private static bool HasProfile(DataTreeList list)
    {
        foreach (var entry in list.Children)
        {
            if (entry is DataTreeValue { Value: string })
                return true;
        }
        return false;
    }

    // The package's own step back to linear: on the curve below 1, as it is from 1 up.
    private static float Linear(float value)
        => System.Math.Abs(value) >= 1f ? value : value < 0f ? -ColorSpace.GammaToLinear(-value) : ColorSpace.GammaToLinear(value);
}
