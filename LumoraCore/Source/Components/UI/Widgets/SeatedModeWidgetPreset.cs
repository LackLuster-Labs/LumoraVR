// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// Seated mode on the desk. Playing in a chair, on lifts you so your avatar stands at your full height. Turning
// it on measures where the headset is, so off and on again measures again, and the line under the title says
// so along with how far it lifts. On a screen there is nothing to measure: it says it is for a headset and a
// press cannot turn it on. -xlinka
[ComponentCategory("Hidden")]
public sealed class SeatedModeWidgetPreset : ToggleWidgetPreset
{
    // Two lines, the title and what it is doing.
    public const float CardHeight = 54f;

    protected override string Title => "Widget.SeatedMode.Title".AsLocale("Seated mode").Resolve();
    protected override string? Icon => "seated";
    protected override color IconTint => DashTheme.Positive;
    protected override bool StateLine => true;

    // Whatever changes the line with the switch where it is: a headset coming or going, the lift, the units.
    protected override int StateRevision
        => (SeatedMode.InHeadset ? 1 : 0)
           | (EngineSettings.ImperialUnits ? 2 : 0)
           | (int)MathF.Round(SeatedMode.Lift * 1000f) << 2;

    protected override bool Value
    {
        get => SeatedMode.Enabled;
        set => SeatedMode.SetEnabled(value);
    }

    protected override string StateText(bool on)
    {
        if (!SeatedMode.InHeadset)
        {
            return (on
                ? "Widget.SeatedMode.InHeadset".AsLocale("Applies in a headset")
                : "Widget.SeatedMode.HeadsetOnly".AsLocale("For playing in a headset")).Resolve();
        }
        if (!on)
            return "Widget.SeatedMode.SitThenTurnOn".AsLocale("Sit as you play, then turn on").Resolve();
        float lift = SeatedMode.Lift;
        if (lift < 0.01f)
            return "Widget.SeatedMode.FullHeight".AsLocale("Already at full height").Resolve();
        return "Widget.SeatedMode.Lifted".AsLocale("Lifted {0}. Off and on re-measures", Distance(lift)).Resolve();
    }

    private static string Distance(float metres)
        => EngineSettings.ImperialUnits
            ? $"{(int)MathF.Round(metres / 0.0254f)} in"
            : $"{(int)MathF.Round(metres * 100f)} cm";
}
