using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI;

/// <summary>
/// Generic Godot UI panel that renders a .tscn scene to a 3D quad.
/// Can be used directly or extended for specific panel types.
/// </summary>
[ComponentCategory("GodotUI")]
public class GodotUIPanel : ImplementableComponent
{
    /// <summary>
    /// Path to the Godot scene file to load (e.g. "res://Scenes/UI/MyPanel.tscn").
    /// </summary>
    public Sync<string> ScenePath { get; private set; } = null!;

    /// <summary>
    /// Size of the UI canvas in pixels.
    /// </summary>
    public Sync<float2> Size { get; private set; } = null!;

    /// <summary>
    /// Pixels per unit for world scale.
    /// </summary>
    public Sync<float> PixelsPerUnit { get; private set; } = null!;

    /// <summary>
    /// How often to refresh live data (in seconds). 0 = every frame.
    /// </summary>
    public Sync<float> RefreshRate { get; private set; } = null!;

    /// <summary>
    /// Resolution multiplier for crisp text (default 2x).
    /// </summary>
    public Sync<int> ResolutionScale { get; private set; } = null!;

    private float _refreshTimer = 0f;

    /// <summary>
    /// Event fired when live data should be refreshed.
    /// </summary>
    public event Action? OnDataRefresh;

    /// <summary>
    /// Event fired when scene is loaded (passes button names for registration).
    /// </summary>
    public event Action<List<string>>? OnSceneLoaded;

    /// <summary>
    /// Event fired when a button is pressed.
    /// </summary>
    public event Action<string>? OnButtonPressed;

    /// <summary>
    /// Default scene path. Override in derived classes.
    /// </summary>
    protected virtual string DefaultScenePath => "";

    /// <summary>
    /// Default panel size in pixels.
    /// </summary>
    protected virtual float2 DefaultSize => new float2(500, 600);

    /// <summary>
    /// Default pixels per unit.
    /// </summary>
    protected virtual float DefaultPixelsPerUnit => 800f;

    /// <summary>
    /// Default refresh rate in seconds.
    /// </summary>
    protected virtual float DefaultRefreshRate => 0.25f;

    /// <summary>
    /// Default resolution scale.
    /// </summary>
    protected virtual int DefaultResolutionScale => 2;

    public override void OnAwake()
    {
        base.OnAwake();
        InitializeSyncMembers();
    }

    private void InitializeSyncMembers()
    {
        ScenePath = new Sync<string>(this, DefaultScenePath);
        Size = new Sync<float2>(this, DefaultSize);
        PixelsPerUnit = new Sync<float>(this, DefaultPixelsPerUnit);
        RefreshRate = new Sync<float>(this, DefaultRefreshRate);
        ResolutionScale = new Sync<int>(this, DefaultResolutionScale);

        ScenePath.OnChanged += _ => NotifyChanged();
        Size.OnChanged += _ => NotifyChanged();
        PixelsPerUnit.OnChanged += _ => NotifyChanged();
        ResolutionScale.OnChanged += _ => NotifyChanged();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        _refreshTimer += delta;
        if (_refreshTimer >= RefreshRate.Value)
        {
            _refreshTimer = 0f;
            OnDataRefresh?.Invoke();
        }
    }

    /// <summary>
    /// Get UI data to populate labels. Override in derived classes.
    /// Returns dictionary of node path -> text value.
    /// </summary>
    public virtual Dictionary<string, string> GetUIData()
    {
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Get UI colors for specific labels. Override in derived classes.
    /// Returns dictionary of node path -> color.
    /// </summary>
    public virtual Dictionary<string, color> GetUIColors()
    {
        return new Dictionary<string, color>();
    }

    /// <summary>
    /// Called when a button in the UI is pressed.
    /// </summary>
    public virtual void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.EndsWith("CloseButton"))
        {
            Close();
            return;
        }
        OnButtonPressed?.Invoke(buttonPath);
    }

    /// <summary>
    /// Notify that scene was loaded.
    /// </summary>
    public void NotifySceneLoaded(List<string> buttonPaths)
    {
        OnSceneLoaded?.Invoke(buttonPaths);
    }

    /// <summary>
    /// Close the panel.
    /// </summary>
    public virtual void Close()
    {
        Slot.Destroy();
    }

}
