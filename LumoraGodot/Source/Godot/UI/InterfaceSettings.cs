// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;

namespace Lumora.Source.Godot.UI;

/// <summary>
/// Runtime-tweakable interface settings (reticle, mouse sensitivity/smoothing, ...).
/// Values are read directly by the corresponding subsystems each frame so changes
/// take effect immediately. Persistence is left to the settings UI.
/// </summary>
public static class InterfaceSettings
{
    public enum ReticleStyle
    {
        Ring,
        Dot,
        Crosshair,
        Off
    }

    private static float _reticleSize = 12f;
    private static float _reticleThickness = 2f;
    private static Color _reticleColor = new(1f, 1f, 1f, 0.6f);
    private static Color _reticleHoverColor = new(0.4f, 1f, 0.4f, 0.85f);
    private static ReticleStyle _reticleStyle = ReticleStyle.Ring;
    private static float _mouseSensitivity = 1f;
    private static float _mouseSmoothing = 0f;

    public static event Action Changed;

    public static float ReticleSize
    {
        get => _reticleSize;
        set { _reticleSize = Mathf.Clamp(value, 2f, 48f); Changed?.Invoke(); }
    }

    public static float ReticleThickness
    {
        get => _reticleThickness;
        set { _reticleThickness = Mathf.Clamp(value, 1f, 8f); Changed?.Invoke(); }
    }

    public static Color ReticleColor
    {
        get => _reticleColor;
        set { _reticleColor = value; Changed?.Invoke(); }
    }

    public static Color ReticleHoverColor
    {
        get => _reticleHoverColor;
        set { _reticleHoverColor = value; Changed?.Invoke(); }
    }

    public static ReticleStyle Style
    {
        get => _reticleStyle;
        set { _reticleStyle = value; Changed?.Invoke(); }
    }

    /// <summary>
    /// Mouse-look sensitivity multiplier (1.0 = engine default).
    /// </summary>
    public static float MouseSensitivity
    {
        get => _mouseSensitivity;
        set { _mouseSensitivity = Mathf.Clamp(value, 0.05f, 10f); Changed?.Invoke(); }
    }

    /// <summary>
    /// Mouse-delta smoothing (0 = raw input, recommended at high refresh rates).
    /// </summary>
    public static float MouseSmoothing
    {
        get => _mouseSmoothing;
        set { _mouseSmoothing = Mathf.Clamp(value, 0f, 0.95f); Changed?.Invoke(); }
    }
}
