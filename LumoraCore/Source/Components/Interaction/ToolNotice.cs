// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Text;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Helio.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;

namespace Lumora.Core.Components.Interaction;

// Says why a tool did not go into the hand. A refused equip used to be a log line only, so in a Social or Event
// world a tool key or an equip confirm did nothing anyone could see. This machine's business alone: a local slot,
// in front of the eyes for a few seconds, striped in the world mode's colour, then folded away. Not by the hand: on
// desktop the hand sits under the view and the card went with it. A second refusal takes the place of the first
// rather than stacking on it. -xlinka
[ComponentCategory("Hidden")]
public sealed class ToolNotice : Component
{
    private const string SlotName = "Tool Notice";
    private const float Width = 0.46f;
    private const float LineHeight = 0.024f;
    private const float Pad = 0.022f;
    private const float StripeWidth = 0.008f;
    private const float Radius = 0.016f;
    private const float TitleSize = 0.019f;
    private const float BodySize = 0.015f;
    private const int LineChars = 46;
    private const float Linger = 5f;
    private const float Fold = 0.3f;
    private const float BelowEyes = 0.14f;
    private const float Ahead = 0.85f;

    private static readonly color PanelColor = DashTheme.Panel.WithAlpha(0.95f);

    private float _age;

    // Must run on the world's thread.
    public static void Refused(World? world, string? toolName)
    {
        if (world?.RootSlot == null || world.IsDestroyed || world.LocalUser == null)
            return;
        string what = string.IsNullOrWhiteSpace(toolName) ? "That tool" : toolName!;
        var mode = world.Mode;
        string title, text;
        if (!world.AllowsWorldEditing)
        {
            title = $"Tools are off in {DashTheme.ModeLabel(mode)} worlds";
            text = mode == WorldMode.Event
                ? $"{what} stays put away. Nobody builds or edits an Event world, the host included."
                : $"{what} stays put away. A Social world stays as it was built, for everyone, the host included. Your avatar and your own things still work.";
        }
        else
        {
            title = "Tools are not part of your role";
            text = $"{what} stays put away. The host has not given your role tools in this world.";
        }
        Show(world, title, text, DashTheme.ModeTint(mode));
    }

    // The same card for a refusal that is not about tools: a protected item, mostly. Must run on the world's thread.
    public static void Say(World? world, string title, string text) => Say(world, title, text, DashTheme.Warning);

    // The same card with its own stripe colour, for news that is not a refusal.
    public static void Say(World? world, string title, string text, color accent)
    {
        if (world?.RootSlot == null || world.IsDestroyed || world.LocalUser == null)
            return;
        Show(world, title, text, accent);
    }

    private static void Show(World world, string title, string text, color accent)
    {
        foreach (var old in world.RootSlot.LocalChildren)
        {
            if (old.Name.Value == SlotName && old.GetComponent<ToolNotice>() != null && !old.IsDestroyed)
            {
                old.Destroy();
                break;
            }
        }
        var slot = world.RootSlot.AddLocalSlot(SlotName);
        slot.Persistent.Value = false;
        slot.GlobalPosition = Where(world);
        slot.AttachComponent<ToolNotice>().Compose(title, text, accent);
    }

    private static float3 Where(World world)
    {
        var root = world.LocalUser?.Root;
        float3 eye = root?.HeadSlot != null ? root.HeadPosition : new float3(0f, 1.6f, 0f);
        float3 ahead = root?.HeadSlot != null ? root.HeadRotation * float3.Backward : float3.Backward;
        return eye + ahead * Ahead - float3.Up * BelowEyes;
    }

    private void Compose(string title, string text, color accent)
    {
        var theme = NameplateTheme.For(World);
        Slot.AttachComponent<FaceLocalUser>();

        var lines = Wrap(text, LineChars);
        float height = Pad * 2f + TitleSize * 1.4f + lines.Count * LineHeight;
        float top = height * 0.5f - Pad;
        float left = -Width * 0.5f + Pad + StripeWidth;

        AddQuad("Panel", new float2(Width, height), new float3(0f, 0f, -0.002f), PanelColor, Radius, theme);
        AddQuad("Stripe", new float2(StripeWidth, height - Radius * 2f), new float3(-Width * 0.5f + StripeWidth * 1.5f, 0f, -0.001f),
            accent, StripeWidth * 0.5f, theme);

        AddText("Title", title, new float3(left, top - TitleSize * 0.6f, 0f), TitleSize, accent, theme);
        AddText("Body", string.Join("\n", lines), new float3(left, top - TitleSize * 1.4f - LineHeight * 0.5f, 0f), BodySize, DashTheme.Text, theme)
            .VerticalAlign.Value = TextVerticalAlignment.Top;
    }

    private void AddQuad(string name, in float2 size, in float3 at, in color tint, float radius, NameplateTheme? theme)
    {
        var slot = Slot.AddSlot(name);
        slot.LocalPosition.Value = at;
        var mesh = slot.AttachComponent<LumoraMeshes.RoundedQuadMesh>();
        mesh.Size.Value = size;
        mesh.CornerRadius.Value = radius;
        mesh.Color = tint;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        if (theme?.PlateMaterial != null)
            renderer.Material.Target = theme.PlateMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    private TextRenderer AddText(string name, string content, in float3 at, float size, in color ink, NameplateTheme? theme)
    {
        var slot = Slot.AddSlot(name);
        slot.LocalPosition.Value = at;
        var label = slot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            label.Font.Target = theme.Font;
        label.Size.Value = size;
        label.Color.Value = ink;
        label.HorizontalAlign.Value = TextHorizontalAlignment.Left;
        label.VerticalAlign.Value = TextVerticalAlignment.Middle;
        label.Text.Value = content;
        return label;
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
            lines.Add(line.ToString());
        return lines;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        _age += delta;
        if (_age < Linger)
            return;
        float left = 1f - (_age - Linger) / Fold;
        if (left <= 0f)
        {
            Slot.Destroy();
            return;
        }
        Slot.LocalScale.Value = new float3(1f, left, 1f);
    }
}
