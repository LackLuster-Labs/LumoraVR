// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Utility;

// Says what a value is: the words, colour and icon of the first case it matches, or of Otherwise when it
// matches none, written to a label, a tint and an icon. A toggle's "On / Off" and its colour on a button or a
// menu entry, say. -xlinka
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
[ComponentGenericTypes(GenericTypeGroup.Values)]
public class ValueCaption<T> : Component
{
    public readonly SyncRef<IField<T>> Source;
    // Shows Otherwise whatever the value.
    public readonly Sync<bool> ShowOtherwise;
    public readonly SyncList<ValueCaptionCase<T>> Cases;
    public readonly ValueCaptionCase<T> Otherwise = new();
    public readonly FieldDrive<string> Label;
    public readonly FieldDrive<colorHDR> Tint;
    // The same tint onto a plain colour field: a button's or an image's, which take color.
    public readonly FieldDrive<color> PlainTint;
    public readonly DriveRef<IAssetProvider<TextureAsset>> Icon;

    public static bool IsValidGenericType => DrivenValueTypes.IsPrimitive(typeof(T));

    public ValueCaption()
    {
        Source = new SyncRef<IField<T>>(this);
        ShowOtherwise = new Sync<bool>(this, false);
        Cases = new SyncList<ValueCaptionCase<T>>();
        Label = new FieldDrive<string>(this) { LocalValueOnly = true };
        Tint = new FieldDrive<colorHDR>(this) { LocalValueOnly = true };
        PlainTint = new FieldDrive<color>(this) { LocalValueOnly = true };
        Icon = new DriveRef<IAssetProvider<TextureAsset>>(this) { LocalValueOnly = true };
    }

    public override void OnUpdate(float delta)
    {
        var shown = Otherwise;
        if (!ShowOtherwise.Value && Source.Target is { } source)
        {
            var value = source.Value;
            for (int i = 0; i < Cases.Count; i++)
            {
                var entry = Cases[i];
                if (entry != null && SyncCoder.Equals(entry.Match.Value, value))
                {
                    shown = entry;
                    break;
                }
            }
        }
        Label.SetValue(shown.Label.Value);
        Tint.SetValue(shown.Tint.Value);
        var tint = shown.Tint.Value;
        PlainTint.SetValue(new color(tint.r, tint.g, tint.b, tint.a));
        Icon.SetValue(shown.Icon.Target);
    }
}
