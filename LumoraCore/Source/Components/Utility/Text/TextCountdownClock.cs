// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Utility;

// Counts down to a shared instant and drives the remaining time into a text field.
//
// The deadline is an instant on the session clock rather than a number of seconds ticked down per
// frame, so every peer reads the same time left, a late joiner is immediately correct, and a stalled
// frame cannot make one person's timer drift behind everyone else's. -xlinka
[ComponentCategory("Utility/Text")]
[DefaultUpdateOrder(-100)]
public class TextCountdownClock : Component
{
    // The instant the countdown reaches zero.
    public readonly SyncTimeAnchor Deadline = new();

    // What Restart counts down from, in seconds.
    public readonly Sync<double> Duration;

    // Keep counting past zero, into negative time.
    public readonly Sync<bool> AllowNegative;

    public readonly Sync<bool> ShowTenths;

    public readonly FieldDrive<string> Text;

    private string? _formatted;
    private long _lastTick = long.MinValue;

    public TextCountdownClock()
    {
        Duration = new Sync<double>(this, 60d);
        AllowNegative = new Sync<bool>(this, false);
        ShowTenths = new Sync<bool>(this, false);
        Text = new FieldDrive<string>(this) { LocalValueOnly = true };
    }

    // Seconds left. Counts the full duration while the deadline is unset, so a countdown that has never
    // been started reads as ready rather than as finished.
    public double Remaining
    {
        get
        {
            if (!Deadline.IsSet)
                return Duration.Value;
            double left = -Deadline.Elapsed;
            return AllowNegative.Value || left > 0d ? left : 0d;
        }
    }

    public bool IsFinished => Deadline.IsSet && Deadline.Elapsed >= 0d;

    [SyncMethod]
    [ScriptMethod]
    public void Restart()
    {
        // The anchor replicates, so one peer sets it and everyone counts from it. Two peers restarting
        // at once would otherwise write two different instants.
        if (World?.IsAuthority == true)
            Deadline.SetIn(Duration.Value);
    }

    [SyncMethod]
    [ScriptMethod]
    public void Stop()
    {
        if (World?.IsAuthority == true)
            Deadline.Clear();
    }

    public override void OnUpdate(float delta)
    {
        if (!Text.IsLinkValid)
            return;

        double remaining = Remaining;
        // Rebuild only when the DISPLAYED value moves. At tenths that is ten strings a second instead of
        // one per frame, and at whole seconds it is one.
        double resolution = ShowTenths.Value ? 10d : 1d;
        long tick = (long)System.Math.Floor(remaining * resolution);
        if (tick != _lastTick)
        {
            _lastTick = tick;
            _formatted = Render(remaining);
        }

        Text.SetValue(_formatted!);
    }

    private string Render(double remaining)
    {
        bool negative = remaining < 0d;
        double magnitude = negative ? -remaining : remaining;

        int hours = (int)(magnitude / 3600d);
        int minutes = (int)(magnitude / 60d) % 60;
        int seconds = (int)magnitude % 60;
        string sign = negative ? "-" : string.Empty;

        string body = hours > 0
            ? $"{sign}{hours}:{minutes:00}:{seconds:00}"
            : $"{sign}{minutes}:{seconds:00}";

        if (!ShowTenths.Value)
            return body;

        int tenths = (int)(magnitude * 10d) % 10;
        return $"{body}.{tenths}";
    }
}
