// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using Helio.UI;
using Lumora.Core;

namespace Lumora.Core.Components;

// Slider for a numeric member carrying [Range], beside a number field in the range's text format. The field
// types like every other number in the inspector (math against what is there, Enter or a click away keeps
// it, Escape puts it back), held to the range and to whole numbers where the slider is. -xlinka
[ComponentCategory("Utility/Inspectors")]
public class SliderMemberEditor : MemberEditor
{
    public readonly Sync<float> Min;
    public readonly Sync<float> Max;
    public readonly Sync<bool> WholeNumbers;
    public readonly Sync<string> Format;

    private readonly SyncRef<Slider> _slider;
    private readonly SyncRef<TextInput> _valueInput;

    public SliderMemberEditor()
    {
        Min = new Sync<float>(this, 0f);
        Max = new Sync<float>(this, 1f);
        WholeNumbers = new Sync<bool>(this, false);
        Format = new Sync<string>(this, "");
        _slider = new SyncRef<Slider>(this);
        _valueInput = new SyncRef<TextInput>(this);
    }

    protected override void BuildUI(UIBuilder ui)
    {
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        _slider.Target = ui.Slider(ReadFloat(), Min.Value, Max.Value, OnSlid);
        ui.PopStyle();

        ui.PushStyle();
        ui.MinWidth(64f);
        ui.PreferredWidth(64f);
        ui.FlexibleWidth(0f);
        var input = InspectorUI.CreateTextInput(ui, "Value", TextHorizontalAlignment.Center);
        input.SetSubmitAction(OnTyped);
        input.SubmitOnBlur.Value = true;
        input.Tip.Value = FieldMath.Tip;
        input.FocusLost += OnFocusLost;
        _valueInput.Target = input;
        BindStateTint(input.Slot?.GetComponent<Image>()?.Tint, InspectorUI.RowColor);
        ui.PopStyle();
    }

    public override void OnStart()
    {
        base.OnStart();
        // A plain local event: hooked on every peer, the builder only ran on one.
        if (_valueInput.Target is { IsDestroyed: false } input)
        {
            input.FocusLost -= OnFocusLost;
            input.FocusLost += OnFocusLost;
        }
    }

    public override void OnDestroy()
    {
        if (_valueInput.Target is { } input)
            input.FocusLost -= OnFocusLost;
        base.OnDestroy();
    }

    // Escape, or a blur that typed nothing: the field shows the value again. A kept edit arrives in OnTyped.
    private void OnFocusLost(TextInput input, string text) => RefreshDisplay();

    protected override void RefreshDisplay()
    {
        float value = ReadFloat();
        var slider = _slider.Target;
        if (slider != null && !slider.IsDestroyed && MathF.Abs(slider.Value.Value - value) > 1e-5f)
            slider.Value.Value = value;
        var input = _valueInput.Target;
        if (input != null && !input.IsDestroyed && !input.IsFocused)
        {
            FieldHint.Clear(input);
            input.Text.Value = Shown(value);
        }
    }

    private string Shown(float value)
    {
        string format = string.IsNullOrEmpty(Format.Value) ? (WholeNumbers.Value ? "0" : "0.###") : Format.Value;
        try { return value.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    private float ReadFloat()
    {
        var value = GetMemberValue();
        try { return value == null ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        catch { return 0f; }
    }

    [SyncMethod]
    public void OnSlid(Slider slider, float value)
    {
        var leafType = LeafType;
        if (leafType == null)
            return;
        if (WholeNumbers.Value)
            value = MathF.Round(value);
        try { SetMemberValue(Convert.ChangeType(value, leafType, CultureInfo.InvariantCulture)); }
        catch { }
    }

    // Text that still reads as the value shown writes nothing, so a field only visited trims nothing.
    [SyncMethod]
    public void OnTyped(TextInput input, string text)
    {
        var leafType = LeafType;
        if (leafType == null || !FieldMath.IsNumberType(leafType))
            return;
        object? current = GetMemberValue();
        if (string.Equals((text ?? "").Trim(), Shown(ReadFloat()), StringComparison.Ordinal))
        {
            RefreshDisplay();
            return;
        }
        if (!FieldMath.TryConvert(text, leafType, current, out object? typed, out var result))
        {
            RefreshDisplay();
            if (result.Error != FieldMathError.Empty && !IsReadOnly && input != null && !input.IsDestroyed)
                FieldHint.Show(input, text ?? "", result.Hint, RefreshDisplay);
            return;
        }
        float value;
        try { value = Convert.ToSingle(typed, CultureInfo.InvariantCulture); }
        catch { RefreshDisplay(); return; }
        float low = MathF.Min(Min.Value, Max.Value), high = MathF.Max(Min.Value, Max.Value);
        value = System.Math.Clamp(value, low, high);
        if (WholeNumbers.Value)
            value = MathF.Round(value);
        try
        {
            var written = Convert.ChangeType(value, leafType, CultureInfo.InvariantCulture);
            if (!Equals(written, current))
                SetMemberValue(written);
        }
        catch { }
        RefreshDisplay();
    }
}
