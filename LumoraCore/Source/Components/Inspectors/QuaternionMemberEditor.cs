// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Localization;

namespace Lumora.Core.Components;

// Fields are in DEGREES. floatQ.Euler takes radians; the conversion lives here so nobody types 90 and
// gets 90 radians. Each angle takes math against what it shows now ("+90" turns a quarter more) and is kept
// on Enter or a click away; Escape puts it back.
[ComponentCategory("Utility/Inspectors")]
public class QuaternionMemberEditor : MemberEditor
{
    private readonly SyncRef<TextInput> _yaw;
    private readonly SyncRef<TextInput> _pitch;
    private readonly SyncRef<TextInput> _roll;
    private bool _focusHooked;

    public QuaternionMemberEditor()
    {
        _yaw = new SyncRef<TextInput>(this);
        _pitch = new SyncRef<TextInput>(this);
        _roll = new SyncRef<TextInput>(this);
    }

    protected override void BuildUI(UIBuilder ui)
    {
        // Same columns as a vector's fields, so Rotation's boxes sit under Position's and the axis letters.
        InspectorUI.Columns(ui, "Angles");
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        _yaw.Target = InspectorUI.CreateTextInput(ui, "Yaw", TextHorizontalAlignment.Center);
        _pitch.Target = InspectorUI.CreateTextInput(ui, "Pitch", TextHorizontalAlignment.Center);
        _roll.Target = InspectorUI.CreateTextInput(ui, "Roll", TextHorizontalAlignment.Center);
        ui.PopStyle();
        ui.NestOut();

        foreach (var input in new[] { _yaw.Target, _pitch.Target, _roll.Target })
        {
            input.SetSubmitAction(OnSubmitted);
            input.SubmitOnBlur.Value = true;
            input.Tip.Value = FieldMath.Tip;
        }
        HookFocus();
    }

    public override void OnStart()
    {
        base.OnStart();
        HookFocus();
    }

    public override void OnDestroy()
    {
        if (_focusHooked)
        {
            foreach (var input in new[] { _yaw.Target, _pitch.Target, _roll.Target })
            {
                if (input != null)
                    input.FocusLost -= OnFocusLost;
            }
        }
        base.OnDestroy();
    }

    // Local events, so every peer hooks them (the builder only ran on the authority).
    private void HookFocus()
    {
        if (_focusHooked || _yaw.Target == null || _pitch.Target == null || _roll.Target == null)
            return;
        _focusHooked = true;
        _yaw.Target.FocusLost += OnFocusLost;
        _pitch.Target.FocusLost += OnFocusLost;
        _roll.Target.FocusLost += OnFocusLost;
    }

    // Escape or a blur that typed nothing: show the angle again. A kept edit lands in OnSubmitted next.
    private void OnFocusLost(TextInput input, string text) => RefreshDisplay();

    protected override void RefreshDisplay()
    {
        if (GetMemberValue() is not floatQ rotation)
            return;
        // Radians: x is pitch, y is yaw, z is roll. Submit hands them back to Euler as yaw, pitch, roll.
        var euler = rotation.ToEuler();
        WriteField(_yaw.Target, euler.y);
        WriteField(_pitch.Target, euler.x);
        WriteField(_roll.Target, euler.z);
    }

    private static void WriteField(TextInput? input, float radians)
    {
        if (input == null || input.IsDestroyed || input.IsFocused)
            return;
        FieldHint.Clear(input);
        input.Text.Value = Degrees(radians);
    }

    private int InputAxis(TextInput? input)
        => input == null ? -1 : ReferenceEquals(input, _yaw.Target) ? 0 : ReferenceEquals(input, _pitch.Target) ? 1 : ReferenceEquals(input, _roll.Target) ? 2 : -1;

    // Each angle box carries degrees, while a card from the row label carries the whole rotation. -xlinka
    public override IGrabbable? CopyValue(TextInput input, in float3 spawnPoint)
    {
        int axis = InputAxis(input);
        if (axis < 0 || GetMemberValue() is not floatQ rotation)
            return base.CopyValue(input, spawnPoint);
        var euler = rotation.ToEuler();
        float angle = (axis == 0 ? euler.y : axis == 1 ? euler.x : euler.z) * (180f / MathF.PI);
        return ValueProxy.Spawn(World, typeof(float), angle, spawnPoint);
    }

    protected override bool TryFit(IField field, object? value, Type type, TextInput? input,
        out string path, out object? fitted, out LocaleText refusal)
    {
        int axis = InputAxis(input);
        if (axis >= 0 && FieldMath.IsNumberType(type) && GetMemberValue() is floatQ rotation)
        {
            path = MemberPath.Value ?? "";
            fitted = null;
            if (!ValueCardConversion.TryConvert(value, type, typeof(float), out var converted, out refusal))
                return false;
            var euler = rotation.ToEuler();
            float degrees = (float)converted!;
            if (!float.IsFinite(degrees))
            {
                refusal = ValueCardConversion.NotFinite;
                return false;
            }
            float angle = degrees * (MathF.PI / 180f);
            fitted = floatQ.Euler(axis == 0 ? angle : euler.y, axis == 1 ? angle : euler.x, axis == 2 ? angle : euler.z);
            return true;
        }
        return base.TryFit(field, value, type, input, out path, out fitted, out refusal);
    }

    private static string Degrees(float radians)
        => (radians * (180f / MathF.PI)).ToString("0.##", CultureInfo.InvariantCulture);

    // One angle changes; the other two stay what the rotation holds now, not what their boxes read.
    [SyncMethod]
    public void OnSubmitted(TextInput input, string text)
    {
        if (GetMemberValue() is not floatQ rotation)
            return;
        var euler = rotation.ToEuler();
        int axis = ReferenceEquals(input, _yaw.Target) ? 0 : ReferenceEquals(input, _pitch.Target) ? 1 : ReferenceEquals(input, _roll.Target) ? 2 : -1;
        if (axis < 0)
            return;
        float shownRadians = axis == 0 ? euler.y : axis == 1 ? euler.x : euler.z;
        float shown = shownRadians * (180f / MathF.PI);
        if (string.Equals((text ?? "").Trim(), Degrees(shownRadians), StringComparison.Ordinal))
        {
            RefreshDisplay();
            return;
        }
        var result = FieldMath.Evaluate(text, shown);
        if (!result.Ok || !float.IsFinite((float)result.Value))
        {
            RefreshDisplay();
            if (result.Error != FieldMathError.Empty && !IsReadOnly && input != null && !input.IsDestroyed)
                FieldHint.Show(input, text ?? "", result.Ok ? new FieldMath.Result(0d, FieldMathError.NotFinite).Hint : result.Hint, RefreshDisplay);
            return;
        }
        const float DegToRad = MathF.PI / 180f;
        float yaw = euler.y, pitch = euler.x, roll = euler.z;
        float typed = (float)result.Value * DegToRad;
        if (axis == 0) yaw = typed; else if (axis == 1) pitch = typed; else roll = typed;
        SetMemberValue(floatQ.Euler(yaw, pitch, roll));
        RefreshDisplay();
    }
}
