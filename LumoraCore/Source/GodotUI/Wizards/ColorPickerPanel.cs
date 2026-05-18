// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.GodotUI.Wizards;

/// <summary>
/// Standalone in-world color picker panel for editing any Sync&lt;color&gt; field.
/// More general than GodotMaterialColorPicker — works with any component field
/// via TargetMemberOwner + TargetMemberName, or via the SetTarget() convenience method.
/// </summary>
[ComponentCategory("GodotUI/Wizards")]
public sealed class ColorPickerPanel : GodotUIPanel
{
    protected override string DefaultScenePath => LumAssets.UI.ColorPickerPanel;
    protected override float2 DefaultSize => new float2(360, 500);
    protected override float DefaultPixelsPerUnit => 900f;
    protected override float DefaultRefreshRate => 0f; // drive every frame for live preview

    // ── Target binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Component that owns the field being edited. Pair with TargetMemberName.
    /// </summary>
    public readonly SyncRef<Component> TargetMemberOwner;

    /// <summary>
    /// Name of the ISyncMember on TargetMemberOwner to edit.
    /// If empty, CurrentColor is used as a free-standing value.
    /// </summary>
    public readonly Sync<string> TargetMemberName;

    // ── Live color value (what the Godot scene binds to) ─────────────────────

    /// <summary>The color currently being shown/edited.</summary>
    public readonly Sync<color> CurrentColor;

    /// <summary>Whether to expose alpha channel controls in the UI.</summary>
    public readonly Sync<bool> ShowAlpha;

    /// <summary>Whether to allow HDR (values &gt; 1) via a brightness multiplier.</summary>
    public readonly Sync<bool> AllowHDR;

    /// <summary>Label shown in the panel header (e.g. field name).</summary>
    public readonly Sync<string> Label;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired whenever the user changes the color. Write-back is automatic when a target is bound.</summary>
    public event Action<color>? OnColorChanged;

    /// <summary>Fired when the user confirms (OK / closes picker).</summary>
    public event Action<color>? OnColorConfirmed;

    // ── Private state ─────────────────────────────────────────────────────────

    private ISyncMember? _boundMember;

    public override void OnAwake()
    {
        base.OnAwake();

        TargetMemberOwner.OnTargetChange += _ => RebindTarget();
        TargetMemberName.OnChanged += _ => RebindTarget();
        CurrentColor.OnChanged += ApplyToTarget;
    }

    public override void OnInit()
    {
        base.OnInit();
        TargetMemberName.Value = string.Empty;
        CurrentColor.Value = color.White;
        ShowAlpha.Value = true;
        AllowHDR.Value = false;
        Label.Value = "Color";
    }

    public override void OnAttach()
    {
        base.OnAttach();

        if (Slot.GetComponent<Grabbable>() == null)
            Slot.AttachComponent<Grabbable>();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Convenience: bind this picker to a specific sync member on a component.
    /// Reads the current value and populates CurrentColor.
    /// </summary>
    public void SetTarget(Component owner, string memberName, string? label = null)
    {
        TargetMemberOwner.Target = owner;
        TargetMemberName.Value = memberName;
        Label.Value = label ?? memberName;
    }

    /// <summary>
    /// Bind directly to a known Sync&lt;color&gt; member without a component lookup.
    /// </summary>
    public void SetTargetDirect(ISyncMember member, string? label = null)
    {
        _boundMember = member;
        Label.Value = label ?? member.Name ?? "Color";
        ConfigureForBoundMember();
        SyncColorFromMember();
    }

    // ── Internal binding ──────────────────────────────────────────────────────

    private void RebindTarget()
    {
        _boundMember = null;

        var owner = TargetMemberOwner.Target;
        var memberName = TargetMemberName.Value;
        if (owner == null || string.IsNullOrEmpty(memberName)) return;

        var field = owner.TryGetField(memberName);
        if (field is ISyncMember m)
        {
            _boundMember = m;
            ConfigureForBoundMember();
            SyncColorFromMember();
        }
    }

    private void ConfigureForBoundMember()
    {
        ShowAlpha.Value = true;
        AllowHDR.Value = _boundMember is IField field && field.ValueType == typeof(colorHDR);
    }

    private void SyncColorFromMember()
    {
        if (_boundMember == null) return;
        var raw = _boundMember.GetValueAsObject();
        if (raw is color c)
        {
            // Suppress write-back while syncing from source
            CurrentColor.OnChanged -= ApplyToTarget;
            CurrentColor.Value = c;
            CurrentColor.OnChanged += ApplyToTarget;
        }
        else if (raw is colorHDR h)
        {
            // color stores floats too; keeping HDR values here lets the Godot picker intensity edit them.
            CurrentColor.OnChanged -= ApplyToTarget;
            CurrentColor.Value = new color(h.r, h.g, h.b, h.a);
            CurrentColor.OnChanged += ApplyToTarget;
        }
    }

    private void ApplyToTarget(color newColor)
    {
        if (_boundMember != null)
        {
            if (_boundMember is IField field && field.ValueType == typeof(colorHDR))
            {
                SyncMemberEditorBuilder.SetColorHDR(_boundMember, new colorHDR(newColor.r, newColor.g, newColor.b, newColor.a));
            }
            else
            {
                SyncMemberEditorBuilder.SetColor(_boundMember, newColor);
            }
        }

        OnColorChanged?.Invoke(newColor);
    }

    // ── GetUIData for Godot scene ─────────────────────────────────────────────

    public override Dictionary<string, string> GetUIData()
    {
        var c = CurrentColor.Value;
        return new Dictionary<string, string>
        {
            ["Label"] = Label.Value,
            ["HexValue"] = $"#{F2X(c.r)}{F2X(c.g)}{F2X(c.b)}{F2X(c.a)}",
            ["R"] = c.r.ToString("F4"),
            ["G"] = c.g.ToString("F4"),
            ["B"] = c.b.ToString("F4"),
            ["A"] = c.a.ToString("F4"),
            ["ShowAlpha"] = ShowAlpha.Value ? "1" : "0",
            ["AllowHDR"] = AllowHDR.Value ? "1" : "0",
        };
    }

    public override Dictionary<string, color> GetUIColors()
    {
        return new Dictionary<string, color>
        {
            ["Preview"] = CurrentColor.Value,
        };
    }

    // ── Button / input routing from Godot scene ───────────────────────────────

    public override void HandleButtonPress(string buttonPath)
    {
        if (buttonPath.EndsWith("ConfirmButton"))
        {
            OnColorConfirmed?.Invoke(CurrentColor.Value);
            Close();
            return;
        }

        base.HandleButtonPress(buttonPath);
    }

    /// <summary>
    /// Called by the Godot scene when the user drags a color slider.
    /// channel: "R", "G", "B", or "A". value: 0-1 (or > 1 if AllowHDR).
    /// </summary>
    public void SetChannel(string channel, float value)
    {
        var c = CurrentColor.Value;
        CurrentColor.Value = channel switch
        {
            "R" => new color(value, c.g, c.b, c.a),
            "G" => new color(c.r, value, c.b, c.a),
            "B" => new color(c.r, c.g, value, c.a),
            "A" => new color(c.r, c.g, c.b, value),
            _ => c,
        };
    }

    /// <summary>
    /// Called by the Godot scene when the user types a hex string (e.g. "FF8040FF").
    /// </summary>
    public void SetFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length is not (6 or 8)) return;
        try
        {
            float r = Convert.ToInt32(hex[..2], 16) / 255f;
            float g = Convert.ToInt32(hex[2..4], 16) / 255f;
            float b = Convert.ToInt32(hex[4..6], 16) / 255f;
            float a = hex.Length == 8 ? Convert.ToInt32(hex[6..8], 16) / 255f : 1f;
            CurrentColor.Value = new color(r, g, b, a);
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string F2X(float f)
    {
        int v = System.Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        return v.ToString("X2");
    }
}
