// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Touch;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// The glowing part of a lamp. Its glow follows the light it belongs to: the light's colour times its
// intensity while the light is on, black while it is off. Touching it switches the light.
//
// It sits below a grabbable on the same object, so pointing at a lamp still picks it up; a fingertip or a
// press of the beam on the bulb is what switches it. -xlinka
[ComponentCategory("Rendering")]
public sealed class LightBulb : TouchControl
{
    public readonly SyncRef<Light> Light;

    // An emissive colour, usually the one on the bulb's own material.
    public readonly FieldDrive<colorHDR> Glow;

    public readonly Sync<bool> SwitchOnTouch;

    private ChangeFollow? _follow;

    public LightBulb()
    {
        Light = new SyncRef<Light>(this);
        // Every peer has the light and computes the same glow from it.
        Glow = new FieldDrive<colorHDR>(this) { LocalValueOnly = true };
        SwitchOnTouch = new Sync<bool>(this, true);
    }

    public override void OnInit()
    {
        base.OnInit();
        InteractionPriority.Value = -1;
    }

    public override void OnAttach()
    {
        base.OnAttach();
        Light.Target ??= Slot.GetComponent<Light>();
    }

    public override void OnDestroy()
    {
        _follow?.Clear();
        base.OnDestroy();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        var light = Light.Target;
        var follow = _follow ??= new ChangeFollow(this);
        follow.Begin();
        follow.AddRef(Light);
        follow.Add(Glow.Target);
        if (light != null)
        {
            follow.Add(light.Enabled);
            follow.Add(light.LightColor);
            follow.Add(light.Intensity);
        }
        follow.End();

        if (light == null || !Glow.IsLinkValid)
            return;
        follow.BeginQuiet();
        try
        {
            Glow.SetValue(GlowOf(light));
        }
        finally
        {
            follow.EndQuiet();
        }
    }

    public static colorHDR GlowOf(Light light)
    {
        if (!light.Enabled.Value)
            return colorHDR.Black;
        var tint = light.LightColor.Value;
        float intensity = light.Intensity.Value;
        return new colorHDR(tint.r * intensity, tint.g * intensity, tint.b * intensity, tint.a);
    }

    public override bool CanTouch(User? user, TouchProbeKind kind)
        => SwitchOnTouch.Value && Light.Target != null && base.CanTouch(user, kind);

    protected override void OnTouchContact(in TouchContact contact)
    {
        if (contact.Hover == TouchPhase.Begin)
            PulseHover(in contact);
        if (contact.Contact != TouchPhase.Begin)
            return;
        var light = Light.Target;
        if (light == null || !SwitchOnTouch.Value)
            return;
        PulseContact(in contact);
        light.Enabled.Value = !light.Enabled.Value;
    }
}
