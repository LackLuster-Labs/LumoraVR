// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Input;

/// <summary>
/// Coarse classification of the active VR platform. Used by rendering and
/// gameplay code to pick sensible defaults (mobile shader paths, button
/// prompts, etc.) without having to string-match runtime names everywhere.
/// </summary>
public enum VRPlatform
{
    None,
    DesktopOpenXR,
    MetaQuestStandalone,
    PicoStandalone,
    ViveFocusStandalone,
}

/// <summary>
/// Interface for VR input drivers.
/// </summary>
public interface IVRDriver
{
    /// <summary>
    /// Check if VR is available and active.
    /// </summary>
    bool IsVRActive { get; }

    /// <summary>
    /// Update VR devices with latest tracking data.
    /// </summary>
    void UpdateVRDevices(VRController leftController, VRController rightController, HeadDevice headDevice);

    /// <summary>
    /// XRInterface name (e.g. "OpenXR"). Stable identifier for the
    /// underlying tracking backend.
    /// </summary>
    string VRSystemName { get; }

    /// <summary>
    /// Coarse platform bucket - drives mobile/desktop branching.
    /// Returns <see cref="VRPlatform.None"/> when VR is not active.
    /// </summary>
    VRPlatform Platform { get; }

    /// <summary>
    /// Human-readable OpenXR runtime name (e.g. "SteamVR/OpenXR",
    /// "Oculus", "Pico OpenXR Runtime"). Empty when unavailable.
    /// </summary>
    string RuntimeName { get; }

    /// <summary>
    /// Active OpenXR interaction profile path for the left hand
    /// (e.g. "/interaction_profiles/oculus/touch_controller").
    /// Empty when no profile is bound or the controller is idle.
    /// </summary>
    string LeftInteractionProfile { get; }

    /// <summary>
    /// Active OpenXR interaction profile path for the right hand.
    /// </summary>
    string RightInteractionProfile { get; }
}
