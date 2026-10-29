// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Linq;
using Helio.UI;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Scripting;

// Keeps a script's inspector section true to its graph. The section is built once, and the moment someone
// presses Pack or Unpack on it is exactly when what it shows stops being true, so this sits on the section's
// own body while the section is open and builds the body again whenever the script's state reads differently.
// Only the machine that built the body rebuilds it, four times a second at most: a person reads it after
// pressing, nothing faster helps. -xlinka
[HideInInspector]
[ComponentCategory("Hidden")]
public sealed class ScriptGraphReadout : Component
{
    private const double Interval = 0.25;

    public readonly SyncRef<ScriptHost> Host = new();

    private double _next;
    private string? _shown;

    // On the body a section is built into: a readout for the host, told what the body shows now.
    internal static void Watch(UIBuilder ui, ScriptHost host)
    {
        var body = ui.Root;
        if (body == null || body.IsDestroyed || host == null)
            return;
        var readout = body.GetComponent<ScriptGraphReadout>();
        if (readout == null)
        {
            readout = body.AttachComponent<ScriptGraphReadout>();
            readout.Persistent = false;
            readout.Host.Target = host;
        }
        readout._shown = host.InspectorState();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var body = Slot;
        if (Host.Target is not { IsDestroyed: false } host || body == null || body.IsDestroyed)
        {
            Slot?.RemoveComponent(this);
            return;
        }
        if (!IsMintedHere)
            return;
        double now = World.Time.TotalTime;
        if (now < _next)
            return;
        _next = now + Interval;
        if (host.InspectorState() == _shown)
            return;

        foreach (var child in body.Children.ToList())
            child.Destroy();
        var ui = new UIBuilder(body);
        InspectorUI.ApplyTheme(ui, body);
        try
        {
            host.BuildInspectorBody(ui);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"ScriptGraphReadout: the section could not be built again: {ex.Message}");
            _shown = host.InspectorState();
        }
    }
}
