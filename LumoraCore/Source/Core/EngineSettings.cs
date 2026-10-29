// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Media;

namespace Lumora.Core;

// Engine-owned user settings. The settings UI writes these; subsystems read
// them directly (mouse look) or the platform layer subscribes to Changed and
// applies what only it can (vsync, window mode, render scale, audio bus).
// Persisted as JSON in the user's application data folder.
public static class EngineSettings
{
    public static event Action? Changed;

    private static bool _dirty;
    private static bool _loaded;

    // INPUT

    private static float _mouseSensitivity = 1f;
    public static float MouseSensitivity
    {
        get => _mouseSensitivity;
        set => SetValue(ref _mouseSensitivity, SettingsCatalog.MouseSensitivity.Clamp(value));
    }

    private static float _mouseSmoothing;
    public static float MouseSmoothing
    {
        get => _mouseSmoothing;
        set => SetValue(ref _mouseSmoothing, SettingsCatalog.MouseSmoothing.Clamp(value));
    }

    // How fast a pad's look stick turns the view on a screen, as a multiple of the stock pace (StickLook).
    private static float _stickLookSpeed = 1f;
    public static float StickLookSpeed
    {
        get => _stickLookSpeed;
        set => SetValue(ref _stickLookSpeed, SettingsCatalog.StickLookSpeed.Clamp(value));
    }

    // Stick up looks down, the flight-stick way some people learned on.
    private static bool _invertStickLook;
    public static bool InvertStickLook
    {
        get => _invertStickLook;
        set => SetValue(ref _invertStickLook, value);
    }

    // How much a VR laser's aim is steadied, 0 for the raw controller ray. The laser reads it as it casts.
    private static float _laserSmoothing = 0.5f;
    public static float LaserSmoothing
    {
        get => _laserSmoothing;
        set => SetValue(ref _laserSmoothing, SettingsCatalog.LaserSmoothing.Clamp(value));
    }

    // Scales every controller vibration, 0 for none at all.
    private static float _hapticsStrength = 1f;
    public static float HapticsStrength
    {
        get => _hapticsStrength;
        set => SetValue(ref _hapticsStrength, SettingsCatalog.HapticsStrength.Clamp(value));
    }

    // Noclip flight speed in m/s. Only affects the noclip locomotion module.
    private static float _noclipSpeed = 6f;
    public static float NoclipSpeed
    {
        get => _noclipSpeed;
        set => SetValue(ref _noclipSpeed, SettingsCatalog.NoclipSpeed.Clamp(value));
    }

    // Preferred locomotion module by DisplayName ("Walk", "Blink", "Noclip", ...), set from the
    // Settings screen's locomotion picker. Empty means no preference - spawn uses the ordinary first-usable
    // pick. Applied at spawn/world join; a name that no longer matches a usable module (renamed, or denied
    // by the current world's permission gate) just falls through to that default instead of failing.
    private static string _preferredLocomotion = string.Empty;
    public static string PreferredLocomotion
    {
        get => _preferredLocomotion;
        set => SetValue(ref _preferredLocomotion, value ?? string.Empty);
    }

    // PHOTOS

    // The sizes a photo is taken at, and the one picked.
    public static readonly (int Width, int Height)[] PhotoSizes = { (1920, 1080), (2560, 1440), (3840, 2160) };
    private static int _photoSize;
    public static int PhotoSize
    {
        get => _photoSize;
        set => SetValue(ref _photoSize, System.Math.Clamp(value, 0, PhotoSizes.Length - 1));
    }

    private static Assets.ImageEncoding _photoEncoding = Assets.ImageEncoding.Webp;
    public static Assets.ImageEncoding PhotoEncoding
    {
        get => _photoEncoding;
        set => SetEnum(ref _photoEncoding, value);
    }

    // Seconds a timed photo counts down.
    private static float _photoTimerSeconds = 5f;
    public static float PhotoTimerSeconds
    {
        get => _photoTimerSeconds;
        set => SetValue(ref _photoTimerSeconds, SettingsCatalog.PhotoTimerSeconds.Clamp(value));
    }

    // An L made with the left hand opens a viewfinder.
    private static bool _handFrame = true;
    public static bool HandFrame
    {
        get => _handFrame;
        set => SetValue(ref _handFrame, value);
    }

    // Every photo also goes into the inventory's Photos folder.
    private static bool _photosToInventory = true;
    public static bool PhotosToInventory
    {
        get => _photosToInventory;
        set => SetValue(ref _photosToInventory, value);
    }

    // A mark over this user's name while the streaming program on this machine records or streams. Off,
    // nothing about it goes on their user record.
    private static bool _showCapture = true;
    public static bool ShowCapture
    {
        get => _showCapture;
        set => SetValue(ref _showCapture, value);
    }

    // The hand this user leads with, Left or Right. Everyone sees it on their user record; menus and scripts
    // ask it which hand is which.
    private static Input.Chirality _primaryHand = Input.Chirality.Right;
    public static Input.Chirality PrimaryHand
    {
        get => _primaryHand;
        set => SetEnum(ref _primaryHand, value == Input.Chirality.Left ? Input.Chirality.Left : Input.Chirality.Right);
    }

    // AVATAR

    // Calibrated standing/eye height in metres. Drives the avatar auto-rescale (AvatarIK reads
    // InputInterface.UserHeight, which is kept in sync with this). Default 1.75 m. -xlinka
    private static float _userHeight = 1.75f;
    public static float UserHeight
    {
        get => _userHeight;
        set => SetValue(ref _userHeight, SettingsCatalog.UserHeight.Clamp(value));
    }

    // Seated mode: someone who plays sitting down is lifted so their avatar stands at the eye height their
    // UserHeight gives. SeatedHeadHeight is the headset's height over the play-space floor when it was turned
    // on, in metres, 0 until measured. Both stay with this machine: the chair and the floor calibration are
    // this rig's, not the account's. Input.SeatedMode turns the two into the lift.
    private static bool _seatedMode;
    public static bool SeatedMode
    {
        get => _seatedMode;
        set => SetValue(ref _seatedMode, value);
    }

    private static float _seatedHeadHeight;
    public static float SeatedHeadHeight
    {
        get => _seatedHeadHeight;
        set => SetValue(ref _seatedHeadHeight, SettingsCatalog.SeatedHeadHeight.Clamp(value));
    }

    // While somebody's avatar is still arriving on this machine, a glowing stand-in with a progress readout
    // takes its place and the half-built avatar stays out of sight. Off shows the avatar as it builds.
    private static bool _avatarArrival = true;
    public static bool AvatarArrival
    {
        get => _avatarArrival;
        set => SetValue(ref _avatarArrival, value);
    }

    // CHAT

    // A bubble beside other people's heads while they type in the world's chat, then with what they sent.
    private static bool _showChatBubbles = true;
    public static bool ShowChatBubbles
    {
        get => _showChatBubbles;
        set => SetValue(ref _showChatBubbles, value);
    }

    // On a screen, the world's last few chat lines in the bottom left corner for a while after each new one.
    // Off, the box only shows while you are typing into it.
    private static bool _showDesktopChat = true;
    public static bool ShowDesktopChat
    {
        get => _showDesktopChat;
        set => SetValue(ref _showDesktopChat, value);
    }

    // AUDIO

    // The master gain sits on the mixer's device output; the four below are group gains an AudioOutput
    // folds into its own volume by what the sound is FOR, so turning music down does not take the voice
    // of the person talking to you with it. -xlinka
    private static float _masterVolume = 1f;
    public static float MasterVolume
    {
        get => _masterVolume;
        set => SetValue(ref _masterVolume, SettingsCatalog.MasterVolume.Clamp(value));
    }

    private static float _soundEffectVolume = 1f;
    public static float SoundEffectVolume
    {
        get => _soundEffectVolume;
        set => SetValue(ref _soundEffectVolume, SettingsCatalog.SoundEffectVolume.Clamp(value));
    }

    private static float _multimediaVolume = 1f;
    public static float MultimediaVolume
    {
        get => _multimediaVolume;
        set => SetValue(ref _multimediaVolume, SettingsCatalog.MultimediaVolume.Clamp(value));
    }

    private static float _voiceVolume = 1f;
    public static float VoiceVolume
    {
        get => _voiceVolume;
        set => SetValue(ref _voiceVolume, SettingsCatalog.VoiceVolume.Clamp(value));
    }

    private static float _uiVolume = 1f;
    public static float UIVolume
    {
        get => _uiVolume;
        set => SetValue(ref _uiVolume, SettingsCatalog.UIVolume.Clamp(value));
    }

    // Whispering is quiet in both directions: a whisperer far enough away is attenuated by this for
    // everyone else, and the whisperer's own world is turned down by it so the person they are leaning
    // in to is the thing they can hear.
    private static float _whisperAttenuationVolume = 0.5f;
    public static float WhisperAttenuationVolume
    {
        get => _whisperAttenuationVolume;
        set => SetValue(ref _whisperAttenuationVolume, SettingsCatalog.WhisperAttenuationVolume.Clamp(value));
    }

    // DEVICES
    //
    // Empty is "use whatever the system calls default", and that is a different setting from naming the
    // device that is default today: the system choice follows the machine when a headset is plugged in
    // or the desk speakers are switched off, a named one stays put and goes quiet when it is unplugged.
    // A name the machine no longer has falls back to the system default at apply time rather than
    // leaving the engine pointed at nothing. -xlinka
    private static string _audioInputDevice = string.Empty;
    public static string AudioInputDevice
    {
        get => _audioInputDevice;
        set => SetValue(ref _audioInputDevice, value ?? string.Empty);
    }

    private static string _audioOutputDevice = string.Empty;
    public static string AudioOutputDevice
    {
        get => _audioOutputDevice;
        set => SetValue(ref _audioOutputDevice, value ?? string.Empty);
    }

    // Opening bitrate for an in-world audio stream, kbit/s. Voice has its own, much lower, figure: this
    // is for streaming a device into the room, which is usually music and usually stereo.
    private static int _defaultStreamBitrateKbps = 128;
    public static int DefaultStreamBitrateKbps
    {
        get => _defaultStreamBitrateKbps;
        set => SetValue(ref _defaultStreamBitrateKbps, SettingsCatalog.DefaultStreamBitrateKbps.ClampInt(value));
    }

    // The device the last stream went out on, by id, so the picker opens on it next time. Empty leaves
    // the picker on its first entry, which is system audio wherever the machine has it.
    private static string _streamDevice = string.Empty;
    public static string StreamDevice
    {
        get => _streamDevice;
        set => SetValue(ref _streamDevice, value ?? string.Empty);
    }

    // Where a stream's sound goes: an output device by id, usually a virtual cable a streaming program
    // listens to. Empty sends it nowhere.
    private static string _streamOutputDevice = string.Empty;
    public static string StreamOutputDevice
    {
        get => _streamOutputDevice;
        set => SetValue(ref _streamOutputDevice, value ?? string.Empty);
    }

    // DESKTOP
    //
    // The desktop page: the monitor it shows (DesktopDisplay.Key, empty for the main one), whether it
    // follows the mouse onto another monitor, and how bright the picture is drawn.
    private static string _desktopMonitor = string.Empty;
    public static string DesktopMonitor
    {
        get => _desktopMonitor;
        set => SetValue(ref _desktopMonitor, value ?? string.Empty);
    }

    private static bool _desktopFollowCursor;
    public static bool DesktopFollowCursor
    {
        get => _desktopFollowCursor;
        set => SetValue(ref _desktopFollowCursor, value);
    }

    private static float _desktopBrightness = 1f;
    public static float DesktopBrightness
    {
        get => _desktopBrightness;
        set => SetValue(ref _desktopBrightness, SettingsCatalog.DesktopBrightness.Clamp(value));
    }

    // What the desktop portal hands back when screen sharing is allowed, given back next time instead of
    // asking the user again. Only a Wayland desktop has one.
    //
    // It is single use: every share spends the token it was given and hands back the next. So it goes to
    // disk the moment it arrives, from whatever thread brings it, and it is not a change: nothing applies
    // it and nobody is asked to save it. A token that waited for "Exit and Save" would be a spent one on
    // every other way out, and the user would be asked again each launch.
    private static string _desktopPortalToken = string.Empty;
    public static string DesktopPortalToken
    {
        get => System.Threading.Volatile.Read(ref _desktopPortalToken);
        set
        {
            value ??= string.Empty;
            System.Threading.Volatile.Write(ref _desktopPortalToken, value);
            try
            {
                Settings.WriteValue(KeyDesktopPortalToken, value);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"EngineSettings: failed to keep the screen-share token: {ex.Message}");
            }
        }
    }

    // VOICE
    //
    // Local only: none of this replicates, because it describes this machine's microphone and nobody
    // else's ears. The host's own silencing lives on User.IsSilenced and is a different thing entirely.
    //
    // There is deliberately no noise suppression setting. Suppression means a denoiser, we have no
    // managed one, and a checkbox that turns nothing on is worse than no checkbox. The gate below is
    // what we actually have: it cuts the room between words, it does not clean up what is said over it.
    // -xlinka
    private static bool _microphoneMuted;
    public static bool MicrophoneMuted
    {
        get => _microphoneMuted;
        set => SetValue(ref _microphoneMuted, value);
    }

    private static bool _voiceNormalization = true;
    public static bool VoiceNormalization
    {
        get => _voiceNormalization;
        set => SetValue(ref _voiceNormalization, value);
    }

    // Peak amplitude a captured frame has to reach before the gate counts it as speech.
    // Spectral suppression ahead of the gate. On by default: a gate alone passes the whole room the
    // moment it opens, and "I can hear your fan" is the most common thing anyone says about anyone
    // else's microphone.
    private static bool _noiseSuppression = true;
    public static bool NoiseSuppression
    {
        get => _noiseSuppression;
        set => SetValue(ref _noiseSuppression, value);
    }

    // THE GATE, RETUNED TO THE BAND IT ACTUALLY WORKS IN.
    //
    // These four shipped on ranges five to ten times wider than anything usable - hold and release
    // ran to two seconds where a gate lives under a fifth of one - so the whole working range was
    // squeezed into the first tenth of the slider and a normal drag threw it straight past. The
    // ranges top out at 0.2, the band these controls are designed around.
    //
    // The release default came down with them: it was 0.2 s, which is a fifth of a second of open
    // microphone after every phrase. That is exactly the window the room noise rides out on. -xlinka
    private static float _noiseGateThreshold = 0.015f;
    public static float NoiseGateThreshold
    {
        get => _noiseGateThreshold;
        set => SetValue(ref _noiseGateThreshold, SettingsCatalog.NoiseGateThreshold.Clamp(value));
    }

    // Seconds the gate takes to open. Long enough and the first consonant of every word is gone.
    private static float _noiseGateAttack = 0.02f;
    public static float NoiseGateAttack
    {
        get => _noiseGateAttack;
        set => SetValue(ref _noiseGateAttack, SettingsCatalog.NoiseGateAttack.Clamp(value));
    }

    // Seconds the gate stays open after the last loud frame, so a pause between words is not a hole.
    private static float _noiseGateHold = 0.2f;
    public static float NoiseGateHold
    {
        get => _noiseGateHold;
        set => SetValue(ref _noiseGateHold, SettingsCatalog.NoiseGateHold.Clamp(value));
    }

    private static float _noiseGateRelease = 0.02f;
    public static float NoiseGateRelease
    {
        get => _noiseGateRelease;
        set => SetValue(ref _noiseGateRelease, SettingsCatalog.NoiseGateRelease.Clamp(value));
    }

    // A peak target nothing reads: the leveller steers by VoiceTargetLevel. Kept so the saved key stays.
    private static float _normalizationThreshold = 0.05f;
    public static float NormalizationThreshold
    {
        get => _normalizationThreshold;
        set => SetValue(ref _normalizationThreshold, SettingsCatalog.NormalizationThreshold.Clamp(value));
    }

    // Voiced speech level the leveller steers a voice toward, RMS in dBFS. A new key rather than a new
    // default on the old one: most installs have the old peak target written down already, so a changed
    // default there would have reached nobody. -xlinka
    private static float _voiceTargetLevel = -18.5f;
    public static float VoiceTargetLevel
    {
        get => _voiceTargetLevel;
        set => SetValue(ref _voiceTargetLevel, SettingsCatalog.VoiceTargetLevel.Clamp(value));
    }

    // Fixed gain on the microphone ahead of the gate and the leveller, dB. For a capsule quieter than the
    // leveller's own range, or for anyone running with normalizing off.
    private static float _microphoneGain;
    public static float MicrophoneGain
    {
        get => _microphoneGain;
        set => SetValue(ref _microphoneGain, SettingsCatalog.MicrophoneGain.Clamp(value));
    }

    // The gain the leveller settled at on each input device, dB, so a microphone heard before starts at
    // its own level instead of climbing from unity through the first words of every session. Machine
    // local, per device id, and written when the device closes rather than on Commit: it is state the
    // chain arrived at, not a choice anybody saves. NaN for a device never heard. -xlinka
    public static float MicrophoneGainSeed(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return float.NaN;
        try
        {
            return Settings.ReadValue(KeyMicrophoneGainSeedPrefix + deviceId, float.NaN);
        }
        catch
        {
            return float.NaN;
        }
    }

    public static void SetMicrophoneGainSeed(string deviceId, float gainDb)
    {
        if (string.IsNullOrEmpty(deviceId) || !float.IsFinite(gainDb))
            return;
        gainDb = MathF.Round(gainDb, 1);
        // Half a decibel either way is the same microphone; not worth rewriting the store for.
        if (MathF.Abs(MicrophoneGainSeed(deviceId) - gainDb) < 0.5f)
            return;
        try
        {
            Settings.WriteValue(KeyMicrophoneGainSeedPrefix + deviceId, gainDb);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"EngineSettings: failed to keep the microphone gain: {ex.Message}");
        }
    }

    // MEDIA PLAYBACK

    // Which browser's cookie jar the video service resolver may read, for age-gated or member-only
    // links. None is the default and nothing is read at all until somebody picks one: handing a
    // resolver your logged-in session is a decision, not a convenience.
    private static CookiesBrowser _videoCookiesBrowser = CookiesBrowser.None;
    public static CookiesBrowser VideoCookiesBrowser
    {
        get => _videoCookiesBrowser;
        set => SetEnum(ref _videoCookiesBrowser, value);
    }

    // The default cap a video provider asks the resolver for when it has no cap of its own. Decode cost
    // scales with pixels, and a panel on a wall does not gain anything from 4k.
    private static int _videoMaxHeight = 1080;
    public static int VideoMaxHeight
    {
        get => _videoMaxHeight;
        set => SetValue(ref _videoMaxHeight, SettingsCatalog.VideoMaxHeight.ClampInt(value));
    }

    // STORAGE

    // Gigabytes of disk for assets this machine holds only because they came from elsewhere: downloads, and
    // what peers or the cloud sent. Past it the file wanted longest ago is let go; it can be had again. Zero
    // until someone sets it, which reads as the platform's default.
    private static int _assetCacheGigabytes;
    public static int AssetCacheGigabytes
    {
        get => _assetCacheGigabytes > 0 ? _assetCacheGigabytes : DefaultAssetCacheGigabytes;
        set => SetValue(ref _assetCacheGigabytes, SettingsCatalog.AssetCacheGigabytes.ClampInt(value));
    }

    // A standalone headset or phone shares a small disk with everything else on it.
    public static int DefaultAssetCacheGigabytes => Engine.UseConstrainedResources ? 4 : 10;

    // NETWORK
    //
    // All of it stays with this machine: which node is near, which ports are open and whether Steam runs
    // here are facts about the network the computer sits on, not about who signed in. The transport reads
    // its share through NetworkOptions, kept in step by the setters and by Load. -xlinka

    // A NAT node's id from the official node list; empty takes the nearest by round trip.
    private static string _natNode = string.Empty;
    public static string NatNode
    {
        get => _natNode;
        set
        {
            SetValue(ref _natNode, value?.Trim() ?? string.Empty);
            Lumora.Nexus.Transport.NetworkOptions.PreferredNode = _natNode;
        }
    }

    // Joining through a node: punch a direct path first, and relay through the node when that fails.
    private static bool _natPunch = true;
    public static bool NatPunch
    {
        get => _natPunch;
        set
        {
            SetValue(ref _natPunch, value);
            Lumora.Nexus.Transport.NetworkOptions.AllowPunch = value;
        }
    }

    private static bool _natRelay = true;
    public static bool NatRelay
    {
        get => _natRelay;
        set
        {
            SetValue(ref _natRelay, value);
            Lumora.Nexus.Transport.NetworkOptions.AllowRelay = value;
        }
    }

    // TCP once no UDP way in has worked, straight to the host or through its node's gate.
    private static bool _tcpFallback = true;
    public static bool TcpFallback
    {
        get => _tcpFallback;
        set
        {
            SetValue(ref _tcpFallback, value);
            Lumora.Nexus.Transport.NetworkOptions.AllowTcp = value;
        }
    }

    // QUIC to a host's address before LNL to it, where this machine has QUIC.
    private static bool _preferQuic;
    public static bool PreferQuic
    {
        get => _preferQuic;
        set
        {
            SetValue(ref _preferQuic, value);
            Lumora.Nexus.Transport.NetworkOptions.PreferQuic = value;
        }
    }

    // Where a Steam address goes among a session's addresses when joining.
    public enum SteamJoin
    {
        Fallback,
        Prefer,
        Off,
    }

    private static SteamJoin _steamJoin = SteamJoin.Fallback;
    public static SteamJoin SteamConnections
    {
        get => _steamJoin;
        set => SetEnum(ref _steamJoin, value);
    }

    // IPv6 addresses listed for hosted worlds, dialled when joining, and used to reach a node over IPv6.
    private static bool _ipv6 = true;
    public static bool Ipv6
    {
        get => _ipv6;
        set
        {
            SetValue(ref _ipv6, value);
            Lumora.Nexus.Transport.NetworkOptions.AllowIpv6 = value;
        }
    }

    // A listed world's hosting port forwarded on the router (PCP, NAT-PMP or UPnP) for as long as it is hosted.
    private static bool _portForwarding = true;
    public static bool PortForwarding
    {
        get => _portForwarding;
        set => SetValue(ref _portForwarding, value);
    }

    // Sessions announced to, and heard from, this local network.
    private static bool _lanDiscovery = true;
    public static bool LanDiscovery
    {
        get => _lanDiscovery;
        set => SetValue(ref _lanDiscovery, value);
    }

    // The UDP port hosted worlds listen on, the next free one up for each further world; 0 takes any free port.
    // Read straight from the store before Load, because the home is hosted before the settings are read.
    private static int _hostingPort;
    public static int HostingPort
    {
        get => _loaded ? _hostingPort : SettingsCatalog.HostingPort.ClampOptionalInt(Settings.ReadValue(KeyHostingPort, 0));
        set => SetValue(ref _hostingPort, SettingsCatalog.HostingPort.ClampOptionalInt(value));
    }

    // Who can join a world you host, as the create forms start out and as a saved world opens. The home stays
    // private whatever this says.
    private static World.WorldAccessLevel _defaultJoinLevel = SettingsCatalog.DefaultJoinLevel.Default;
    public static World.WorldAccessLevel DefaultJoinLevel
    {
        get => _defaultJoinLevel;
        set => SetEnum(ref _defaultJoinLevel, SettingsCatalog.DefaultJoinLevel.Clamp(value));
    }

    private static void PushNetworkOptions()
    {
        Lumora.Nexus.Transport.NetworkOptions.PreferredNode = _natNode;
        Lumora.Nexus.Transport.NetworkOptions.AllowPunch = _natPunch;
        Lumora.Nexus.Transport.NetworkOptions.AllowRelay = _natRelay;
        Lumora.Nexus.Transport.NetworkOptions.AllowIpv6 = _ipv6;
        Lumora.Nexus.Transport.NetworkOptions.AllowTcp = _tcpFallback;
        Lumora.Nexus.Transport.NetworkOptions.PreferQuic = _preferQuic;
    }

    // VIDEO

    private static bool _vsync = true;
    public static bool VSync
    {
        get => _vsync;
        set => SetValue(ref _vsync, value);
    }

    private static int _maxFps;
    public static int MaxFps
    {
        get => _maxFps;
        set => SetValue(ref _maxFps, SettingsCatalog.MaxFps.ClampOptionalInt(value));
    }

    // Frame cap applied while the window is unfocused or minimized; 0 = no background throttle. Caps a
    // loop that would otherwise free-run when the compositor stops blocking the swap. Ignored in VR
    // (the headset compositor owns frame timing). Never raises the rate above MaxFps.
    private static int _backgroundFps = 30;
    public static int BackgroundFps
    {
        get => _backgroundFps;
        set => SetValue(ref _backgroundFps, SettingsCatalog.BackgroundFps.ClampOptionalInt(value));
    }

    private static bool _fullscreen;
    public static bool Fullscreen
    {
        get => _fullscreen;
        set => SetValue(ref _fullscreen, value);
    }

    // The screen camera's vertical field of view in degrees. 90 was hard-coded before there was a setting,
    // about 120 across a 16:9 window, which shrank everything in front of the user: the radial menu
    // read as far away. -xlinka
    private static float _fieldOfView = 75f;
    public static float FieldOfView
    {
        get => _fieldOfView;
        set => SetValue(ref _fieldOfView, SettingsCatalog.FieldOfView.Clamp(value));
    }

    private static float _renderScale = 1f;
    public static float RenderScale
    {
        get => _renderScale;
        set => SetValue(ref _renderScale, SettingsCatalog.RenderScale.Clamp(value));
    }

    // RenderScale becomes a CEILING when this is on: the platform layer lowers the 3D render scale
    // when frames run long and walks it back up when they do not. A 4k window is four times the
    // pixels of 1080p and nothing was adapting, which is the whole "30fps in fullscreen" report.
    // Off unless chosen: every step down smears world-space text and hairlines into mush, and panels
    // are most of what you look at. -xlinka
    private static bool _dynamicResolution = false;
    public static bool DynamicResolution
    {
        get => _dynamicResolution;
        set => SetValue(ref _dynamicResolution, value);
    }

    // 0 bilinear, 1 FSR 1, 2 FSR 2. DESKTOP ONLY and deliberately so: both are Forward+ features, our
    // Android/standalone build runs the mobile renderer where they do not exist, and FSR2 in particular
    // is a known open Godot bug in VR (godotengine/godot#89167 - it simply does not improve the image
    // through OpenXR). The XR path keeps bilinear; offering a headset toggle that changes nothing would
    // be a placebo. Only bites below 1.0 scale anyway, which is what dynamic resolution produces. -xlinka
    private static int _upscaler;
    public static int Upscaler
    {
        get => _upscaler;
        set => SetValue(ref _upscaler, SettingsCatalog.Upscaler.ClampInt(value));
    }

    public static string DescribeUpscaler(int value) => value switch
    {
        1 => "FSR 1",
        2 => "FSR 2",
        _ => "Bilinear"
    };

    // Vertical resolution to run at, 0 meaning the display's own. Anything at or above the display is
    // treated as native. What it does depends on the window mode, which is what players expect from
    // this control: WINDOWED resizes the window to that size, FULLSCREEN keeps the window native (a
    // Godot fullscreen window is borderless at desktop resolution) and renders the 3D buffer at that
    // height instead, so the interface stays sharp while the expensive part gets cheaper. -xlinka
    public static readonly int[] ResolutionHeightOptions = { 0, 2160, 1440, 1080, 900, 720 };

    private static int _resolutionHeight;
    public static int ResolutionHeight
    {
        get => _resolutionHeight;
        set => SetValue(ref _resolutionHeight, value < 0 ? 0 : value);
    }

    public static string DescribeResolutionHeight(int value) => value <= 0 ? "Native" : $"{value}p";

    public static readonly int[] TextureSizeOptions = { 0, 2048, 1024, 512, 256 };

    // Longest edge, in pixels, that a URL-loaded texture is allowed to reach; 0 loads it at source
    // resolution. Providers fold this into their variant descriptor, so changing it swaps every
    // texture over to a different variant on the next change pass - no world reload, and textures
    // already resident at the new cap are simply shared rather than reloaded.
    //
    // Snapped to a generated bucket rather than free-form: a cap nobody generated a blob for would
    // silently fall back to the source and quietly do nothing, which is the worst kind of setting.
    private static int _maxTextureSize;
    public static int MaxTextureSize
    {
        get => _maxTextureSize;
        set => SetValue(ref _maxTextureSize, SnapTextureSize(value));
    }

    // Original on a desktop. 2048 on a standalone headset or phone: one memory holds pictures and
    // everything else, and 2048 keeps a big world's textures well inside a Quest 2's share of its 6 GB
    // once they are block compressed.
    public static int DefaultMaxTextureSize => Engine.UseConstrainedResources || SteamDeckPlatform.IsSteamDeck ? 2048 : 0;

    public static int SnapTextureSize(int value)
    {
        if (value <= 0)
            return 0;
        int best = 0;
        foreach (int option in TextureSizeOptions)
        {
            if (option > 0 && option <= value && option > best)
                best = option;
        }
        // Below the smallest bucket, clamp up to it rather than silently meaning "no cap".
        return best == 0 ? 256 : best;
    }

    public static string DescribeTextureSize(int value) => value <= 0 ? "Original" : $"{value} px";

    // Whether reflection probes render at all. Off leaves every probe in the world alone as data and
    // simply stops the renderer node existing, so glossy surfaces fall back to the sky. This is a real
    // frame-time lever: a probe in Always mode re-renders the scene from six directions.
    private static bool _reflectionsEnabled = true;
    public static bool ReflectionsEnabled
    {
        get => _reflectionsEnabled;
        set => SetValue(ref _reflectionsEnabled, value);
    }

    // There is deliberately no reflection-resolution setting. A probe's face size comes from the
    // renderer's reflection atlas, which is sized once when the renderer starts and has no runtime
    // setter, so a slider for it would move a number that changes nothing until the next launch.
    // On/off is the lever that genuinely exists, and it is a big one. -xlinka

    // Multiplier on every LOD switching distance. Above 1 keeps detailed levels alive further out
    // (prettier, heavier); below 1 drops to cheaper levels sooner. Applied by the LOD hooks when the
    // setting changes, not per frame.
    private static float _lodBias = 1f;
    public static float LodBias
    {
        get => _lodBias;
        set => SetValue(ref _lodBias, SettingsCatalog.LodBias.Clamp(value));
    }

    // Screen-space error, in pixels, the renderer accepts before a mesh drops to a cheaper level of
    // itself. Imported meshes carry a continuous chain of index-reduced levels, so this is trading
    // triangles against a difference measured in fractions of a pixel: nothing vanishes, and the
    // coarsest level holds however far away it gets. 0 pins every mesh to full detail. Applied to the
    // live viewports on change; new viewports inherit the project default. -xlinka
    private static float _meshLodThreshold = 2f;
    public static float MeshLodThreshold
    {
        get => _meshLodThreshold;
        set => SetValue(ref _meshLodThreshold, SettingsCatalog.MeshLodThreshold.ClampOptional(value));
    }

    public static string DescribeMeshLodThreshold(float value) =>
        value <= 0f ? "Full" : $"{value:0.#} px";

    // There is deliberately no network tick rate here. The sync rate is a property of the SESSION, not
    // of the machine looking at it: a guest turning it down does not make the host send less, it just
    // desyncs that guest. It lives on WorldSettings, host-only, and the Session screen edits it. -xlinka

    // COMFORT

    public enum TurnStyle
    {
        Snap,
        Smooth,
    }

    // How the stick turns you. Read live by TurnSubmodule, which every smooth locomotion module owns.
    private static TurnStyle _turnMode = TurnStyle.Snap;
    public static TurnStyle TurnMode
    {
        get => _turnMode;
        set => SetEnum(ref _turnMode, value);
    }

    // Degrees per snap flick. Small angles are smoother but cost more flicks per turn; large ones are
    // the comfort option.
    private static float _snapTurnAngle = 45f;
    public static float SnapTurnAngle
    {
        get => _snapTurnAngle;
        set => SetValue(ref _snapTurnAngle, SettingsCatalog.SnapTurnAngle.Clamp(value));
    }

    // Degrees per second while the stick is held, in Smooth mode.
    private static float _smoothTurnSpeed = 90f;
    public static float SmoothTurnSpeed
    {
        get => _smoothTurnSpeed;
        set => SetValue(ref _smoothTurnSpeed, SettingsCatalog.SmoothTurnSpeed.Clamp(value));
    }

    // Where smooth movement in VR takes forward from. Read live by the locomotion modules; a desktop
    // always walks where the view faces.
    private static MovementDirection _movementDirection = MovementDirection.Head;
    public static MovementDirection MovementDirection
    {
        get => _movementDirection;
        set => SetEnum(ref _movementDirection, value);
    }

    // INTERFACE

    public enum GizmoControlStyle
    {
        ContextMenu,
        Cubes,
        AllGizmo,
    }

    private static GizmoControlStyle _gizmoControls = GizmoControlStyle.ContextMenu;
    public static GizmoControlStyle GizmoControls
    {
        get => _gizmoControls;
        set => SetEnum(ref _gizmoControls, Enum.IsDefined(value) ? value : GizmoControlStyle.ContextMenu);
    }

    public enum ReticleShape
    {
        Ring,
        Dot,
        Crosshair,
        Off,
    }

    // Shared desktop reticle settings: the dash reads them through InterfaceSettings and the world
    // laser renders the same style at half the size.
    private static ReticleShape _reticleStyle = ReticleShape.Ring;
    public static ReticleShape ReticleStyle
    {
        get => _reticleStyle;
        set => SetEnum(ref _reticleStyle, value);
    }

    private static float _reticleSize = 12f;
    public static float ReticleSize
    {
        get => _reticleSize;
        set => SetValue(ref _reticleSize, SettingsCatalog.ReticleSize.Clamp(value));
    }

    private static float _reticleThickness = 2f;
    public static float ReticleThickness
    {
        get => _reticleThickness;
        set => SetValue(ref _reticleThickness, SettingsCatalog.ReticleThickness.Clamp(value));
    }

    // Lengths read in feet and inches instead of metres. Only the text changes: every length is still
    // kept in metres, so flipping this never moves a value.
    private static bool _imperialUnits;
    public static bool ImperialUnits
    {
        get => _imperialUnits;
        set
        {
            if (_imperialUnits != value)
                UnitsVersion++;
            SetValue(ref _imperialUnits, value);
        }
    }

    // Moves on every flip of the units. Text already showing a length checks it to know it has to be
    // written again, though the value under it is the same.
    public static int UnitsVersion { get; private set; }

    // The label beside the pointer naming what it is on, shown once it has stayed there TooltipDelay seconds.
    private static bool _tooltips = true;
    public static bool Tooltips
    {
        get => _tooltips;
        set => SetValue(ref _tooltips, value);
    }

    private static float _tooltipDelay = 0.6f;
    public static float TooltipDelay
    {
        get => _tooltipDelay;
        set => SetValue(ref _tooltipDelay, SettingsCatalog.TooltipDelay.Clamp(value));
    }

    // POST PROCESSING
    //
    // The image goes out linear, with no tonemapping curve and no colour adjustment: a curve is a cost on
    // every pixel of both eyes, and how a world is graded belongs to the world, not to a viewer setting.
    // -xlinka

    // Glow intensity. 0 turns the whole glow pass off rather than running it at zero strength, so the
    // cost goes away instead of being paid for nothing.
    private static float _bloom = 0.5f;
    public static float Bloom
    {
        get => _bloom;
        set => SetValue(ref _bloom, SettingsCatalog.Bloom.Clamp(value));
    }

    // Ambient occlusion strength, 0 = the pass is off, full by default. Contact shadow is what grounds
    // a body in a room: without it feet float over the floor and every corner reads as the same flat
    // ambient. Not available on the standalone renderer, where the applier leaves it off whatever this
    // says.
    private static float _ambientOcclusion = 1f;
    public static float AmbientOcclusion
    {
        get => _ambientOcclusion;
        set => SetValue(ref _ambientOcclusion, SettingsCatalog.AmbientOcclusion.Clamp(value));
    }

    // Screen-space reflections. Off by default: a full ray march per pixel, and only glossy floors
    // show it.
    private static bool _screenSpaceReflections;
    public static bool ScreenSpaceReflections
    {
        get => _screenSpaceReflections;
        set => SetValue(ref _screenSpaceReflections, value);
    }

    // Screen-space indirect lighting: diffuse bounce between nearby surfaces, the partner effect to
    // ambient occlusion in the engine's own guide and one of the two screen-space passes that render
    // clean in a headset. Off by default; it costs a second half-resolution pass like the occlusion.
    private static bool _screenSpaceIndirectLighting;
    public static bool ScreenSpaceIndirectLighting
    {
        get => _screenSpaceIndirectLighting;
        set => SetValue(ref _screenSpaceIndirectLighting, value);
    }

    public static readonly string[] ShadowQualityOptions = { "Hard", "Soft Low", "Soft Medium", "Soft High" };

    // The renderer's soft-shadow filter, which nothing in this engine has ever set - so we have been
    // running on the platform default for every world, on every machine. It is one of the largest
    // single levers on shadow cost: the same geometry measured a 2.1x swing across this range. -xlinka
    private static int _shadowQuality = 1;
    public static int ShadowQuality
    {
        get => _shadowQuality;
        set => SetValue(ref _shadowQuality, System.Math.Clamp(value, 0, ShadowQualityOptions.Length - 1));
    }

    public static string DescribeShadowQuality(int value)
        => value >= 0 && value < ShadowQualityOptions.Length ? ShadowQualityOptions[value] : ShadowQualityOptions[1];

    public static readonly int[] AntiAliasingOptions = { 0, 1, 2, 4, 8 };

    // Desktop mode: 0 is off, 1 is FXAA, and 2/4/8 retain the saved MSAA sample counts.
    // 2x MSAA by default: FXAA blurs the whole frame after the fact, and small panel text is the first
    // thing it eats. 4x cost a sixth of the frame with a toon-shaded avatar up close. Headset rendering
    // has its own quality profile.
    private static int _antiAliasing = 2;
    public static int AntiAliasing
    {
        get => _antiAliasing;
        set => SetValue(ref _antiAliasing, SnapAntiAliasing(value));
    }

    public static int SnapAntiAliasing(int value)
    {
        int best = 0;
        foreach (int option in AntiAliasingOptions)
        {
            if (option <= value && option > best)
                best = option;
        }
        return best;
    }

    public static string DescribeAntiAliasing(int value) => SnapAntiAliasing(value) switch
    {
        0 => "Off",
        1 => "FXAA",
        int samples => $"{samples}x MSAA",
    };

    // Multiplier on every directional light's shadow distance and on how far past its range a point or
    // spot light still gets a shadow. Below 1 shrinks the cascade range, which is the cheapest real win
    // on the shadow pass; above 1 buys distant shadows back. Applied by the light hooks on change, same
    // shape as LodBias.
    private static float _shadowDistanceScale = 1f;
    public static float ShadowDistanceScale
    {
        get => _shadowDistanceScale;
        set => SetValue(ref _shadowDistanceScale, SettingsCatalog.ShadowDistanceScale.Clamp(value));
    }

    // How many point and spot lights may cast a shadow at once, picked by how near and bright each is
    // to the viewer; the rest still light, unshadowed. Each one is up to six scene passes every frame
    // something in its range moves, so this is the shadow cost of a world full of lamps. A headset gets
    // its own, lower figure: two eyes at 90 Hz on the same budget a monitor gets at 60. 0 turns point and
    // spot shadows off. -xlinka
    private static int _shadowedLights = 4;
    public static int ShadowedLights
    {
        get => _shadowedLights;
        set => SetValue(ref _shadowedLights, SettingsCatalog.ShadowedLights.ClampInt(value));
    }

    private static int _shadowedLightsVR = 2;
    public static int ShadowedLightsVR
    {
        get => _shadowedLightsVR;
        set => SetValue(ref _shadowedLightsVR, SettingsCatalog.ShadowedLights.ClampInt(value));
    }

    // Times a second the shadow of something that keeps moving (a fan, a turntable, a spinning sign) is
    // brought up to date, 0 for every frame. A point light re-renders all six faces whenever anything it
    // covers moves, so one spinner kept every lamp around it rendering every frame; at this rate the lamps
    // sit on their cached shadow in between and the moving thing's shadow steps instead. Avatars and what
    // people hold are never slowed. -xlinka
    private static int _movingShadowRate = 15;
    public static int MovingShadowRate
    {
        get => _movingShadowRate;
        set => SetValue(ref _movingShadowRate, SettingsCatalog.MovingShadowRate.ClampOptionalInt(value));
    }

    // Somebody else's avatar behind you or a long way off swings its hair and runs its idle animation less
    // often, or not at all until it is back in view (AvatarViewLod). Your own is never touched. -xlinka
    private static bool _reduceDistantAvatars = true;
    public static bool ReduceDistantAvatars
    {
        get => _reduceDistantAvatars;
        set => SetValue(ref _reduceDistantAvatars, value);
    }

    // Metres past which other people's avatars cast no shadow, and past which they are not drawn. 0 is never.
    // A crowd's shadows were as many triangles again as the crowd. -xlinka
    private static float _avatarShadowDistance = 15f;
    public static float AvatarShadowDistance
    {
        get => _avatarShadowDistance;
        set => SetValue(ref _avatarShadowDistance, SettingsCatalog.AvatarShadowDistance.ClampOptional(value));
    }

    private static float _avatarDrawDistance = 80f;
    public static float AvatarDrawDistance
    {
        get => _avatarDrawDistance;
        set => SetValue(ref _avatarDrawDistance, SettingsCatalog.AvatarDrawDistance.ClampOptional(value));
    }

    // Past this many metres somebody else's avatar is drawn as a picture of itself, taken now and then from
    // where you stand, and stops being posed in between. 0 is never. -xlinka
    private static float _avatarStandInDistance = 25f;
    public static float AvatarStandInDistance
    {
        get => _avatarStandInDistance;
        set => SetValue(ref _avatarStandInDistance, SettingsCatalog.AvatarStandInDistance.ClampOptional(value));
    }

    // How many of the nearest other avatars keep their full body whatever the distance; everyone further down the
    // list is a picture too. 0 is everyone.
    private static int _avatarFullDetailCount = 16;
    public static int AvatarFullDetailCount
    {
        get => _avatarFullDetailCount;
        set => SetValue(ref _avatarFullDetailCount, SettingsCatalog.AvatarFullDetailCount.ClampOptionalInt(value));
    }

    // Whose avatars keep the shaders they were made with: everyone's, only friends', or nobody else's. The rest
    // wear a plain lit material made from each surface's own colour, texture, cutout and glow. A toon shader
    // cost eight times a plain material on every pixel it covered, and an avatar up close covers all of them.
    // Your own is never touched. -xlinka
    public const int OthersShadersFull = 0;
    public const int OthersShadersFriends = 1;
    public const int OthersShadersSimple = 2;
    public static readonly string[] OthersAvatarShaderOptions = { "Full", "Full for Friends", "Simple" };
    private static int _othersAvatarShaders = OthersShadersFull;
    public static int OthersAvatarShaders
    {
        get => _othersAvatarShaders;
        set => SetValue(ref _othersAvatarShaders, System.Math.Clamp(value, 0, OthersAvatarShaderOptions.Length - 1));
    }

    // PERSISTENCE - values apply live and land in the settings store (Settings -> config.dat) through
    // Commit, which SaveIfDue runs a moment after the last change.

    private const string KeyMouseSensitivity = "Engine.Input.MouseSensitivity";
    private const string KeyMouseSmoothing = "Engine.Input.MouseSmoothing";
    private const string KeyStickLookSpeed = "Engine.Input.StickLookSpeed";
    private const string KeyInvertStickLook = "Engine.Input.InvertStickLook";
    private const string KeyLaserSmoothing = "Engine.Input.LaserSmoothing";
    private const string KeyHapticsStrength = "Engine.Input.HapticsStrength";
    private const string KeyNoclipSpeed = "Engine.Input.NoclipSpeed";
    private const string KeyPreferredLocomotion = "Engine.Input.PreferredLocomotion";
    private const string KeyPrimaryHand = "Engine.Input.PrimaryHand";
    private const string KeyPhotoSize = "Engine.Photos.Size";
    private const string KeyPhotoEncoding = "Engine.Photos.Encoding";
    private const string KeyPhotoTimerSeconds = "Engine.Photos.TimerSeconds";
    private const string KeyHandFrame = "Engine.Photos.HandFrame";
    private const string KeyPhotosToInventory = "Engine.Photos.ToInventory";
    private const string KeyShowCapture = "Engine.Photos.ShowCapture";
    private const string KeyUserHeight = "Engine.Avatar.UserHeight";
    private const string KeySeatedMode = "Engine.Avatar.SeatedMode";
    private const string KeySeatedHeadHeight = "Engine.Avatar.SeatedHeadHeight";
    private const string KeyAvatarArrival = "Engine.Avatar.Arrival";
    private const string KeyChatBubbles = "Engine.Interface.ChatBubbles";
    private const string KeyDesktopChat = "Engine.Interface.DesktopChat";
    private const string KeyMasterVolume = "Engine.Audio.MasterVolume";
    private const string KeySoundEffectVolume = "Engine.Audio.SoundEffectVolume";
    private const string KeyMultimediaVolume = "Engine.Audio.MultimediaVolume";
    private const string KeyVoiceVolume = "Engine.Audio.VoiceVolume";
    private const string KeyUIVolume = "Engine.Audio.UIVolume";
    private const string KeyWhisperAttenuationVolume = "Engine.Audio.WhisperAttenuationVolume";
    private const string KeyDefaultStreamBitrateKbps = "Engine.Audio.DefaultStreamBitrateKbps";
    private const string KeyAudioInputDevice = "Engine.Audio.InputDevice";
    private const string KeyAudioOutputDevice = "Engine.Audio.OutputDevice";
    private const string KeyStreamDevice = "Engine.Audio.StreamDevice";
    private const string KeyStreamOutputDevice = "Engine.Audio.StreamOutputDevice";
    // The camera keeps its own settings under this prefix, all of them following the account.
    public const string CameraKeyPrefix = "Engine.Camera.";
    private const string KeyDesktopMonitor = "Engine.Desktop.Monitor";
    private const string KeyDesktopFollowCursor = "Engine.Desktop.FollowCursor";
    private const string KeyDesktopBrightness = "Engine.Desktop.Brightness";
    private const string KeyDesktopPortalToken = "Engine.Desktop.PortalToken";
    private const string KeyMicrophoneMuted = "Engine.Audio.MicrophoneMuted";
    private const string KeyVoiceNormalization = "Engine.Audio.VoiceNormalization";
    private const string KeyNoiseSuppression = "Engine.Audio.NoiseSuppression";
    private const string KeyNoiseGateThreshold = "Engine.Audio.NoiseGateThreshold";
    private const string KeyNoiseGateAttack = "Engine.Audio.NoiseGateAttack";
    private const string KeyNoiseGateHold = "Engine.Audio.NoiseGateHold";
    private const string KeyNoiseGateRelease = "Engine.Audio.NoiseGateRelease";
    private const string KeyNormalizationThreshold = "Engine.Audio.NormalizationThreshold";
    private const string KeyVoiceTargetLevel = "Engine.Audio.VoiceTargetLevel";
    private const string KeyMicrophoneGain = "Engine.Audio.MicrophoneGain";
    private const string KeyMicrophoneGainSeedPrefix = "Engine.Audio.MicrophoneGainSeed.";
    private const string KeyVideoCookiesBrowser = "Engine.Media.VideoCookiesBrowser";
    private const string KeyVideoMaxHeight = "Engine.Media.VideoMaxHeight";
    private const string KeyAssetCacheGigabytes = "Engine.Storage.AssetCacheGigabytes";
    private const string KeyNatNode = "Engine.Network.NatNode";
    private const string KeyIpv6 = "Engine.Network.Ipv6";
    private const string KeyPortForwarding = "Engine.Network.PortForwarding";
    private const string KeyNatPunch = "Engine.Network.NatPunch";
    private const string KeyNatRelay = "Engine.Network.NatRelay";
    private const string KeyTcpFallback = "Engine.Network.TcpFallback";
    private const string KeyPreferQuic = "Engine.Network.PreferQuic";
    private const string KeySteamConnections = "Engine.Network.SteamConnections";
    private const string KeyLanDiscovery = "Engine.Network.LanDiscovery";
    private const string KeyHostingPort = "Engine.Network.HostingPort";
    private const string KeyDefaultJoinLevel = "Engine.Worlds.DefaultJoinLevel";
    private const string KeyVSync = "Engine.Video.VSync";
    private const string KeyMaxFps = "Engine.Video.MaxFps";
    private const string KeyBackgroundFps = "Engine.Video.BackgroundFps";
    private const string KeyFullscreen = "Engine.Video.Fullscreen";
    private const string KeyRenderScale = "Engine.Video.RenderScale";
    private const string KeyDynamicResolution = "Engine.Video.DynamicResolution";
    private const string KeyUpscaler = "Engine.Video.Upscaler";
    private const string KeyResolutionHeight = "Engine.Video.ResolutionHeight";
    private const string KeyFieldOfView = "Engine.Video.FieldOfView";
    private const string KeyMaxTextureSize = "Engine.Video.MaxTextureSize";
    private const string KeyMobileTextureCeiling = "Engine.Video.MobileTextureCeiling";
    private const string KeySharpPanelDefaults = "Engine.Video.SharpPanelDefaults";
    private const string KeyMsaaTwoDefault = "Engine.Video.MsaaTwoDefault";
    private const string KeyReflectionsEnabled = "Engine.Video.ReflectionsEnabled";
    private const string KeyLodBias = "Engine.Video.LodBias";
    private const string KeyMeshLodThreshold = "Engine.Video.MeshLodThreshold";
    private const string KeyShadowDistanceScale = "Engine.Video.ShadowDistanceScale";
    private const string KeyShadowedLights = "Engine.Video.ShadowedLights";
    private const string KeyShadowedLightsVR = "Engine.Video.ShadowedLightsVR";
    private const string KeyMovingShadowRate = "Engine.Video.MovingShadowRate";
    private const string KeyReduceDistantAvatars = "Engine.Video.ReduceDistantAvatars";
    private const string KeyAvatarShadowDistance = "Engine.Video.AvatarShadowDistance";
    private const string KeyAvatarDrawDistance = "Engine.Video.AvatarDrawDistance";
    private const string KeyAvatarStandInDistance = "Engine.Video.AvatarStandInDistance";
    private const string KeyAvatarFullDetailCount = "Engine.Video.AvatarFullDetailCount";
    private const string KeyOthersAvatarShaders = "Engine.Video.OthersAvatarShaders";
    private const string KeyBloom = "Engine.Video.Bloom";
    private const string KeyAmbientOcclusion = "Engine.Video.AmbientOcclusion";
    private const string KeyScreenSpaceReflections = "Engine.Video.ScreenSpaceReflections";
    private const string KeyScreenSpaceIndirectLighting = "Engine.Video.ScreenSpaceIndirectLighting";
    private const string KeyAntiAliasing = "Engine.Video.AntiAliasing";
    private const string KeyShadowQuality = "Engine.Video.ShadowQuality";
    private const string KeyTurnMode = "Engine.Comfort.TurnMode";
    private const string KeySnapTurnAngle = "Engine.Comfort.SnapTurnAngle";
    private const string KeySmoothTurnSpeed = "Engine.Comfort.SmoothTurnSpeed";
    private const string KeyMovementDirection = "Engine.Comfort.MovementDirection";
    private const string KeyReticleStyle = "Engine.Interface.ReticleStyle";
    private const string KeyGizmoControls = "Engine.Interface.GizmoControls";
    private const string KeyReticleSize = "Engine.Interface.ReticleSize";
    private const string KeyReticleThickness = "Engine.Interface.ReticleThickness";
    private const string KeyImperialUnits = "Engine.Interface.ImperialUnits";
    private const string KeyTooltips = "Engine.Interface.Tooltips";
    private const string KeyTooltipDelay = "Engine.Interface.TooltipDelay";

    // Reads every value again, for when the account brought newer ones. On the main thread: what listens
    // to Changed reaches into the windowing and the renderer. A change made here and not saved yet goes in
    // first, newer than what the account brought, or the reload would quietly undo it.
    public static void Reload()
    {
        if (_dirty)
            Commit();
        _loaded = false;
        Load();
    }

    // Which settings follow the account to every computer it signs in on, and which stay with this one.
    // What depends on the hardware in front of you stays: devices, the graphics, master volume (headphones
    // differ), how the microphone is cleaned up, which monitor and browser. What is about how you like
    // to play goes with you.
    private static readonly HashSet<string> AccountKeys = new(StringComparer.Ordinal)
    {
        KeyMouseSensitivity, KeyMouseSmoothing, KeyStickLookSpeed, KeyInvertStickLook, KeyNoclipSpeed, KeyPreferredLocomotion, KeyPrimaryHand,
        KeyLaserSmoothing, KeyHapticsStrength,
        KeyPhotoSize, KeyPhotoEncoding, KeyPhotoTimerSeconds, KeyHandFrame, KeyPhotosToInventory, KeyShowCapture,
        KeyUserHeight, KeyAvatarArrival, KeyChatBubbles, KeyDesktopChat,
        KeySoundEffectVolume, KeyMultimediaVolume, KeyVoiceVolume, KeyUIVolume, KeyWhisperAttenuationVolume,
        KeyDefaultStreamBitrateKbps,
        KeyVideoMaxHeight,
        KeyDesktopFollowCursor, KeyDesktopBrightness,
        KeyTurnMode, KeySnapTurnAngle, KeySmoothTurnSpeed, KeyMovementDirection,
        KeyReticleStyle, KeyGizmoControls, KeyReticleSize, KeyReticleThickness,
        KeyImperialUnits, KeyTooltips, KeyTooltipDelay,
        KeyDefaultJoinLevel,
    };

    public static bool FollowsAccount(string key)
        => AccountKeys.Contains(key) || key.StartsWith(CameraKeyPrefix, StringComparison.Ordinal);

    // A Steam Deck starts from its own defaults, read in as the fallbacks Load uses, so they only stand where
    // the store holds nothing: a value somebody picked always wins, and nothing is written for them, so a later
    // build can still move a Deck default. Only this machine's keys: what follows the account is the person's,
    // and their Deck should not push a handheld's choices onto their desktop.
    //
    // Fullscreen, so the window is the panel's own 1280x800 rather than a 16:9 desktop window gamescope has to
    // letterbox; the view a little narrower, so a panel across the room is not a smudge on a 7-inch screen; hard
    // shadows from two lights, out to 60% of the distance; no ambient occlusion; level of detail drops sooner;
    // other people's avatars stop drawing at 40 m and casting at 8, and wear their own shaders only for friends (a
    // toon shader up close costs eight times a plain one on every pixel). Pictures cap at 2048 like a headset's.
    private static void UseDeckDefaults()
    {
        _fullscreen = true;
        _fieldOfView = 70f;
        _shadowQuality = 0;
        _shadowDistanceScale = 0.6f;
        _shadowedLights = 2;
        _ambientOcclusion = 0f;
        _lodBias = 0.75f;
        _avatarDrawDistance = 40f;
        _avatarShadowDistance = 8f;
        _avatarStandInDistance = 15f;
        _avatarFullDetailCount = 8;
        _othersAvatarShaders = OthersShadersFriends;
    }

    public static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (SteamDeckPlatform.IsSteamDeck)
                UseDeckDefaults();
            // Before the first read the fields still hold what this build starts from.
            if (_defaults.Count == 0)
            {
                foreach (var (key, value) in Current())
                    _defaults[key] = value;
            }
            _mouseSensitivity = SettingsCatalog.MouseSensitivity.Clamp(Settings.ReadValue(KeyMouseSensitivity, _mouseSensitivity));
            _mouseSmoothing = SettingsCatalog.MouseSmoothing.Clamp(Settings.ReadValue(KeyMouseSmoothing, _mouseSmoothing));
            _stickLookSpeed = SettingsCatalog.StickLookSpeed.Clamp(Settings.ReadValue(KeyStickLookSpeed, _stickLookSpeed));
            _invertStickLook = Settings.ReadValue(KeyInvertStickLook, _invertStickLook);
            _laserSmoothing = SettingsCatalog.LaserSmoothing.Clamp(Settings.ReadValue(KeyLaserSmoothing, _laserSmoothing));
            _hapticsStrength = SettingsCatalog.HapticsStrength.Clamp(Settings.ReadValue(KeyHapticsStrength, _hapticsStrength));
            _noclipSpeed = SettingsCatalog.NoclipSpeed.Clamp(Settings.ReadValue(KeyNoclipSpeed, _noclipSpeed));
            _preferredLocomotion = Settings.ReadValue(KeyPreferredLocomotion, _preferredLocomotion) ?? string.Empty;
            _primaryHand = ReadEnum(KeyPrimaryHand, _primaryHand) == Input.Chirality.Left ? Input.Chirality.Left : Input.Chirality.Right;
            _photoSize = System.Math.Clamp(Settings.ReadValue(KeyPhotoSize, _photoSize), 0, PhotoSizes.Length - 1);
            _photoEncoding = ReadEnum(KeyPhotoEncoding, _photoEncoding);
            _photoTimerSeconds = SettingsCatalog.PhotoTimerSeconds.Clamp(Settings.ReadValue(KeyPhotoTimerSeconds, _photoTimerSeconds));
            _handFrame = Settings.ReadValue(KeyHandFrame, _handFrame);
            _photosToInventory = Settings.ReadValue(KeyPhotosToInventory, _photosToInventory);
            _showCapture = Settings.ReadValue(KeyShowCapture, _showCapture);
            _userHeight = SettingsCatalog.UserHeight.Clamp(Settings.ReadValue(KeyUserHeight, _userHeight));
            _seatedMode = Settings.ReadValue(KeySeatedMode, _seatedMode);
            _seatedHeadHeight = SettingsCatalog.SeatedHeadHeight.Clamp(Settings.ReadValue(KeySeatedHeadHeight, _seatedHeadHeight));
            _avatarArrival = Settings.ReadValue(KeyAvatarArrival, _avatarArrival);
            _showChatBubbles = Settings.ReadValue(KeyChatBubbles, _showChatBubbles);
            _showDesktopChat = Settings.ReadValue(KeyDesktopChat, _showDesktopChat);
            _masterVolume = SettingsCatalog.MasterVolume.Clamp(Settings.ReadValue(KeyMasterVolume, _masterVolume));
            _soundEffectVolume = SettingsCatalog.SoundEffectVolume.Clamp(Settings.ReadValue(KeySoundEffectVolume, _soundEffectVolume));
            _multimediaVolume = SettingsCatalog.MultimediaVolume.Clamp(Settings.ReadValue(KeyMultimediaVolume, _multimediaVolume));
            _voiceVolume = SettingsCatalog.VoiceVolume.Clamp(Settings.ReadValue(KeyVoiceVolume, _voiceVolume));
            _uiVolume = SettingsCatalog.UIVolume.Clamp(Settings.ReadValue(KeyUIVolume, _uiVolume));
            _whisperAttenuationVolume = SettingsCatalog.WhisperAttenuationVolume.Clamp(Settings.ReadValue(KeyWhisperAttenuationVolume, _whisperAttenuationVolume));
            _defaultStreamBitrateKbps = SettingsCatalog.DefaultStreamBitrateKbps.ClampInt(Settings.ReadValue(KeyDefaultStreamBitrateKbps, _defaultStreamBitrateKbps));
            _audioInputDevice = Settings.ReadValue(KeyAudioInputDevice, _audioInputDevice) ?? string.Empty;
            _audioOutputDevice = Settings.ReadValue(KeyAudioOutputDevice, _audioOutputDevice) ?? string.Empty;
            _streamDevice = Settings.ReadValue(KeyStreamDevice, _streamDevice) ?? string.Empty;
            _streamOutputDevice = Settings.ReadValue(KeyStreamOutputDevice, _streamOutputDevice) ?? string.Empty;
            _desktopMonitor = Settings.ReadValue(KeyDesktopMonitor, _desktopMonitor) ?? string.Empty;
            _desktopFollowCursor = Settings.ReadValue(KeyDesktopFollowCursor, _desktopFollowCursor);
            _desktopBrightness = SettingsCatalog.DesktopBrightness.Clamp(Settings.ReadValue(KeyDesktopBrightness, _desktopBrightness));
            _desktopPortalToken = Settings.ReadValue(KeyDesktopPortalToken, _desktopPortalToken) ?? string.Empty;
            _microphoneMuted = Settings.ReadValue(KeyMicrophoneMuted, _microphoneMuted);
            _voiceNormalization = Settings.ReadValue(KeyVoiceNormalization, _voiceNormalization);
            _noiseSuppression = Settings.ReadValue(KeyNoiseSuppression, _noiseSuppression);
            _noiseGateThreshold = SettingsCatalog.NoiseGateThreshold.Clamp(Settings.ReadValue(KeyNoiseGateThreshold, _noiseGateThreshold));
            _noiseGateAttack = SettingsCatalog.NoiseGateAttack.Clamp(Settings.ReadValue(KeyNoiseGateAttack, _noiseGateAttack));
            _noiseGateHold = SettingsCatalog.NoiseGateHold.Clamp(Settings.ReadValue(KeyNoiseGateHold, _noiseGateHold));
            _noiseGateRelease = SettingsCatalog.NoiseGateRelease.Clamp(Settings.ReadValue(KeyNoiseGateRelease, _noiseGateRelease));
            _normalizationThreshold = SettingsCatalog.NormalizationThreshold.Clamp(Settings.ReadValue(KeyNormalizationThreshold, _normalizationThreshold));
            _voiceTargetLevel = SettingsCatalog.VoiceTargetLevel.Clamp(Settings.ReadValue(KeyVoiceTargetLevel, _voiceTargetLevel));
            _microphoneGain = SettingsCatalog.MicrophoneGain.Clamp(Settings.ReadValue(KeyMicrophoneGain, _microphoneGain));
            _videoCookiesBrowser = ReadEnum(KeyVideoCookiesBrowser, _videoCookiesBrowser);
            _videoMaxHeight = SettingsCatalog.VideoMaxHeight.ClampInt(Settings.ReadValue(KeyVideoMaxHeight, _videoMaxHeight));
            int assetCache = Settings.ReadValue(KeyAssetCacheGigabytes, 0);
            _assetCacheGigabytes = assetCache <= 0 ? 0 : SettingsCatalog.AssetCacheGigabytes.ClampInt(assetCache);
            _natNode = (Settings.ReadValue(KeyNatNode, _natNode) ?? string.Empty).Trim();
            _ipv6 = Settings.ReadValue(KeyIpv6, _ipv6);
            _portForwarding = Settings.ReadValue(KeyPortForwarding, _portForwarding);
            _natPunch = Settings.ReadValue(KeyNatPunch, _natPunch);
            _natRelay = Settings.ReadValue(KeyNatRelay, _natRelay);
            _tcpFallback = Settings.ReadValue(KeyTcpFallback, _tcpFallback);
            _preferQuic = Settings.ReadValue(KeyPreferQuic, _preferQuic);
            _steamJoin = ReadEnum(KeySteamConnections, _steamJoin);
            _lanDiscovery = Settings.ReadValue(KeyLanDiscovery, _lanDiscovery);
            _hostingPort = SettingsCatalog.HostingPort.ClampOptionalInt(Settings.ReadValue(KeyHostingPort, _hostingPort));
            _defaultJoinLevel = SettingsCatalog.DefaultJoinLevel.Clamp(ReadEnum(KeyDefaultJoinLevel, _defaultJoinLevel));
            PushNetworkOptions();
            _vsync = Settings.ReadValue(KeyVSync, _vsync);
            int fps = Settings.ReadValue(KeyMaxFps, _maxFps);
            _maxFps = SettingsCatalog.MaxFps.ClampOptionalInt(fps);
            int bgFps = Settings.ReadValue(KeyBackgroundFps, _backgroundFps);
            _backgroundFps = SettingsCatalog.BackgroundFps.ClampOptionalInt(bgFps);
            _fullscreen = Settings.ReadValue(KeyFullscreen, _fullscreen);
            _renderScale = SettingsCatalog.RenderScale.Clamp(Settings.ReadValue(KeyRenderScale, _renderScale));
            _fieldOfView = SettingsCatalog.FieldOfView.Clamp(Settings.ReadValue(KeyFieldOfView, _fieldOfView));
            _dynamicResolution = Settings.ReadValue(KeyDynamicResolution, _dynamicResolution);
            _upscaler = SettingsCatalog.Upscaler.ClampInt(Settings.ReadValue(KeyUpscaler, _upscaler));
            _resolutionHeight = System.Math.Max(0, Settings.ReadValue(KeyResolutionHeight, _resolutionHeight));
            _maxTextureSize = SnapTextureSize(Settings.ReadValue(KeyMaxTextureSize, DefaultMaxTextureSize));
            // Builds before this saved Original on phones and headsets without anyone choosing it, and
            // there a picture's memory is the same memory everything else runs in. Once, an Original saved
            // that way becomes the mobile default; Original chosen after that stays.
            if (Engine.UseConstrainedResources && !Settings.ReadValue(KeyMobileTextureCeiling, false))
            {
                if (_maxTextureSize == 0)
                    _maxTextureSize = DefaultMaxTextureSize;
                Settings.WriteValue(KeyMaxTextureSize, _maxTextureSize);
                Settings.WriteValue(KeyMobileTextureCeiling, true);
            }
            _reflectionsEnabled = Settings.ReadValue(KeyReflectionsEnabled, _reflectionsEnabled);
            _lodBias = SettingsCatalog.LodBias.Clamp(Settings.ReadValue(KeyLodBias, _lodBias));
            float meshLod = Settings.ReadValue(KeyMeshLodThreshold, _meshLodThreshold);
            _meshLodThreshold = SettingsCatalog.MeshLodThreshold.ClampOptional(meshLod);
            _shadowDistanceScale = SettingsCatalog.ShadowDistanceScale.Clamp(Settings.ReadValue(KeyShadowDistanceScale, _shadowDistanceScale));
            _shadowedLights = SettingsCatalog.ShadowedLights.ClampInt(Settings.ReadValue(KeyShadowedLights, _shadowedLights));
            _shadowedLightsVR = SettingsCatalog.ShadowedLights.ClampInt(Settings.ReadValue(KeyShadowedLightsVR, _shadowedLightsVR));
            _movingShadowRate = SettingsCatalog.MovingShadowRate.ClampOptionalInt(Settings.ReadValue(KeyMovingShadowRate, _movingShadowRate));
            _reduceDistantAvatars = Settings.ReadValue(KeyReduceDistantAvatars, _reduceDistantAvatars);
            _avatarShadowDistance = SettingsCatalog.AvatarShadowDistance.ClampOptional(Settings.ReadValue(KeyAvatarShadowDistance, _avatarShadowDistance));
            _avatarDrawDistance = SettingsCatalog.AvatarDrawDistance.ClampOptional(Settings.ReadValue(KeyAvatarDrawDistance, _avatarDrawDistance));
            _avatarStandInDistance = SettingsCatalog.AvatarStandInDistance.ClampOptional(Settings.ReadValue(KeyAvatarStandInDistance, _avatarStandInDistance));
            _avatarFullDetailCount = SettingsCatalog.AvatarFullDetailCount.ClampOptionalInt(Settings.ReadValue(KeyAvatarFullDetailCount, _avatarFullDetailCount));
            _othersAvatarShaders = System.Math.Clamp(Settings.ReadValue(KeyOthersAvatarShaders, _othersAvatarShaders), 0, OthersAvatarShaderOptions.Length - 1);
            _bloom = SettingsCatalog.Bloom.Clamp(Settings.ReadValue(KeyBloom, _bloom));
            _ambientOcclusion = SettingsCatalog.AmbientOcclusion.Clamp(Settings.ReadValue(KeyAmbientOcclusion, _ambientOcclusion));
            _screenSpaceReflections = Settings.ReadValue(KeyScreenSpaceReflections, _screenSpaceReflections);
            _screenSpaceIndirectLighting = Settings.ReadValue(KeyScreenSpaceIndirectLighting, _screenSpaceIndirectLighting);
            _antiAliasing = SnapAntiAliasing(Settings.ReadValue(KeyAntiAliasing, _antiAliasing));
            // Builds before this saved FXAA and dynamic resolution as everyone's choice without anyone
            // making it. Once, those two old defaults move to the sharp ones; picked again after, they stay.
            if (!Settings.ReadValue(KeySharpPanelDefaults, false))
            {
                if (_antiAliasing == 1)
                    _antiAliasing = 2;
                _dynamicResolution = false;
                Settings.WriteValue(KeyAntiAliasing, _antiAliasing);
                Settings.WriteValue(KeyDynamicResolution, _dynamicResolution);
                Settings.WriteValue(KeySharpPanelDefaults, true);
            }
            // That move wrote 4x into everyone's store as if they had picked it. Once, 4x goes to the new 2x
            // default; picked again after, it stays.
            if (!Settings.ReadValue(KeyMsaaTwoDefault, false))
            {
                if (_antiAliasing == 4)
                    _antiAliasing = 2;
                Settings.WriteValue(KeyAntiAliasing, _antiAliasing);
                Settings.WriteValue(KeyMsaaTwoDefault, true);
            }
            _shadowQuality = System.Math.Clamp(Settings.ReadValue(KeyShadowQuality, _shadowQuality), 0, ShadowQualityOptions.Length - 1);
            _turnMode = ReadEnum(KeyTurnMode, _turnMode);
            _snapTurnAngle = SettingsCatalog.SnapTurnAngle.Clamp(Settings.ReadValue(KeySnapTurnAngle, _snapTurnAngle));
            _smoothTurnSpeed = SettingsCatalog.SmoothTurnSpeed.Clamp(Settings.ReadValue(KeySmoothTurnSpeed, _smoothTurnSpeed));
            _movementDirection = ReadEnum(KeyMovementDirection, _movementDirection);
            _reticleStyle = ReadEnum(KeyReticleStyle, _reticleStyle);
            _gizmoControls = ReadEnum(KeyGizmoControls, GizmoControlStyle.ContextMenu);
            if (!Enum.IsDefined(_gizmoControls)) _gizmoControls = GizmoControlStyle.ContextMenu;
            _reticleSize = SettingsCatalog.ReticleSize.Clamp(Settings.ReadValue(KeyReticleSize, _reticleSize));
            _reticleThickness = SettingsCatalog.ReticleThickness.Clamp(Settings.ReadValue(KeyReticleThickness, _reticleThickness));
            // A reload can bring the other units from the account, and that counts as a flip too.
            bool imperial = Settings.ReadValue(KeyImperialUnits, _imperialUnits);
            if (imperial != _imperialUnits)
                UnitsVersion++;
            _imperialUnits = imperial;
            _tooltips = Settings.ReadValue(KeyTooltips, _tooltips);
            _tooltipDelay = SettingsCatalog.TooltipDelay.Clamp(Settings.ReadValue(KeyTooltipDelay, _tooltipDelay));
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"EngineSettings: failed to load: {ex.Message}");
        }

        RememberStored();
        _dirty = false;
    }

    // Saved on its own a moment after the last change, so a slider being dragged is one write and not a
    // write a frame, and saved again on the way out by Engine.Dispose. Only what differs from the store is
    // written: a key this machine never changed stays absent, so its default can still move, and it never
    // reaches the account as a fresh change that would beat a value another computer actually chose.
    private const double SaveDelaySeconds = 1.0;

    private static readonly Dictionary<string, string> _stored = new(StringComparer.Ordinal);

    // What each setting is before anybody picks anything, as the store writes it. Load falls back to the live
    // value for a key the store does not hold, so taking a key out alone leaves the old value standing; a reset
    // writes this, reloads, then takes the key out (SettingsTransfer does).
    private static readonly Dictionary<string, string> _defaults = new(StringComparer.Ordinal);

    public static bool TryGetDefault(string key, out string value)
    {
        Load();
        return _defaults.TryGetValue(key, out value!);
    }
    private static long _changedAt;

    // Every frame, from the engine's update on the main thread, where the store's listeners expect to be.
    public static void SaveIfDue()
    {
        if (_dirty && System.Diagnostics.Stopwatch.GetElapsedTime(_changedAt).TotalSeconds >= SaveDelaySeconds)
            Commit();
    }

    public static void Commit()
    {
        // Before Load there is nothing to tell a change from a default, and every default would go down as a choice.
        if (!_loaded)
            return;
        _dirty = false;
        try
        {
            foreach (var (key, value) in Current())
            {
                if (_stored.TryGetValue(key, out var stored) && stored == value)
                    continue;
                Settings.WriteValue(key, value);
                _stored[key] = value;
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"EngineSettings: failed to save: {ex.Message}");
        }
    }

    // What the store holds now, as Load read it, so Commit can tell a change from a default.
    private static void RememberStored()
    {
        _stored.Clear();
        foreach (var (key, value) in Current())
            _stored[key] = value;
    }

    private static string Text<T>(T value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static IEnumerable<(string Key, string Value)> Current()
    {
        yield return (KeyMouseSensitivity, Text(_mouseSensitivity));
        yield return (KeyMouseSmoothing, Text(_mouseSmoothing));
        yield return (KeyStickLookSpeed, Text(_stickLookSpeed));
        yield return (KeyInvertStickLook, Text(_invertStickLook));
        yield return (KeyLaserSmoothing, Text(_laserSmoothing));
        yield return (KeyHapticsStrength, Text(_hapticsStrength));
        yield return (KeyNoclipSpeed, Text(_noclipSpeed));
        yield return (KeyPreferredLocomotion, Text(_preferredLocomotion));
        yield return (KeyPrimaryHand, _primaryHand.ToString());
        yield return (KeyPhotoSize, Text(_photoSize));
        yield return (KeyPhotoEncoding, _photoEncoding.ToString());
        yield return (KeyPhotoTimerSeconds, Text(_photoTimerSeconds));
        yield return (KeyHandFrame, Text(_handFrame));
        yield return (KeyPhotosToInventory, Text(_photosToInventory));
        yield return (KeyShowCapture, Text(_showCapture));
        yield return (KeyUserHeight, Text(_userHeight));
        yield return (KeySeatedMode, Text(_seatedMode));
        yield return (KeySeatedHeadHeight, Text(_seatedHeadHeight));
        yield return (KeyAvatarArrival, Text(_avatarArrival));
        yield return (KeyChatBubbles, Text(_showChatBubbles));
        yield return (KeyDesktopChat, Text(_showDesktopChat));
        yield return (KeyMasterVolume, Text(_masterVolume));
        yield return (KeySoundEffectVolume, Text(_soundEffectVolume));
        yield return (KeyMultimediaVolume, Text(_multimediaVolume));
        yield return (KeyVoiceVolume, Text(_voiceVolume));
        yield return (KeyUIVolume, Text(_uiVolume));
        yield return (KeyWhisperAttenuationVolume, Text(_whisperAttenuationVolume));
        yield return (KeyDefaultStreamBitrateKbps, Text(_defaultStreamBitrateKbps));
        yield return (KeyAudioInputDevice, Text(_audioInputDevice));
        yield return (KeyAudioOutputDevice, Text(_audioOutputDevice));
        yield return (KeyStreamDevice, Text(_streamDevice));
        yield return (KeyStreamOutputDevice, Text(_streamOutputDevice));
        yield return (KeyDesktopMonitor, Text(_desktopMonitor));
        yield return (KeyDesktopFollowCursor, Text(_desktopFollowCursor));
        yield return (KeyDesktopBrightness, Text(_desktopBrightness));
        yield return (KeyMicrophoneMuted, Text(_microphoneMuted));
        yield return (KeyVoiceNormalization, Text(_voiceNormalization));
        yield return (KeyNoiseSuppression, Text(_noiseSuppression));
        yield return (KeyNoiseGateThreshold, Text(_noiseGateThreshold));
        yield return (KeyNoiseGateAttack, Text(_noiseGateAttack));
        yield return (KeyNoiseGateHold, Text(_noiseGateHold));
        yield return (KeyNoiseGateRelease, Text(_noiseGateRelease));
        yield return (KeyNormalizationThreshold, Text(_normalizationThreshold));
        yield return (KeyVoiceTargetLevel, Text(_voiceTargetLevel));
        yield return (KeyMicrophoneGain, Text(_microphoneGain));
        yield return (KeyVideoCookiesBrowser, _videoCookiesBrowser.ToString());
        yield return (KeyVideoMaxHeight, Text(_videoMaxHeight));
        yield return (KeyAssetCacheGigabytes, Text(_assetCacheGigabytes));
        yield return (KeyNatNode, Text(_natNode));
        yield return (KeyIpv6, Text(_ipv6));
        yield return (KeyPortForwarding, Text(_portForwarding));
        yield return (KeyNatPunch, Text(_natPunch));
        yield return (KeyNatRelay, Text(_natRelay));
        yield return (KeyTcpFallback, Text(_tcpFallback));
        yield return (KeyPreferQuic, Text(_preferQuic));
        yield return (KeySteamConnections, _steamJoin.ToString());
        yield return (KeyLanDiscovery, Text(_lanDiscovery));
        yield return (KeyHostingPort, Text(_hostingPort));
        yield return (KeyDefaultJoinLevel, _defaultJoinLevel.ToString());
        yield return (KeyVSync, Text(_vsync));
        yield return (KeyMaxFps, Text(_maxFps));
        yield return (KeyBackgroundFps, Text(_backgroundFps));
        yield return (KeyFullscreen, Text(_fullscreen));
        yield return (KeyRenderScale, Text(_renderScale));
        yield return (KeyFieldOfView, Text(_fieldOfView));
        yield return (KeyDynamicResolution, Text(_dynamicResolution));
        yield return (KeyUpscaler, Text(_upscaler));
        yield return (KeyResolutionHeight, Text(_resolutionHeight));
        yield return (KeyMaxTextureSize, Text(_maxTextureSize));
        yield return (KeyReflectionsEnabled, Text(_reflectionsEnabled));
        yield return (KeyLodBias, Text(_lodBias));
        yield return (KeyMeshLodThreshold, Text(_meshLodThreshold));
        yield return (KeyShadowDistanceScale, Text(_shadowDistanceScale));
        yield return (KeyShadowedLights, Text(_shadowedLights));
        yield return (KeyShadowedLightsVR, Text(_shadowedLightsVR));
        yield return (KeyMovingShadowRate, Text(_movingShadowRate));
        yield return (KeyReduceDistantAvatars, Text(_reduceDistantAvatars));
        yield return (KeyAvatarShadowDistance, Text(_avatarShadowDistance));
        yield return (KeyAvatarDrawDistance, Text(_avatarDrawDistance));
        yield return (KeyAvatarStandInDistance, Text(_avatarStandInDistance));
        yield return (KeyAvatarFullDetailCount, Text(_avatarFullDetailCount));
        yield return (KeyOthersAvatarShaders, Text(_othersAvatarShaders));
        yield return (KeyBloom, Text(_bloom));
        yield return (KeyAmbientOcclusion, Text(_ambientOcclusion));
        yield return (KeyScreenSpaceReflections, Text(_screenSpaceReflections));
        yield return (KeyScreenSpaceIndirectLighting, Text(_screenSpaceIndirectLighting));
        yield return (KeyAntiAliasing, Text(_antiAliasing));
        yield return (KeyShadowQuality, Text(_shadowQuality));
        yield return (KeyTurnMode, _turnMode.ToString());
        yield return (KeySnapTurnAngle, Text(_snapTurnAngle));
        yield return (KeySmoothTurnSpeed, Text(_smoothTurnSpeed));
        yield return (KeyMovementDirection, _movementDirection.ToString());
        yield return (KeyReticleStyle, _reticleStyle.ToString());
        yield return (KeyGizmoControls, _gizmoControls.ToString());
        yield return (KeyReticleSize, Text(_reticleSize));
        yield return (KeyReticleThickness, Text(_reticleThickness));
        yield return (KeyImperialUnits, Text(_imperialUnits));
        yield return (KeyTooltips, Text(_tooltips));
        yield return (KeyTooltipDelay, Text(_tooltipDelay));
    }

    // Enums persist by NAME: an int would silently re-point at a different option the day someone
    // inserts a value in the middle of the enum.
    private static T ReadEnum<T>(string key, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(Settings.ReadValue(key, fallback.ToString()), ignoreCase: true, out var parsed) ? parsed : fallback;

    private static void SetValue<T>(ref T field, T value) where T : IEquatable<T>
    {
        if (field.Equals(value))
            return;
        field = value;
        MarkChanged();
    }

    private static void MarkChanged()
    {
        _dirty = true;
        _changedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Changed?.Invoke();
    }

    // Enums do not satisfy IEquatable<T>, so they need their own gate rather than the generic one.
    private static void SetEnum<T>(ref T field, T value) where T : struct, Enum
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        MarkChanged();
    }
}
