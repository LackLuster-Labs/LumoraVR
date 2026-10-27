// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Helio.UI;
using LumoraMeshes = Lumora.Core.Components.Meshes;

namespace Lumora.Core.Components;

// A quiet word when a favourite could not be used and the built-in one stands in for it. It is this
// machine's business only, so it lives on a local slot: nobody else in the world sees it. It comes up
// beside the thing that was made (in front of the user when there is nothing to stand by), holds a few
// seconds, folds away and is gone. The pink stripe down its left edge is the favourites colour, the same
// pink as the badge on a favourite's inventory card. -xlinka
[ComponentCategory("Hidden")]
public sealed class FavouriteNotice : Component
{
    private const float Width = 0.46f;
    private const float LineHeight = 0.024f;
    private const float Pad = 0.022f;
    private const float StripeWidth = 0.008f;
    private const float Radius = 0.016f;
    private const float TitleSize = 0.019f;
    private const float BodySize = 0.015f;
    private const int LineChars = 46;
    private const float Linger = 6f;
    private const float Fold = 0.3f;
    private const float BelowEyes = 0.18f;
    private const float Ahead = 0.85f;

    private static readonly color PanelColor = DashTheme.Panel.WithAlpha(0.95f);
    private static readonly color Pink = DashTheme.IconPink;

    private float _age;

    // Must run on the world's thread.
    public static void Show(World? world, Slot? near, string title, string text)
    {
        Logging.Logger.Log($"Favourites: {title}. {text}");
        ShowLocal(world, near, title, text);
    }

    internal static void ShowLocal(World? world, Slot? near, string title, string text)
    {
        if (world?.RootSlot == null || world.IsDestroyed || world.LocalUser == null)
            return;
        var slot = world.RootSlot.AddLocalSlot("Favourite Notice");
        slot.Persistent.Value = false;
        slot.GlobalPosition = Where(world, near);
        var notice = slot.AttachComponent<FavouriteNotice>();
        notice.Compose(title, text);
    }

    // Between the thing and the user's eyes, a little under them; in front of the user when there is no thing.
    private static float3 Where(World world, Slot? near)
    {
        var root = world.LocalUser?.Root;
        float3 eye = root?.HeadSlot != null ? root.HeadPosition : new float3(0f, 1.6f, 0f);
        float3 ahead = root?.HeadSlot != null ? root.HeadRotation * float3.Backward : float3.Backward;
        float3 spot = eye + ahead * Ahead;
        if (near != null && !near.IsDestroyed)
        {
            float3 toward = eye - near.GlobalPosition;
            float distance = toward.Length;
            if (distance > 0.05f)
                spot = near.GlobalPosition + toward / distance * MathF.Min(0.45f, distance * 0.6f);
        }
        return spot - float3.Up * BelowEyes;
    }

    private void Compose(string title, string text)
    {
        var theme = NameplateTheme.For(World);
        Slot.AttachComponent<FaceLocalUser>();

        var lines = Wrap(text, LineChars);
        float height = Pad * 2f + TitleSize * 1.4f + lines.Count * LineHeight;
        float top = height * 0.5f - Pad;
        float left = -Width * 0.5f + Pad + StripeWidth;

        AddQuad("Panel", new float2(Width, height), new float3(0f, 0f, -0.002f), PanelColor, Radius, theme);
        AddQuad("Stripe", new float2(StripeWidth, height - Radius * 2f), new float3(-Width * 0.5f + StripeWidth * 1.5f, 0f, -0.001f),
            Pink, StripeWidth * 0.5f, theme);

        AddText("Title", title, new float3(left, top - TitleSize * 0.6f, 0f), TitleSize, Pink, theme);
        var body = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                body.Append('\n');
            body.Append(lines[i]);
        }
        AddText("Body", body.ToString(), new float3(left, top - TitleSize * 1.4f - LineHeight * 0.5f, 0f), BodySize, DashTheme.Text, theme)
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
        var text = slot.AttachComponent<TextRenderer>();
        if (theme?.Font != null)
            text.Font.Target = theme.Font;
        text.Size.Value = size;
        text.Color.Value = ink;
        text.HorizontalAlign.Value = TextHorizontalAlignment.Left;
        text.VerticalAlign.Value = TextVerticalAlignment.Middle;
        text.Text.Value = content;
        return text;
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
