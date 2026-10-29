// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Everything every nameplate in a world shares: one font, one plate material, one material per badge
// icon. Found or built once per world on a LOCAL slot, so twenty users in a room cost one set of these,
// not twenty.
//
// The plate material carries no colour and no texture of its own - tint is white, use_vertex_color is
// on, and every piece of the plate paints itself through its corner colours. Shape is GEOMETRY
// (RoundedQuadMesh, RoundedQuadRingMesh), so panel, ring, chips, accent and status dot all share the
// one material. That is what lets a chip change colour on a role change without allocating anything,
// and it keeps the whole plate on one draw material. -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class NameplateTheme : Component
{
    public const string ThemeSlotName = "Nameplate Theme";

    private const string FallbackFontPath = "res://Assets/Fonts/FiraCode/FiraCode-SemiBold.ttf";
    private const string IconRoot = "res://Assets/Textures/Icons/";
    private const string IconSlotPrefix = "Icon ";

    // THE PLATE PALETTE
    //
    // Drawn from the dash tokens so a nameplate and a dash card are the same object at different sizes.
    // Every colour below lands on a MESH CORNER, and vertex colours reach the shader untouched while
    // DashTheme already stores linear, so a token goes on a corner exactly as written. Only a MATERIAL
    // tint has to go back through DashTheme.MeshTint, because source_color linearises what it is handed.
    //
    // No screen blur behind a plate, ever. The dash gets frosted glass because there is one of it; a room
    // of twenty users would pay twenty blur passes for a label the size of a postcard, so the plate reads
    // as glass on tint alone. -xlinka
    public static readonly color PlateFill =
        new color(DashTheme.Panel.r, DashTheme.Panel.g, DashTheme.Panel.b, 0.68f);

    // The dash draws its hairline at 6% white because it lands on an opaque dark panel. A plate's ring has
    // the room behind it instead, so it carries more alpha or it is not a line at all. -xlinka
    public static readonly color PlateEdge =
        new color(DashTheme.OutlineStrong.r, DashTheme.OutlineStrong.g, DashTheme.OutlineStrong.b, 0.22f);

    // Under a beam that would open the person's profile: the glass lifts the way a dash card does under the
    // pointer, and a plain ring takes the accent.
    public static readonly color PlateHoverFill =
        new color(DashTheme.SurfaceHover.r, DashTheme.SurfaceHover.g, DashTheme.SurfaceHover.b, 0.86f);
    public static readonly color PlateHoverEdge =
        new color(DashTheme.Accent.r, DashTheme.Accent.g, DashTheme.Accent.b, 0.75f);

    public static readonly color ChipFill = DashTheme.Field;
    public static readonly color ChipInk = DashTheme.TextDim;
    public static readonly color NameInk = DashTheme.Text;

    // Somebody who is not signed in: still readable, a step back from an account's name so the two never
    // read as the same kind of thing at a glance.
    public static readonly color GuestInk = DashTheme.TextDim;

    // The accent a supporter gets when their tier names no colour of its own.
    public static readonly color SupporterAccent = new color(0.96f, 0.38f, 0.55f, 1f).ToLinear();

    // Platform staff: a cool ring that carries the moving light, and the light that crosses the name. The
    // light is tinted, not white, because most names already are white.
    public static readonly color StaffRim = new color(0.36f, 0.84f, 0.92f, 0.95f).ToLinear();
    public static readonly colorHDR StaffGlint = new colorHDR(0.3f, 0.86f, 1f, 1f);

    // The tab under a staff plate: the ring's cyan, lit, with the word in the body's navy.
    public static readonly color StaffTabFill = new color(0.45f, 0.9f, 0.98f, 1f).ToLinear();
    public static readonly color StaffTabInk = new color(0.03f, 0.06f, 0.16f, 1f).ToLinear();

    private const string StaffRimShader = "res://Shaders/Nameplate_StaffRim.gdshader";
    private const string StaffPanelShader = "res://Shaders/Nameplate_StaffPanel.gdshader";
    private const string StaffAccentShader = "res://Shaders/Nameplate_StaffAccent.gdshader";
    private const string FlowRimShader = "res://Shaders/Nameplate_FlowRim.gdshader";
    private const string FlowAccentShader = "res://Shaders/Nameplate_FlowAccent.gdshader";

    // Faint, not a black keyline: the plate behind the name does most of the contrast work and the ring is
    // only there for the range where the plate has faded out from under it.
    public static readonly color NameOutline = new color(0f, 0f, 0f, 0.6f);

    // What the accent underline falls back to when nobody authored a rim colour on the equip manager.
    public static readonly color Accent = DashTheme.Accent;

    public FontProvider? Font { get; private set; }
    public UnlitMaterial? PlateMaterial { get; private set; }

    private readonly Dictionary<string, UnlitMaterial> _iconMaterials = new();
    private CustomShaderMaterial? _staffRim;
    private CustomShaderMaterial? _staffAccent;
    private CustomShaderMaterial? _staffPanel;
    private CustomShaderMaterial? _flowRim;
    private CustomShaderMaterial? _flowAccent;

    // Staff, and tiers the accounts service marks as animated, each wear a moving ring and accent bar.
    public CustomShaderMaterial? StaffRimMaterial => Shared(ref _staffRim, "Staff Rim", StaffRimShader);
    public CustomShaderMaterial? StaffAccentMaterial => Shared(ref _staffAccent, "Staff Accent", StaffAccentShader);
    public CustomShaderMaterial? StaffPanelMaterial => Shared(ref _staffPanel, "Staff Panel", StaffPanelShader);
    public CustomShaderMaterial? FlowRimMaterial => Shared(ref _flowRim, "Flow Rim", FlowRimShader);
    public CustomShaderMaterial? FlowAccentMaterial => Shared(ref _flowAccent, "Flow Accent", FlowAccentShader);

    // Built the first time a plate asks, then shared by every plate in the world that wears it.
    private CustomShaderMaterial? Shared(ref CustomShaderMaterial? material, string name, string shader)
    {
        if (material != null && !material.IsDestroyed)
            return material;
        if (Slot == null || Slot.IsDestroyed)
            return null;
        material = Slot.AddSlot(name).AttachComponent<CustomShaderMaterial>();
        material.ShaderPath.Value = shader;
        material.BlendMode.Value = BlendMode.Alpha;
        material.Culling.Value = Culling.None;
        return material;
    }

    public override void OnStart()
    {
        base.OnStart();
        Build();
    }

    private void Build()
    {
        if (Slot == null || Font != null)
            return;

        var fontSlot = Slot.AddSlot("Font");
        Font = fontSlot.AttachComponent<FontProvider>();
        // Lato Semibold is the dash face, so a name over somebody's head is set in the same type as the
        // rest of the UI. Whatever the dash registered comes next and the shipped mono face last: text
        // with no font renders NOTHING, and a headless run or a template that spawns a user root itself
        // has no dash to ask. -xlinka
        Font.URL.Value = DashTheme.FontSemibold;
        var registered = ImportDialog.ResolveFontUrl(World);
        if (registered != null && registered != DashTheme.FontSemibold)
            Font.FallbackURLs.Add(registered);
        Font.FallbackURLs.Add(new Uri(FallbackFontPath));

        var materialSlot = Slot.AddSlot("Plate");
        PlateMaterial = materialSlot.AttachComponent<UnlitMaterial>();
        PlateMaterial.TintColor.Value = colorHDR.White;
        PlateMaterial.UseVertexColor.Value = true;
        PlateMaterial.BlendMode.Value = BlendMode.Alpha;
        // The plate is a flat billboard with no back to hide, and a viewer standing behind somebody
        // still gets a readable plate out of it.
        PlateMaterial.Culling.Value = Culling.None;
    }

    // One material and one texture per ICON KIND for the whole world: a room where twenty people are muted
    // draws twenty chips off a single mute texture. The material carries NO colour - the badge's ink rides
    // the icon mesh's vertex colours - so two badges that share an icon cannot fight over a tint. -xlinka
    public UnlitMaterial? IconMaterial(string icon)
    {
        if (Slot == null || Slot.IsDestroyed || string.IsNullOrEmpty(icon))
            return null;

        if (_iconMaterials.TryGetValue(icon, out var cached) && cached != null && !cached.IsDestroyed)
            return cached;

        var slot = Slot.AddSlot(IconSlotPrefix + icon);
        Uri url;
        try
        {
            url = new Uri(IconRoot + icon + ".svg");
        }
        catch (UriFormatException)
        {
            slot.Destroy();
            return null;
        }

        var provider = slot.AttachComponent<ImageProvider>();
        provider.GenerateMipmaps.Value = false;
        provider.URL.Value = url;

        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = colorHDR.White;
        material.UseVertexColor.Value = true;
        material.BlendMode.Value = BlendMode.Alpha;
        material.Culling.Value = Culling.None;
        material.Texture.Target = provider;

        _iconMaterials[icon] = material;
        return material;
    }

    // Find-or-build, on a local slot under the world root. Every peer builds its own copy and none of it
    // replicates or saves.
    public static NameplateTheme? For(World? world)
    {
        var root = world?.RootSlot;
        if (root == null || root.IsDestroyed)
            return null;

        var slot = root.FindChild(ThemeSlotName, recursive: false);
        var theme = slot?.GetComponent<NameplateTheme>();
        if (theme != null && !theme.IsDestroyed)
            return theme;

        slot?.Destroy();
        slot = root.AddLocalSlot(ThemeSlotName);
        slot.Persistent.Value = false;
        theme = slot.AttachComponent<NameplateTheme>();
        // OnStart is deferred a tick and the first plate wants the font in the same breath it is built.
        theme.Build();
        return theme;
    }
}
