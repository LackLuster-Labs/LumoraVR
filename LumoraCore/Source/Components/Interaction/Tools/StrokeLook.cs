// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// The surface each tip's stroke wears. Every stroke gets materials of its own on its own slot: changing the
// brush's colour never reaches back into what is already drawn, and a stroke saved on its own takes its look
// with it.
public static class StrokeLook
{
    public static void Apply(Slot slot, MeshRenderer renderer, BrushTip tip, colorHDR colour)
    {
        switch (tip)
        {
            case BrushTip.Ribbon:
            case BrushTip.Fill:
            {
                // flat things are seen from both sides, and lit as the side you are looking at
                var material = slot.AttachComponent<PBS_TwoSided>();
                material.AlbedoColor.Value = colour;
                material.Smoothness.Value = tip == BrushTip.Fill ? 0.3f : 0.45f;
                material.Metallic.Value = 0f;
                renderer.Materials.Add(material);
                break;
            }
            case BrushTip.Confetti:
            {
                // the chips carry their own colours; the albedo stays white so they come through as they are
                var material = slot.AttachComponent<PBS_TwoSided>();
                material.AlbedoColor.Value = new colorHDR(1f, 1f, 1f, 1f);
                material.VertexColors.Value = VertexColorTarget.Albedo;
                material.Smoothness.Value = 0.7f;
                material.Metallic.Value = 0.25f;
                renderer.Materials.Add(material);
                break;
            }
            case BrushTip.Neon:
            {
                // A white-hot thread inside a soft glow of the colour. The core is pushed past 1 so the bloom
                // picks it up; the glow is brightest face on and gone at the edges, so it reads as light
                // around a line and not as a fat translucent tube.
                var core = slot.AttachComponent<UnlitMaterial>();
                var hot = new colorHDR(
                    (colour.r * 0.65f + 0.35f) * 2.4f,
                    (colour.g * 0.65f + 0.35f) * 2.4f,
                    (colour.b * 0.65f + 0.35f) * 2.4f, 1f);
                core.TintColor.Value = hot;
                core.UseVertexColor.Value = false;

                var glow = slot.AttachComponent<FresnelMaterial>();
                glow.NearColor.Value = new colorHDR(colour.r * 1.1f, colour.g * 1.1f, colour.b * 1.1f, 1f);
                glow.FarColor.Value = new colorHDR(0f, 0f, 0f, 0f);
                glow.Exponent.Value = 1.4f;
                glow.BlendMode.Value = BlendMode.Additive;
                glow.ZWrite.Value = ZWrite.Off;
                glow.UseVertexColor.Value = false;

                renderer.Materials.Add(core);
                renderer.Materials.Add(glow);
                break;
            }
            case BrushTip.Spline:
            {
                var material = slot.AttachComponent<PBS_Metallic>();
                material.AlbedoColor.Value = colour;
                material.Smoothness.Value = 0.8f;
                material.Metallic.Value = 0.35f;
                renderer.Materials.Add(material);
                break;
            }
            default:
            {
                var material = slot.AttachComponent<PBS_Metallic>();
                material.AlbedoColor.Value = colour;
                material.Smoothness.Value = 0.55f;
                material.Metallic.Value = 0.05f;
                renderer.Materials.Add(material);
                break;
            }
        }
    }
}
