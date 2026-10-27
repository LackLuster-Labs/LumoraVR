// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Core.Components.Utility;

// Drives a float with any other number: a count of people onto a range map's bounds, a whole-number setting onto
// a slider. A value that will not convert drives NaN, which reads as wrong rather than as a quiet zero.
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
[ComponentGenericTypes(typeof(int), typeof(long), typeof(uint), typeof(ulong), typeof(short), typeof(ushort),
    typeof(byte), typeof(sbyte), typeof(double), typeof(bool))]
public class NumberToFloat<T> : Component
{
    public readonly SyncRef<IField<T>> Source;

    public readonly FieldDrive<float> Target;

    public static bool IsValidGenericType => typeof(IConvertible).IsAssignableFrom(typeof(T)) && typeof(T) != typeof(string);

    public NumberToFloat()
    {
        Source = new SyncRef<IField<T>>(this);
        Target = new FieldDrive<float>(this) { LocalValueOnly = true };
    }

    public override void OnUpdate(float delta)
    {
        if (!Target.IsLinkValid || Source.Target is not { } source)
            return;
        float value;
        try
        {
            value = source.Value is IConvertible number ? number.ToSingle(CultureInfo.InvariantCulture) : float.NaN;
        }
        catch (Exception)
        {
            value = float.NaN;
        }
        Target.SetValue(value);
    }
}
