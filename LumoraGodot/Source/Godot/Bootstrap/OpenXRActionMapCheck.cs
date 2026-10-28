// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Says so in the log when the action map this build carries is short of a profile. A runtime only gets
// bindings for the profiles in the map, so a missing one is a controller whose sticks and buttons do
// nothing, and nothing else shows it. The editor once dropped seven of them on a reload (see
// addons/action_map_guard) and six builds shipped that way. One load of the map, off the main thread. -xlinka
public static class OpenXRActionMapCheck
{
    private static readonly string[] Expected =
    {
        "/interaction_profiles/khr/simple_controller",
        "/interaction_profiles/oculus/touch_controller",
        "/interaction_profiles/microsoft/motion_controller",
        "/interaction_profiles/hp/mixed_reality_controller",
        "/interaction_profiles/samsung/odyssey_controller",
        "/interaction_profiles/huawei/controller",
        "/interaction_profiles/htc/vive_controller",
        "/interaction_profiles/htc/vive_cosmos_controller",
        "/interaction_profiles/htc/vive_focus3_controller",
        "/interaction_profiles/htc/vive_tracker_htcx",
        "/interaction_profiles/valve/index_controller",
        "/interaction_profiles/valve/frame_controller_valve",
        "/interaction_profiles/bytedance/pico4_controller",
        "/interaction_profiles/ext/hand_interaction_ext",
        "/interaction_profiles/ext/eye_gaze_interaction",
    };

    public static void Run()
    {
        try
        {
            if (!ProjectSettings.GetSettingWithOverride("xr/openxr/enabled").AsBool())
                return;

            var path = ResourceUid.EnsurePath(ProjectSettings.GetSettingWithOverride("xr/openxr/default_action_map").AsString());
            var watch = Stopwatch.StartNew();
            var map = ResourceLoader.Load<OpenXRActionMap>(path);
            if (map == null)
            {
                LumoraLogger.Warn($"OpenXR action map: {path} did not load. No controller gets any bindings in VR.");
                return;
            }

            var present = new HashSet<string>();
            var empty = new List<string>();
            int profiles = map.GetInteractionProfileCount();
            for (int i = 0; i < profiles; i++)
            {
                var profile = map.GetInteractionProfile(i);
                if (profile == null)
                    continue;
                present.Add(profile.InteractionProfilePath);
                if (profile.GetBindingCount() == 0)
                    empty.Add(profile.InteractionProfilePath);
            }

            var missing = new List<string>();
            foreach (var expected in Expected)
            {
                if (!present.Contains(expected))
                    missing.Add(expected);
            }

            int actions = 0;
            for (int i = 0; i < map.GetActionSetCount(); i++)
                actions += map.GetActionSet(i)?.GetActionCount() ?? 0;

            if (missing.Count > 0)
                LumoraLogger.Warn($"OpenXR action map: {path} has {profiles} interaction profiles, missing {missing.Count} of the {Expected.Length} it should carry: "
                                  + string.Join(", ", missing) + ". Those controllers get no bindings in VR. Restore the file before the next build.");
            if (empty.Count > 0)
                LumoraLogger.Warn($"OpenXR action map: profiles with no bindings in {path}: " + string.Join(", ", empty) + ".");
            if (missing.Count == 0 && empty.Count == 0)
                LumoraLogger.Log($"OpenXR action map: {profiles} interaction profiles, {actions} actions ({watch.ElapsedMilliseconds} ms).");
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"OpenXR action map: check failed: {ex.Message}");
        }
    }
}
