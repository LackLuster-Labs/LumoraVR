using System;
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
    public Sync<string> Text { get; private set; }

    /// <summary>
    /// Size of the text.
    /// </summary>
    public Sync<float> Size { get; private set; }

    /// <summary>
    /// Color of the text.
    /// </summary>
    public Sync<float4> Color { get; private set; }

    /// <summary>
    /// Font to use for rendering.
    /// </summary>
    public Sync<string> Font { get; private set; }

    /// <summary>
    /// Whether the text should always face the camera.
    /// </summary>
    public Sync<bool> Billboard { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        Text = new Sync<string>(this, "Hello World");
        Size = new Sync<float>(this, 1.0f);
        Color = new Sync<float4>(this, new float4(1, 1, 1, 1)); // White
        Font = new Sync<string>(this, "Arial");
        Billboard = new Sync<bool>(this, true);

        // Initialize sync members created in OnAwake
        InitializeNewSyncMembers();

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
        Size.Value = System.Math.Max(0.01f, size);
    }

    /// <summary>
    /// Set the text color.
    /// </summary>
    public void SetColor(float4 color)
    {
        Color.Value = color;
    }
}
