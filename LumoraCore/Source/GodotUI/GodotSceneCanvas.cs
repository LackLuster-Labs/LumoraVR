using System;
using Lumora.Core.Math;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Loads a Godot .tscn scene and renders it to a 3D quad.
/// Use this to display pre-built Godot UI scenes in-world.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotSceneCanvas : ImplementableComponent
{
    /// <summary>
    /// Path to the .tscn scene file (e.g., "res://UI/MyPanel.tscn").
    /// </summary>
    public Sync<string> ScenePath { get; private set; } = null!;

    /// <summary>
    /// Size of the viewport in pixels.
    /// </summary>
    public Sync<float2> Size { get; private set; } = null!;

    /// <summary>
    /// Pixels per unit (affects how large the UI appears in world space).
    /// </summary>
    public Sync<float> PixelsPerUnit { get; private set; } = null!;

    /// <summary>
    /// Whether the canvas is interactive (receives input).
    /// </summary>
    public Sync<bool> Interactive { get; private set; } = null!;

    /// <summary>
    /// Whether the background is transparent.
    /// </summary>
    public Sync<bool> TransparentBackground { get; private set; } = null!;

    /// <summary>
    /// Event fired when scene is loaded. Hook can use this to notify components.
    /// </summary>
    public event Action<GodotSceneCanvas>? OnSceneLoaded;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        ScenePath = new Sync<string>(this, "");
        Size = new Sync<float2>(this, new float2(800, 600));
        PixelsPerUnit = new Sync<float>(this, 1000f);
        Interactive = new Sync<bool>(this, true);
        TransparentBackground = new Sync<bool>(this, true);

        ScenePath.OnChanged += _ => NotifyChanged();
        Size.OnChanged += _ => NotifyChanged();
        PixelsPerUnit.OnChanged += _ => NotifyChanged();
        Interactive.OnChanged += _ => NotifyChanged();
        TransparentBackground.OnChanged += _ => NotifyChanged();
    }

    /// <summary>
    /// Called by hook when scene is loaded.
    /// </summary>
    public void NotifySceneLoaded()
    {
        OnSceneLoaded?.Invoke(this);
    }
}
