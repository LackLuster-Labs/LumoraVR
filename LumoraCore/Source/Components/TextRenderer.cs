// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Lumora.Core.Components;

/// <summary>
/// Renders text in 3D space.
/// </summary>
[ComponentCategory("Rendering")]
public class TextRenderer : ImplementableComponent
{
    /// <summary>
    /// The text to render.
    /// </summary>
    public readonly Sync<string> Text = new();

    /// <summary>
    /// Size of the text.
    /// </summary>
    public readonly Sync<float> Size = new();

    /// <summary>
    /// Color of the text.
    /// </summary>
    public readonly Sync<float4> Color = new();

    /// <summary>
    /// Font to use for rendering.
    /// </summary>
    public readonly Sync<string> Font = new();

    /// <summary>
    /// Whether the text should always face the camera.
    /// </summary>
    public readonly Sync<bool> Billboard = new();

    public override void OnInit()
    {
        base.OnInit();

        Text.Value = "Hello World";
        Size.Value = 1.0f;
        Color.Value = new float4(1, 1, 1, 1); // White
        Font.Value = "Arial";
        Billboard.Value = true;
    }

    public override void OnAwake()
    {
        base.OnAwake();

        Logger.Log($"TextRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        Logger.Log($"TextRenderer: Started on slot '{Slot.SlotName.Value}' with text '{Text.Value}'");
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        Logger.Log($"TextRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    /// <summary>
    /// Set the text content.
    /// </summary>
    public void SetText(string text)
    {
        Text.Value = text ?? "";
    }

    /// <summary>
    /// Set the text size.
    /// </summary>
    public void SetSize(float size)
    {
        Size.Value = size < 0.01f ? 0.01f : size;
    }

    /// <summary>
    /// Set the text color.
    /// </summary>
    public void SetColor(float4 color)
    {
        Color.Value = color;
    }
}
