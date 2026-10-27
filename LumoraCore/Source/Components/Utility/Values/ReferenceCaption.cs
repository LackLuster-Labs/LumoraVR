// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Says what a reference points at: the words, colour and icon of the first case naming that element, or of
// Otherwise when none does, written to a label, a tint and an icon. Which outfit a material slot wears, on
// the menu entry that swaps it. -xlinka
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
[ComponentGenericTypes(GenericTypeGroup.WorldElements, typeof(Component))]
public class ReferenceCaption<T> : Component where T : class, IWorldElement
{
    public readonly SyncRef<SyncRef<T>> Source;
    // Shows Otherwise whatever the reference.
    public readonly Sync<bool> ShowOtherwise;
    public readonly SyncList<ReferenceCaptionCase<T>> Cases;
    public readonly ReferenceCaptionCase<T> Otherwise = new();
    public readonly FieldDrive<string> Label;
    public readonly FieldDrive<colorHDR> Tint;
    // The same tint onto a plain colour field: a button's or an image's, which take color.
    public readonly FieldDrive<color> PlainTint;
    public readonly DriveRef<IAssetProvider<TextureAsset>> Icon;

    public ReferenceCaption()
    {
        Source = new SyncRef<SyncRef<T>>(this);
        ShowOtherwise = new Sync<bool>(this, false);
        Cases = new SyncList<ReferenceCaptionCase<T>>();
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
            var current = source.Target;
            for (int i = 0; i < Cases.Count; i++)
            {
                var entry = Cases[i];
                if (entry != null && ReferenceEquals(entry.Match.Target, current))
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
