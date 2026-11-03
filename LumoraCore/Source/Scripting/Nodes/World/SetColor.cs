// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Script;
using HelioText = Helio.UI.Text;

namespace Lumora.Core.Scripting;

// Tints what a slot shows: its text renderer, else its UI text, else the main colour of the first
// material its mesh renderer draws with that has one. A shader graph's or a shader's material's main colour
// is its colour property called Color or Base Color, else its first colour property. A material is shared by
// everything drawn with it, so tinting one tints them all. A slot with nothing to tint fails.
[Node("World/Components/Set Color", Capability.WorldWrite, Description = "Tints what a slot shows: its text renderer, else its UI text, else the main colour of the first material its mesh renderer draws with that has one.",
    Keywords = "tint, colour, material colour, recolour, paint")]
public sealed class SetColor : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, [Default(1)] color color)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        if (slot!.GetComponent<TextRenderer>() is { IsDestroyed: false } renderer)
        {
            ScriptWrites.Set(ctx, renderer.Color, color);
            return;
        }
        if (slot.GetComponent<HelioText>() is { IsDestroyed: false } label)
        {
            ScriptWrites.Set(ctx, label.Color, color);
            return;
        }
        if (slot.GetComponent<MeshRenderer>() is not { IsDestroyed: false } mesh)
        {
            ctx.Fail($"{ScriptReads.Named(slot)} has nothing to tint");
            return;
        }
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            if (MainColor(mesh.Materials[i]) is { } main)
            {
                ScriptWrites.Set(ctx, main, (colorHDR)color);
                return;
            }
        }
        ctx.Fail($"no material on {ScriptReads.Named(slot)} has a main colour");
    }

    private static IField<colorHDR>? MainColor(IAssetProvider<MaterialAsset>? material) => material switch
    {
        UnlitMaterial { IsDestroyed: false } unlit => unlit.TintColor,
        PBS_Metallic { IsDestroyed: false } metallic => metallic.AlbedoColor,
        PBS_Specular { IsDestroyed: false } specular => specular.AlbedoColor,
        XiexeToonMaterial { IsDestroyed: false } toon => toon.AlbedoColor,
        Components.Assets.CustomShaderMaterial custom when ShaderPropertyFields.MainColor(custom) is { } param
            => ShaderPropertyFields.Of<colorHDR>(param),
        _ => null,
    };
}
