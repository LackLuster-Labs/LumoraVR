// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core;

// The allowed range of one setting, declared ONCE.
//
// A setting's bounds used to be written in three places: the property setter clamped, the load clamped
// again, and the settings screen passed its own min and max to the slider. Three copies of one fact,
// and they had already drifted: the engine accepted a render scale up to 2.0 while the slider stopped
// at 1.5, so a value the renderer supports could not be reached from the UI at all. Mouse sensitivity
// and smoothing were narrowed the same way.
//
// One declaration, three readers. A range cannot disagree with itself. -xlinka
public readonly struct SettingRange
{
    public readonly float Min;
    public readonly float Max;

    // The UI increment. Zero means the setting is not presented as a slider.
    public readonly float Step;

    // Some settings are "off, or a value in a range": an FPS cap of 0 means uncapped, and 1 would be
    // absurd, so anything above off snaps up to a floor. Zero means the setting has no off state.
    public readonly float Floor;

    public SettingRange(float min, float max, float step = 0f)
    {
        Min = min;
        Max = max;
        Step = step;
        Floor = 0f;
    }

    public SettingRange(float min, float max, float step, float floor)
    {
        Min = min;
        Max = max;
        Step = step;
        Floor = floor;
    }

    public float Clamp(float value) => System.Math.Clamp(value, Min, Max);

    public int ClampInt(int value) => System.Math.Clamp(value, (int)Min, (int)Max);

    // Off, or inside [Floor, Max]. The three settings using this each wrote the rule by hand and each
    // drifted from its own slider: the engine took an FPS cap to 480 while the UI stopped at 240, and a
    // background cap to 240 while the UI stopped at 120. -xlinka
    public float ClampOptional(float value)
        => value <= Min ? Min : System.Math.Clamp(value, Floor, Max);

    public int ClampOptionalInt(int value)
        => value <= (int)Min ? (int)Min : System.Math.Clamp(value, (int)Floor, (int)Max);
}

// One value out of a fixed set, declared once the same way: the setter and the load clamp to it and the
// row offers exactly it, so a choice cannot offer what the engine refuses or refuse what it offers.
// Anything outside the set, an old save's or a newer build's, comes back as the default. -xlinka
public readonly struct SettingChoice<T> where T : struct, Enum
{
    private readonly T[] _options;
    public readonly T Default;

    public SettingChoice(T fallback, params T[] options)
    {
        Default = fallback;
        _options = options;
    }

    public IReadOnlyList<T> Options => _options;

    public int IndexOf(T value) => Array.IndexOf(_options, value);

    public T Clamp(T value) => IndexOf(value) >= 0 ? value : Default;

    public T At(int index) => index >= 0 && index < _options.Length ? _options[index] : Default;
}

// Every bounded engine setting. Named rather than keyed by string so a typo is a compile error instead
// of a silently unbounded slider. -xlinka
public static class SettingsCatalog
{
    public static readonly SettingRange MouseSensitivity = new(0.05f, 10f, 0.01f);
    public static readonly SettingRange MouseSmoothing = new(0f, 0.95f, 0.01f);
    public static readonly SettingRange StickLookSpeed = new(0.25f, 3f, 0.05f);
    public static readonly SettingRange LaserSmoothing = new(0f, 1f, 0.05f);
    public static readonly SettingRange HapticsStrength = new(0f, 1f, 0.05f);
    public static readonly SettingRange NoclipSpeed = new(1f, 30f, 0.5f);
    public static readonly SettingRange UserHeight = new(0.5f, 2.5f, 0.01f);
    // 0 until measured, then the headset's height over the floor while sitting. Measured, never a slider.
    public static readonly SettingRange SeatedHeadHeight = new(0f, 2.5f);
    // One shape for every volume: a normalized gain in 5% steps. Finer than that is a step nobody can
    // hear and a slider nobody can land on.
    public static readonly SettingRange MasterVolume = new(0f, 1f, 0.05f);
    public static readonly SettingRange SoundEffectVolume = new(0f, 1f, 0.05f);
    public static readonly SettingRange MultimediaVolume = new(0f, 1f, 0.05f);
    public static readonly SettingRange VoiceVolume = new(0f, 1f, 0.05f);
    public static readonly SettingRange UIVolume = new(0f, 1f, 0.05f);

    // What the world drops to while you are whispering, and what a whisperer's own ears drop the rest
    // of the room to. Same 5% shape as every other gain.
    public static readonly SettingRange WhisperAttenuationVolume = new(0f, 1f, 0.05f);

    // Bitrate an in-world audio stream opens at, in kbit/s. The floor is the lowest the codec will take
    // seriously and the ceiling is well past transparent for stereo, so this is a bandwidth lever rather
    // than a quality one.
    public static readonly SettingRange DefaultStreamBitrateKbps = new(2f, 500f, 1f);

    // Voice capture. The gate threshold is a peak amplitude, not a decibel figure, because that is
    // what the filter measures; the times are seconds.
    // The band a voice gate works in, which is a fifth of a second at the outside. These used to run
    // to half an amplitude and to two whole seconds, so the part of the slider that does anything was
    // its first tenth and every drag overshot it. -xlinka
    public static readonly SettingRange NoiseGateThreshold = new(0f, 0.2f, 0.002f);
    public static readonly SettingRange NoiseGateAttack = new(0f, 0.2f, 0.002f);
    public static readonly SettingRange NoiseGateHold = new(0f, 0.2f, 0.002f);
    public static readonly SettingRange NoiseGateRelease = new(0f, 0.2f, 0.002f);
    public static readonly SettingRange NormalizationThreshold = new(0.01f, 0.2f, 0.002f);
    // The leveller's target as voiced speech RMS in dBFS, and the microphone gain ahead of it in dB.
    // The gain spans the same -12..+24 the leveller itself does.
    public static readonly SettingRange VoiceTargetLevel = new(-30f, -12f, 0.5f);
    public static readonly SettingRange MicrophoneGain = new(-12f, 24f, 0.5f);
    public static readonly SettingRange RenderScale = new(0.5f, 2f, 0.05f);
    public static readonly SettingRange FieldOfView = new(10f, 120f, 1f);
    public static readonly SettingRange Upscaler = new(0f, 2f);
    public static readonly SettingRange LodBias = new(0.25f, 4f, 0.05f);
    public static readonly SettingRange ShadowDistanceScale = new(0.25f, 4f, 0.05f);
    // Metres past which other people's avatars stop casting shadows, and past which they stop drawing at all.
    // 0 is never; anything above it is at least the floor, so an avatar never vanishes at arm's length.
    public static readonly SettingRange AvatarShadowDistance = new(0f, 60f, 1f, 3f);
    public static readonly SettingRange AvatarDrawDistance = new(0f, 300f, 5f, 10f);
    // Metres past which other avatars are pictures, and how many of the nearest are never one; 0 is never and everyone.
    public static readonly SettingRange AvatarStandInDistance = new(0f, 150f, 5f, 10f);
    public static readonly SettingRange AvatarFullDetailCount = new(0f, 64f, 1f, 2f);
    // Point and spot lights casting at once, desktop and headset alike. Sixteen is already past the
    // renderer's own cap of 64 shadow passes a frame, which is about ten point lights.
    public static readonly SettingRange ShadowedLights = new(0f, 16f, 1f);
    // Updates a second for a moving thing's shadow; 0 is every frame, otherwise at least 5.
    public static readonly SettingRange MovingShadowRate = new(0f, 60f, 5f, 5f);
    public static readonly SettingRange Bloom = new(0f, 2f, 0.05f);
    // Off at 0. The renderer takes the strength on a wider scale; the applier maps it.
    public static readonly SettingRange AmbientOcclusion = new(0f, 1f, 0.05f);
    public static readonly SettingRange SnapTurnAngle = new(10f, 90f, 5f);
    public static readonly SettingRange PhotoTimerSeconds = new(2f, 30f, 1f);
    public static readonly SettingRange SmoothTurnSpeed = new(30f, 360f, 5f);
    public static readonly SettingRange ReticleSize = new(2f, 48f, 1f);
    // Off at 0, otherwise floored. Max is what the ENGINE accepts, which is what the slider must offer.
    public static readonly SettingRange MaxFps = new(0f, 480f, 10f, 30f);
    public static readonly SettingRange BackgroundFps = new(0f, 240f, 10f, 5f);
    public static readonly SettingRange MeshLodThreshold = new(0f, 8f, 0.5f, 0.5f);

    public static readonly SettingRange ReticleThickness = new(1f, 8f, 0.5f);

    // Seconds the pointer rests on something before its tooltip shows.
    public static readonly SettingRange TooltipDelay = new(0.2f, 2f, 0.1f);

    // The tallest format the video service resolver is allowed to ask for. A 4k link decodes at four
    // times the cost of 1080p and a wall panel is not four times bigger, so this is a real lever rather
    // than a quality preference.
    public static readonly SettingRange VideoMaxHeight = new(360f, 2160f, 120f);

    // Gigabytes of disk for downloaded and received assets. The floor still holds a busy world's worth; the
    // ceiling is far past anything a session pulls down.
    public static readonly SettingRange AssetCacheGigabytes = new(1f, 200f, 1f);

    // Off (any free port), or a first port with room above it for the worlds hosted after it.
    public static readonly SettingRange HostingPort = new(0f, 65504f, 0f, 1024f);

    // How bright the desktop page draws the screen. Floored so the picture can never be dimmed away.
    public static readonly SettingRange DesktopBrightness = new(0.2f, 1f, 0.05f);

    // Who can join a world you host before you change it: the levels the Session screen offers an ordinary
    // world, Private unless picked otherwise.
    public static readonly SettingChoice<World.WorldAccessLevel> DefaultJoinLevel = new(World.WorldAccessLevel.Private,
        World.WorldAccessLevel.Private, World.WorldAccessLevel.LAN, World.WorldAccessLevel.Contacts,
        World.WorldAccessLevel.ContactsPlus, World.WorldAccessLevel.Anyone);

    // The same for a world hosted for a group, kept by the group rather than by you: members only, or anyone.
    public static readonly SettingChoice<World.WorldAccessLevel> GroupJoinLevel = new(World.WorldAccessLevel.GroupMembers,
        World.WorldAccessLevel.GroupMembers, World.WorldAccessLevel.GroupPublic);
}
