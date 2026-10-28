// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Media;
using Lumora.Core.Networking.Web;

namespace Lumora.Core.Components.UI;

// Category rail on the left, entry list on the right, both driven by listing sources over
// EngineSettings. The entry list is VIRTUALIZED: the Controls page alone is ~50 rows of six cells
// each, which as eagerly-built rows blew past the per-chunk surface band and re-tessellated the whole
// page on every hover. Now only what the viewport can see exists.
//
// Switching category swaps the entry view's path. Nothing around it is rebuilt: the rail, the panel
// and the title are chrome that outlive the selection, and the rows themselves are recycled.
//
// A setting appears in exactly ONE place, and no category shares a name with a section in another one.
// General used to hold both an "Interface" section and a "Dashboard" section while an Interface
// category sat below it in the rail, so there was no way to guess where anything lived.
//
// Every entry here writes a setting a subsystem actually reads. If you cannot point at the consumer,
// it does not go on this screen. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed partial class SettingsScreen : DashboardScreen
{
    private const string KindBinding = "binding";
    private const string KindLocomotion = "locomotion";
    private const string KindNavCategory = "nav";
    private const string KindWebAccess = "webaccess";
    private const string AudioPath = "audio";
    private const string GeneralPath = "general";

    // Web access rows sort after every other General row, all of which sit at Order 0, so the section
    // stays last however often its rows are torn out and re-added.
    private const long WebAccessOrder = 1L << 20;
    // Favourites are rebuilt the same way and sit just above Web Access.
    private const long FavouritesOrder = 1L << 19;

    private const float MetresPerInch = 0.0254f;

    // One poll drives every live row on the screen: the locomotion strip, the pad status, the last
    // input readout and the binding cells. It also catches settings changed from somewhere else (a Home
    // widget, code applying a value). Cheap because every template write is equality-gated, so a tick
    // where nothing moved costs no re-mesh.
    private const float RefreshInterval = 0.1f;

    private Dashboard? _dashboard;
    private readonly SettingsFonts _fonts = new();
    private ListingStyle _style = new ListingStyle();
    private ListingStyle _sidebarStyle = new ListingStyle();
    private readonly ListingItemSource _categories = new();
    private readonly ListingItemSource _entries = new();
    private ListingView? _sidebarView;
    private ListingView? _entryView;
    private RectTransform? _entryViewport;
    private readonly SettingsRowFit _rowFit = new();
    private Text? _title;
    private ListingItem? _activeItem;
    private string _activeCategory = string.Empty;
    private float _refreshAccum;
    private ListingDropdown? _inputDeviceChoice;
    private ListingDropdown? _outputDeviceChoice;
    private ListingDropdown? _streamDeviceChoice;
    private readonly List<ListingItem> _webAccessRows = new();
    private int _webAccessVersion = -1;
    private readonly List<ListingItem> _favouriteRows = new();
    private int _favouritesVersion = -1;
    private ListingSlider? _heightRow;
    private ListingSlider? _tooltipDelayRow;
    private LocaleText _controlsStatus = "Settings.Controls.Hint".AsLocale("Click a binding to rebind it. Escape cancels.");
    private bool _localeHooked;

    // BUILD

    protected override void BuildContent(UIBuilder builder)
    {
        _dashboard = Dashboard.Of(Slot);
        _fonts.Regular = _dashboard?.Font.Target;
        _fonts.Semibold = _dashboard?.FontSemibold.Target;
        _fonts.Bold = _dashboard?.FontBold.Target;

        // RoundedSprite is deliberately left null. Rows paint a procedural RoundedPanel now, so nothing
        // on this screen wants the nine-slice border sprite the old cards were built from.
        _style = new ListingStyle
        {
            Font = _fonts.Body,
            Accent = DashTheme.Accent,
            RowHeight = SettingsMetrics.RowHeight,
            RowSpacing = DashTheme.Gap,
        };
        _sidebarStyle = new ListingStyle
        {
            Font = _fonts.Medium,
            Accent = DashTheme.Accent,
            RowHeight = SettingsMetrics.NavHeight,
            RowSpacing = SettingsMetrics.NavSpacing,
        };

        var root = builder.Current;
        var split = root.AttachComponent<HorizontalLayout>();
        split.Spacing.Value = DashTheme.GapLarge;
        split.PaddingLeft.Value = DashTheme.GapLarge;
        split.PaddingRight.Value = DashTheme.GapLarge;
        split.PaddingTop.Value = DashTheme.GapLarge;
        split.PaddingBottom.Value = DashTheme.GapLarge;
        split.ForceExpandWidth.Value = false;
        split.ForceExpandHeight.Value = true;

        BuildCategories();
        BuildEntries();

        // The rail is bare: no card, no outline. The items are words, and only the current one carries
        // a wash. Its first item lines up with the title beside it.
        var railHost = AddColumn(root, "Sidebar", SettingsMetrics.SidebarWidth);
        // The rail gives up width before the rows do, down to what its longest word needs.
        railHost.GetComponent<LayoutElement>()!.MinWidth.Value = SettingsMetrics.SidebarMinWidth;
        float railTop = (SettingsMetrics.TitleHeight - SettingsMetrics.NavHeight) * 0.5f;
        var railViewport = SettingsUI.Fill(railHost, "Viewport", 0f, DashTheme.Gap, 0f, railTop);
        var railTemplates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.NavHeight };
        railTemplates.MapKind(KindNavCategory, new SettingsNavTemplate(_fonts, PickCategory));
        _sidebarView = ListingView.Attach(railViewport, _sidebarStyle, railTemplates, _categories);
        // The rail is a handful of tabs that never scroll: they switch as the press lands.
        if (_sidebarView.Scroll != null)
            _sidebarView.Scroll.RowsActOnRelease.Value = false;

        // No panel fill of its own. The dash already hands every screen a panel to sit on, and a second
        // one in the same tone inside it is a box drawn around nothing. The rows carry the surface.
        var entryHost = AddColumn(root, "Entries", 0f);
        entryHost.GetComponent<LayoutElement>()!.MinWidth.Value = SettingsMetrics.EntriesMinWidth;
        _title = SettingsUI.Label(entryHost, "Title", _fonts.Strong, DashTheme.FontTitle, DashTheme.Text,
            TextHorizontalAlignment.Left, new float2(0f, 1f), new float2(1f, 1f),
            new float2(DashTheme.Inset, -SettingsMetrics.TitleHeight), new float2(-DashTheme.Inset, 0f));
        var entryViewport = SettingsUI.Fill(entryHost, "Viewport",
            SettingsMetrics.PanelInset, SettingsMetrics.PanelInset,
            SettingsMetrics.PanelInset, SettingsMetrics.TitleHeight);
        _entryViewport = SettingsUI.Rect(entryViewport);
        _entryView = ListingView.Attach(entryViewport, _style, BuildEntryTemplates(), _entries);

        var first = _categories.ItemsAt(string.Empty);
        PickCategory(first.Count > 0 ? first[0] : null);

        LocaleManager.Changed += OnLocaleChanged;
        _localeHooked = true;
    }

    public override void OnDestroy()
    {
        if (_localeHooked)
        {
            LocaleManager.Changed -= OnLocaleChanged;
            _localeHooked = false;
        }
        base.OnDestroy();
    }

    // Every label on this screen is read out of a listing item at bind time, so the only thing a
    // language switch has to do here is ask both views to rebind. Nothing is rebuilt: the rail keeps
    // its selection, the entry list keeps its scroll, and the template writes are equality-gated so the
    // rows whose text did not change do not re-mesh. -xlinka
    private void OnLocaleChanged()
    {
        if (IsDestroyed)
            return;
        BuildDeviceChoices(input: true);
        BuildDeviceChoices(input: false);
        BuildStreamDeviceChoices();
        BuildNetworkChoices();
        _sidebarView?.RefreshValues();
        _entryView?.RefreshValues();
        if (_title != null && _activeItem != null)
            ListingStyle.SetText(_title, _activeItem.Label);
        MarkDirty();
    }

    private ListingTemplateMapper BuildEntryTemplates()
    {
        var labels = new SettingsLabelTemplate(_fonts) { Fit = _rowFit };
        var mapper = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        mapper.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        mapper.Map<ListingToggle>(new SettingsToggleTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingSlider>(new SettingsSliderTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingChoice>(new SettingsChoiceTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingDropdown>(new SettingsDropdownTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingAction>(new SettingsActionTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingTextField>(new SettingsTextFieldTemplate(_fonts) { Fit = _rowFit });
        mapper.Map<ListingLabel>(labels);
        mapper.Fallback(labels);
        mapper.MapKind(KindBinding, new SettingsBindingTemplate(_fonts, this) { Fit = _rowFit });
        mapper.MapKind(KindLocomotion, new SettingsLocomotionTemplate(_fonts, this) { Fit = _rowFit });
        mapper.MapKind(KindWebAccess, new WebAccessRowTemplate(_fonts, this) { Fit = _rowFit });
        mapper.MapKind(KindProse, new SettingsProseTemplate(_fonts) { Fit = _rowFit });
        return mapper;
    }

    private static Slot AddColumn(Slot root, string name, float fixedWidth)
    {
        var column = root.AddSlot(name);
        column.AttachComponent<RectTransform>();
        var element = column.AttachComponent<LayoutElement>();
        if (fixedWidth > 0f)
        {
            element.MinWidth.Value = fixedWidth;
            element.PreferredWidth.Value = fixedWidth;
            element.FlexibleWidth.Value = 0f;
        }
        else
        {
            element.FlexibleWidth.Value = 1f;
        }
        element.FlexibleHeight.Value = 1f;
        return column;
    }

    private void BuildCategories()
    {
        _categories.ClearAll();
        AddCategory("general", "Settings.Category.General".AsLocale("General"));
        AddCategory("audio", "Settings.Category.Audio".AsLocale("Audio"));
        AddCategory("graphics", "Settings.Category.Graphics".AsLocale("Graphics"));
        AddCategory("movement", "Settings.Category.Movement".AsLocale("Movement"));
        AddCategory("controls", "Settings.Category.Controls".AsLocale("Controls"));
        AddCategory("interface", "Settings.Category.Interface".AsLocale("Interface"));
        AddCategory(NetworkPath, "Settings.Category.Network".AsLocale("Network"));
        AddCategory(BackupPath, "Settings.Category.Backup".AsLocale("Backup"));
    }

    private void AddCategory(string key, LocaleText label)
        => _categories.AddRoot(new ListingCustom(key, KindNavCategory, label));

    private void PickCategory(ListingItem? item)
    {
        if (item == null)
            return;
        // Leaving Controls mid-rebind must not strand the listener: while it waits every action set is
        // gated off, so an abandoned capture reads as input having died.
        CancelPendingRebind();
        _activeItem = item;
        _activeCategory = item.Key;
        if (_sidebarView != null)
            _sidebarView.SelectedKey = item.Key;
        // Swaps the listing's items only. The rail, the panel and the title are chrome and stay put.
        _entryView?.SetPath(item.Key);
        if (_title != null)
            ListingStyle.SetText(_title, item.Label);
        if (item.Key == NetworkPath)
            MeasureNodes(force: false);
        MarkDirty();
    }

    // ENTRIES
    //
    // Everything below is wired to a real consumer. Where a value is only meaningful in one mode
    // (snap angle vs smooth speed), the other row goes non-interactive rather than disappearing, so
    // the page does not reflow under your hand.

    private void BuildEntries()
    {
        _entries.ClearAll();
        BuildGeneral();
        BuildAudio();
        BuildGraphics();
        BuildMovement();
        BuildControls();
        BuildInterface();
        BuildNetwork();
        BuildBackup();
    }

    private void BuildGeneral()
    {
        const string path = "general";
        Header(path, "You", "Settings.General.Section.You".AsLocale("You"));
        // Drives the avatar auto-rescale: SettingsApplier feeds it to InputInterface.UserHeight and
        // AvatarIK re-scales so the eyes land at this height. Metres whatever the units say: imperial
        // changes the label and walks the bar an inch at a time, never the value.
        _heightRow = Slider(path, "height", "Settings.General.Height".AsLocale("Height"), SettingsCatalog.UserHeight,
            () => EngineSettings.UserHeight, v => EngineSettings.UserHeight = v, DescribeHeight);
        ApplyHeightStep();
        // Lifts a seated player to the eye height the row above gives. Turning it on measures the headset,
        // which a screen cannot do, so there it stays off.
        Toggle(path, "seatedmode", "Settings.General.SeatedMode".AsLocale("Seated Mode"),
            () => EngineSettings.SeatedMode, v => Lumora.Core.Input.SeatedMode.SetEnabled(v),
            "Settings.General.SeatedMode.Hint".AsLocale("Sit in VR, stand at full height"));

        Header(path, "Video", "Settings.General.Section.Video".AsLocale("Video"));
        // The cap a link is resolved at when the player itself asks for nothing in particular. Decode
        // cost is pixels, and a panel on a wall gains nothing from 4k.
        Slider(path, "videoheight", "Settings.General.VideoQuality".AsLocale("Video Quality"), SettingsCatalog.VideoMaxHeight,
            () => EngineSettings.VideoMaxHeight, v => EngineSettings.VideoMaxHeight = (int)v, v => $"{(int)v}p");
        // Nothing is read out of a browser until one is picked here. It buys age-gated and member-only
        // links and costs handing the resolver a logged-in session, which is a decision to make on
        // purpose rather than a default.
        _entries.Add(path, ListingChoice.FromEnum<CookiesBrowser>("videocookies",
            "Settings.General.VideoCookies".AsLocale("Video Cookies"),
            () => EngineSettings.VideoCookiesBrowser, v => EngineSettings.VideoCookiesBrowser = v));

        Header(path, "Storage", "Settings.General.Section.Storage".AsLocale("Storage"));
        // Disk for what came from elsewhere: downloads, and what peers or the cloud sent. Past it the file
        // wanted longest ago goes, and comes back when something needs it again.
        Slider(path, "assetcache", "Settings.General.AssetCache".AsLocale("Asset Cache"), SettingsCatalog.AssetCacheGigabytes,
            () => EngineSettings.AssetCacheGigabytes, v => EngineSettings.AssetCacheGigabytes = (int)v, v => $"{(int)v} GB");

        _entries.Add(path, new ListingHeader(path + ".h.Favourites", "Settings.General.Section.Favourites".AsLocale("Favourites"))
        {
            Order = FavouritesOrder,
        });
        BuildFavouriteRows();

        _entries.Add(path, new ListingHeader(path + ".h.WebAccess", "Settings.General.Section.WebAccess".AsLocale("Web Access"))
        {
            Order = WebAccessOrder,
        });
        BuildWebAccessRows();
    }

    private void BuildFavouriteRows()
    {
        _favouritesVersion = Favourites.Version;
        FavouriteSettingsRows.Build(_entries, GeneralPath, _favouriteRows, FavouritesOrder);
    }

    // WEB ACCESS
    //
    // Every site this machine's user has allowed or denied for scripts, flippable in place, with Remove to
    // forget one so the next request asks again. The decisions live in WebAccess, not EngineSettings, and
    // a prompt can write one from any thread while this screen is up, so the rows are rebuilt whenever its
    // version counter moves, checked on the refresh tick. -xlinka
    private void BuildWebAccessRows()
    {
        for (int i = 0; i < _webAccessRows.Count; i++)
            _entries.Remove(GeneralPath, _webAccessRows[i].Key);
        _webAccessRows.Clear();

        var access = WebAccess.Shared;
        _webAccessVersion = access.Version;
        var hosts = access.Entries();
        long order = WebAccessOrder;
        for (int i = 0; i < hosts.Count; i++)
        {
            _webAccessRows.Add(_entries.Add(GeneralPath,
                new ListingCustom("webaccess." + hosts[i].Host, KindWebAccess, hosts[i].Host)
                {
                    Tag = hosts[i].Host,
                    Order = ++order,
                }));
        }

        // Nothing decided yet. One dead row rather than a header with a hole under it.
        if (_webAccessRows.Count == 0)
        {
            _webAccessRows.Add(_entries.Add(GeneralPath, new ListingLabel("webaccess.none",
                "Settings.General.WebAccessNone".AsLocale("No sites yet"))
            {
                DetailText = "Settings.General.WebAccessNone.Hint".AsLocale("Scripts ask before they reach a new site"),
                Interactable = false,
                Order = ++order,
            }));
        }
    }

    internal void SetWebAccess(string host, bool allowed)
    {
        WebAccess.Shared.Set(host, allowed);
        _entryView?.RefreshValues();
    }

    internal void ForgetWebAccess(string host)
    {
        WebAccess.Shared.Remove(host);
        BuildWebAccessRows();
        _entryView?.RefreshValues();
        MarkDirty();
    }

    // Its own page: what you hear, what the microphone does before anyone hears it, and how your
    // voice carries.
    private void BuildAudio()
    {
        const string path = AudioPath;
        Header(path, "Volume", "Settings.Audio.Section.Volume".AsLocale("Volume"));
        // Master is the gain on the device the mixer renders into. The four below are group gains an
        // AudioOutput folds in by what the sound is for, so a loud video does not have to be paid for by
        // turning everything else down too.
        Slider(path, "volume", "Settings.Audio.MasterVolume".AsLocale("Master Volume"), SettingsCatalog.MasterVolume,
            () => EngineSettings.MasterVolume, v => EngineSettings.MasterVolume = v, v => $"{v * 100f:0}%");
        Slider(path, "sfxvolume", "Settings.Audio.SoundEffectVolume".AsLocale("Sound Effects"), SettingsCatalog.SoundEffectVolume,
            () => EngineSettings.SoundEffectVolume, v => EngineSettings.SoundEffectVolume = v, v => $"{v * 100f:0}%");
        Slider(path, "mediavolume", "Settings.Audio.MultimediaVolume".AsLocale("Multimedia"), SettingsCatalog.MultimediaVolume,
            () => EngineSettings.MultimediaVolume, v => EngineSettings.MultimediaVolume = v, v => $"{v * 100f:0}%");
        Slider(path, "voicevolume", "Settings.Audio.VoiceVolume".AsLocale("Voice"), SettingsCatalog.VoiceVolume,
            () => EngineSettings.VoiceVolume, v => EngineSettings.VoiceVolume = v, v => $"{v * 100f:0}%");
        Slider(path, "uivolume", "Settings.Audio.UIVolume".AsLocale("Interface"), SettingsCatalog.UIVolume,
            () => EngineSettings.UIVolume, v => EngineSettings.UIVolume = v, v => $"{v * 100f:0}%");

        // A device choice includes the system default, so selecting hardware is one action.
        if (HasDesktopWindow)
        {
            Header(path, "Devices", "Settings.Audio.Section.Devices".AsLocale("Devices"));
            BuildDeviceChoices(input: true);
            BuildDeviceChoices(input: false);
            // Devices are enumerated once at boot, so anything plugged in since is not on either list until
            // this runs. It re-applies the selection too: a device that came back gets picked up again.
            _entries.Add(path, new ListingAction("reloaddevices",
                "Settings.Audio.ReloadDevices".AsLocale("Look again for audio devices"))
            {
                Invoke = ReloadAudioDevices,
                ButtonLabel = "Settings.Audio.ReloadDevicesButton".AsLocale("Reload"),
            });
        }

        Header(path, "Input", "Settings.Audio.Section.Input".AsLocale("Microphone"));
        // Microphone. Local to this machine: muting here stops anything being captured at all, which is
        // a different thing from a host silencing somebody in a session.
        Toggle(path, "micmute", "Settings.Audio.MicrophoneMuted".AsLocale("Mute Microphone"),
            () => EngineSettings.MicrophoneMuted, v => EngineSettings.MicrophoneMuted = v,
            "Settings.Audio.MicrophoneMuted.Hint".AsLocale("Nothing is captured or sent while this is on"));
        // Ahead of everything else in the chain, so with normalizing on it only matters to a microphone
        // too quiet or too hot for the leveller's own range.
        Slider(path, "micgain", "Settings.Audio.MicrophoneGain".AsLocale("Microphone Gain"),
            SettingsCatalog.MicrophoneGain,
            () => EngineSettings.MicrophoneGain, v => EngineSettings.MicrophoneGain = v,
            v => $"{v:+0.0;-0.0;0} dB");
        Toggle(path, "voicenormalize", "Settings.Audio.VoiceNormalization".AsLocale("Normalize Voice"),
            () => EngineSettings.VoiceNormalization, v => EngineSettings.VoiceNormalization = v,
            "Settings.Audio.VoiceNormalization.Hint".AsLocale("Evens out a quiet or a loud microphone"));
        Slider(path, "voicetargetlevel", "Settings.Audio.VoiceTargetLevel".AsLocale("Voice Level"),
            SettingsCatalog.VoiceTargetLevel,
            () => EngineSettings.VoiceTargetLevel, v => EngineSettings.VoiceTargetLevel = v,
            v => $"{v:0.0} dB");
        // The gate cuts the room between words. It does not clean up noise carried over speech: that
        // would be a denoiser and there is none here.
        // Ahead of the gate: takes the room out per band instead of switching the whole frame off.
        Toggle(path, "noisesuppression", "Settings.Audio.NoiseSuppression".AsLocale("Noise Suppression"),
            () => EngineSettings.NoiseSuppression, v => EngineSettings.NoiseSuppression = v,
            "Settings.Audio.NoiseSuppression.Hint".AsLocale("Removes background noise like fans, typing and hum"));
        Slider(path, "gatethreshold", "Settings.Audio.NoiseGateThreshold".AsLocale("Noise Gate"),
            SettingsCatalog.NoiseGateThreshold,
            () => EngineSettings.NoiseGateThreshold, v => EngineSettings.NoiseGateThreshold = v,
            v => v <= 0f ? "Off" : $"{v * 100f:0.0}%");
        Slider(path, "gateattack", "Settings.Audio.NoiseGateAttack".AsLocale("Gate Attack"),
            SettingsCatalog.NoiseGateAttack,
            () => EngineSettings.NoiseGateAttack, v => EngineSettings.NoiseGateAttack = v,
            v => $"{v * 1000f:0} ms");
        Slider(path, "gatehold", "Settings.Audio.NoiseGateHold".AsLocale("Gate Hold"),
            SettingsCatalog.NoiseGateHold,
            () => EngineSettings.NoiseGateHold, v => EngineSettings.NoiseGateHold = v,
            v => $"{v * 1000f:0} ms");
        Slider(path, "gaterelease", "Settings.Audio.NoiseGateRelease".AsLocale("Gate Release"),
            SettingsCatalog.NoiseGateRelease,
            () => EngineSettings.NoiseGateRelease, v => EngineSettings.NoiseGateRelease = v,
            v => $"{v * 1000f:0} ms");
        Header(path, "Voice", "Settings.Audio.Section.Voice".AsLocale("Voice"));
        // A whisper is not a separate channel, it is the same voice heard quieter by everyone outside
        // the close range. This is the gain those listeners get, so 0 makes a whisper genuinely private.
        Slider(path, "whisperattenuation", "Settings.Audio.WhisperAttenuation".AsLocale("Whisper Attenuation"),
            SettingsCatalog.WhisperAttenuationVolume,
            () => EngineSettings.WhisperAttenuationVolume, v => EngineSettings.WhisperAttenuationVolume = v,
            v => $"{v * 100f:0}%");
        // What an audio stream you START is encoded at. Costs upstream bandwidth per listener, which is
        // why it is a choice and not a constant.
        Slider(path, "streambitrate", "Settings.Audio.StreamBitrate".AsLocale("Stream Bitrate"),
            SettingsCatalog.DefaultStreamBitrateKbps,
            () => EngineSettings.DefaultStreamBitrateKbps, v => EngineSettings.DefaultStreamBitrateKbps = (int)v,
            v => $"{v:0} kbps");

        // Where the camera sends the stream's sound. The audio system only opens an output by id on
        // Windows, so nowhere else gets the section.
        if (HasStreamDevices)
        {
            Header(path, "Stream", "Settings.Audio.Section.Stream".AsLocale("Stream"));
            BuildStreamDeviceChoices();
        }
    }

    // AUDIO DEVICES
    //
    // Device IDs stay separate from display labels, including the empty ID for the system default.

    // Is there anything in the list that carries what the machine is playing?
    private static bool HasLoopbackDevice(IReadOnlyList<string> devices)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            if (Lumora.Core.Audio.AudioInputTypes.IsLoopback(Lumora.Core.Audio.AudioInputTypes.Classify(devices[i])))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<string> AudioDevices(bool input)
    {
        var audio = Engine.Current?.AudioSystem;
        if (audio == null)
            return Array.Empty<string>();
        return input ? audio.InputDeviceNames : audio.OutputDeviceNames;
    }

    private static string SavedDevice(bool input)
        => input ? EngineSettings.AudioInputDevice : EngineSettings.AudioOutputDevice;

    private static void SaveDevice(bool input, string name)
    {
        if (input)
            EngineSettings.AudioInputDevice = name;
        else
            EngineSettings.AudioOutputDevice = name;
    }

    private static int IndexIn(IReadOnlyList<string> devices, string name)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    // Update an existing item in place: reloads keep the page order and scroll position.
    private void BuildDeviceChoices(bool input)
    {
        if (!HasDesktopWindow)
            return;

        var devices = AudioDevices(input);
        var ordered = new List<string>(devices);
        if (input)
        {
            ordered.Sort((a, b) =>
            {
                int byType = Lumora.Core.Audio.AudioInputTypes.CaptureOrder(Lumora.Core.Audio.AudioInputTypes.Classify(a))
                    .CompareTo(Lumora.Core.Audio.AudioInputTypes.CaptureOrder(Lumora.Core.Audio.AudioInputTypes.Classify(b)));
                return byType != 0 ? byType : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
        }

        var names = new List<string> { "Settings.Audio.SystemDefault".AsLocale("System default").Resolve() };
        var ids = new List<string> { string.Empty };
        var details = new List<string>
        {
            (devices.Count == 0 ? "Settings.Audio.NoDevices".AsLocale("No devices found")
                : "Settings.Audio.UseSystemInput.Hint".AsLocale("Follows whatever the machine is set to")).Resolve(),
        };
        foreach (string name in ordered)
        {
            ids.Add(name);
            names.Add(name);
            details.Add(input ? Lumora.Core.Audio.AudioInputTypes.Describe(Lumora.Core.Audio.AudioInputTypes.Classify(name)) : string.Empty);
        }
        string saved = SavedDevice(input);
        if (saved.Length > 0 && IndexIn(ids, saved) < 0)
        {
            ids.Add(saved);
            names.Add("Settings.Audio.DeviceUnplugged".AsLocale("{0} (not here)", saved).Resolve());
            details.Add(string.Empty);
        }

        var choice = input ? _inputDeviceChoice : _outputDeviceChoice;
        if (choice == null)
        {
            choice = _entries.Add(AudioPath, new ListingDropdown(input ? "inputdevice" : "outputdevice",
                input ? "Settings.Audio.Section.InputDevice".AsLocale("Input Device")
                    : "Settings.Audio.Section.OutputDevice".AsLocale("Output Device"))
            {
                Options = names,
                Read = () => IndexIn(ids, SavedDevice(input)),
                Write = _ => { },
            });
            if (input) _inputDeviceChoice = choice;
            else _outputDeviceChoice = choice;
        }
        choice.Options = names;
        choice.OptionDetails = details;
        choice.Read = () => IndexIn(ids, SavedDevice(input));
        choice.Write = index =>
        {
            if (index < 0 || index >= ids.Count)
                return;
            SaveDevice(input, ids[index]);
            BuildDeviceChoices(input);
            _entryView?.RefreshValues();
        };
        choice.ListHint = input && OperatingSystem.IsWindows() && !HasLoopbackDevice(devices)
            ? "Settings.Audio.NoLoopback.Hint".AsLocale(
                "To stream what this machine plays, turn on Stereo Mix in Windows sound settings, or install a virtual audio cable")
            : default;
    }

    private void ReloadAudioDevices()
    {
        Engine.Current?.AudioSystem?.RefreshDevices();
        BuildDeviceChoices(input: true);
        BuildDeviceChoices(input: false);
        BuildStreamDeviceChoices();
        _entryView?.RefreshValues();
        MarkDirty();
    }

    private static bool HasStreamDevices => HasDesktopWindow && OperatingSystem.IsWindows();

    private void BuildStreamDeviceChoices()
    {
        if (!HasStreamDevices)
            return;

        var outputs = Lumora.Audio.Wasapi.WasapiEndpoints.EnumerateOutputs();
        var ids = new List<string> { string.Empty };
        var names = new List<string> { "Settings.Audio.StreamNone".AsLocale("No Stream Device").Resolve() };
        foreach (var output in outputs)
        {
            ids.Add(output.Id);
            names.Add(output.Name);
        }
        string saved = EngineSettings.StreamOutputDevice;
        if (saved.Length > 0 && IndexIn(ids, saved) < 0)
        {
            ids.Add(saved);
            names.Add("Settings.Audio.StreamDeviceUnplugged".AsLocale("Saved device (not here)").Resolve());
        }
        _streamDeviceChoice ??= _entries.Add(AudioPath, new ListingDropdown("streamdevice",
            "Settings.Audio.StreamDevice".AsLocale("Stream Device"))
        {
            Options = names,
            Read = () => IndexIn(ids, EngineSettings.StreamOutputDevice),
            Write = _ => { },
            ListHint = "Settings.Audio.StreamNone.Hint".AsLocale("A virtual audio cable lets a streaming program pick the stream's sound up"),
        });
        _streamDeviceChoice.Options = names;
        _streamDeviceChoice.Read = () => IndexIn(ids, EngineSettings.StreamOutputDevice);
        _streamDeviceChoice.Write = index =>
        {
            if (index < 0 || index >= ids.Count)
                return;
            EngineSettings.StreamOutputDevice = ids[index];
            BuildStreamDeviceChoices();
            _entryView?.RefreshValues();
        };
    }

    // Desktop controls are absent on mobile; handhelds still draw their own screen.
    private static bool HasDesktopWindow => !Engine.IsMobilePlatform;
    private static bool DrawsOwnScreen => !Engine.IsStandaloneHeadset;

    private void BuildGraphics()
    {
        const string path = "graphics";
        if (DrawsOwnScreen)
            Header(path, "Display", "Settings.Graphics.Section.Display".AsLocale("Display"));
        if (HasDesktopWindow)
        {
            Toggle(path, "vsync", "Settings.Graphics.VSync".AsLocale("VSync"), () => EngineSettings.VSync, v => EngineSettings.VSync = v);
            Toggle(path, "fullscreen", "Settings.Graphics.Fullscreen".AsLocale("Fullscreen"), () => EngineSettings.Fullscreen, v => EngineSettings.Fullscreen = v);
        }
        if (DrawsOwnScreen)
        {
            // Vertical degrees, the way the camera takes it.
            Slider(path, "fov", "Settings.Graphics.FieldOfView".AsLocale("Field of View"), SettingsCatalog.FieldOfView,
                () => EngineSettings.FieldOfView, v => EngineSettings.FieldOfView = v, v => $"{v:0}°");
            Slider(path, "fps", "Settings.Graphics.FpsLimit".AsLocale("FPS Limit"), SettingsCatalog.MaxFps,
                () => EngineSettings.MaxFps, v => EngineSettings.MaxFps = (int)v,
                v => v <= 0f ? "Off" : $"{(int)v}");
        }
        if (HasDesktopWindow)
        {
            // Caps the loop while the window is unfocused or minimized (vsync stops throttling there).
            Slider(path, "bgfps", "Settings.Graphics.BackgroundFps".AsLocale("Background FPS"), SettingsCatalog.BackgroundFps,
                () => EngineSettings.BackgroundFps, v => EngineSettings.BackgroundFps = (int)v,
                v => v <= 0f ? "Off" : $"{(int)v}");
            // Windowed this resizes the window; fullscreen it drops the 3D buffer to that height and leaves
            // the interface at native. A picker, not a slider: nobody drags for a resolution.
            _entries.Add(path, new ListingDropdown("resolution", "Settings.Graphics.Resolution".AsLocale("Resolution"))
            {
                Options = ResolutionOptionLabels(),
                Read = () => ResolutionIndex(EngineSettings.ResolutionHeight),
                Write = index =>
                {
                    var options = EngineSettings.ResolutionHeightOptions;
                    if (index >= 0 && index < options.Length)
                        EngineSettings.ResolutionHeight = options[index];
                },
            });
        }

        if (DrawsOwnScreen)
        {
            // 5% steps so the viewport is not re-allocated per pixel of drag. With dynamic resolution on
            // this is the CEILING rather than the fixed value.
            Slider(path, "renderscale", "Settings.Graphics.RenderScale".AsLocale("Render Scale"), SettingsCatalog.RenderScale,
                () => EngineSettings.RenderScale, v => EngineSettings.RenderScale = v, v => $"{v * 100f:0}%");
            Toggle(path, "dynres", "Settings.Graphics.DynamicResolution".AsLocale("Dynamic Resolution"),
                () => EngineSettings.DynamicResolution, v => EngineSettings.DynamicResolution = v);
        }
        if (HasDesktopWindow)
        {
            // Desktop only, and only does anything below 100% scale. Not offered in a headset: see
            // EngineSettings.Upscaler for why.
            _entries.Add(path, new ListingChoice("upscaler", "Settings.Graphics.Upscaler".AsLocale("Upscaler"))
            {
                Options = new[] { "Bilinear", "FSR 1", "FSR 2" },
                Read = () => EngineSettings.Upscaler,
                Write = index => EngineSettings.Upscaler = index,
            });
        }

        Header(path, "Image", "Settings.Graphics.Section.Image".AsLocale("Image"));
        if (HasDesktopWindow)
        {
            Slider(path, "bloom", "Settings.Graphics.Bloom".AsLocale("Bloom"), SettingsCatalog.Bloom,
                () => EngineSettings.Bloom, v => EngineSettings.Bloom = v,
                v => v <= 0.001f ? "Off" : $"{v:0.##}");
            // Contact shadow in corners and under bodies. One number, shown as a percentage. Not offered by
            // the standalone renderer.
            Slider(path, "ao", "Settings.Graphics.AmbientOcclusion".AsLocale("Ambient Occlusion"), SettingsCatalog.AmbientOcclusion,
                () => EngineSettings.AmbientOcclusion, v => EngineSettings.AmbientOcclusion = v,
                v => v <= 0.001f ? "Off" : $"{v * 100f:0}%");
            // Diffuse bounce between nearby surfaces, the occlusion's partner pass. Costs about what the
            // occlusion costs; off by default.
            Toggle(path, "ssil", "Settings.Graphics.IndirectLighting".AsLocale("Indirect Lighting"),
                () => EngineSettings.ScreenSpaceIndirectLighting, v => EngineSettings.ScreenSpaceIndirectLighting = v);
            // A ray march per pixel; only glossy floors and water show it. Off by default.
            Toggle(path, "ssr", "Settings.Graphics.ScreenSpaceReflections".AsLocale("Screen Space Reflections"),
                () => EngineSettings.ScreenSpaceReflections, v => EngineSettings.ScreenSpaceReflections = v);
        }
        if (DrawsOwnScreen)
        {
            _entries.Add(path, new ListingChoice("aa", "Settings.Graphics.AntiAliasing".AsLocale("Anti-Aliasing"))
            {
                Options = Array.ConvertAll(EngineSettings.AntiAliasingOptions, EngineSettings.DescribeAntiAliasing),
                Read = () => AntiAliasingIndex(EngineSettings.AntiAliasing),
                Write = index =>
                {
                    var options = EngineSettings.AntiAliasingOptions;
                    if (index >= 0 && index < options.Length)
                        EngineSettings.AntiAliasing = options[index];
                },
            });
        }

        Header(path, "Quality", "Settings.Graphics.Section.Quality".AsLocale("Quality"));
        if (HasDesktopWindow)
        {
            // Never set by anything until now, so every world ran on the renderer default. One of the
            // largest single levers on shadow cost. Hard also draws point light shadows in two passes
            // instead of six.
            _entries.Add(path, new ListingChoice("shadowquality", "Settings.Graphics.ShadowQuality".AsLocale("Shadow Quality"))
            {
                Options = EngineSettings.ShadowQualityOptions,
                Read = () => EngineSettings.ShadowQuality,
                Write = index => EngineSettings.ShadowQuality = index,
            });
        }
        // Driven as an index over the generated buckets, not pixels: a drag can only land on a size
        // that actually has a variant behind it. Providers re-resolve live onto the new cap.
        Slider(path, "texturesize", "Settings.Graphics.MaxTextureSize".AsLocale("Max Texture Size"), 0f, EngineSettings.TextureSizeOptions.Length - 1, 1f,
            () => TextureSizeIndex(EngineSettings.MaxTextureSize),
            v =>
            {
                int index = System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.TextureSizeOptions.Length - 1);
                EngineSettings.MaxTextureSize = EngineSettings.TextureSizeOptions[index];
            },
            v => EngineSettings.DescribeTextureSize(
                EngineSettings.TextureSizeOptions[System.Math.Clamp((int)MathF.Round(v), 0, EngineSettings.TextureSizeOptions.Length - 1)]));
        // Off makes every probe stop rendering, which removes its cost rather than dimming it: a probe
        // in Always mode is six scene renders per bake.
        Toggle(path, "reflections", "Settings.Graphics.Reflections".AsLocale("Reflections"),
            () => EngineSettings.ReflectionsEnabled, v => EngineSettings.ReflectionsEnabled = v);
        // Multiplier on every LOD switch distance. Above 1 holds detailed levels further out.
        Slider(path, "lodbias", "Settings.Graphics.LodBias".AsLocale("LOD Bias"), SettingsCatalog.LodBias,
            () => EngineSettings.LodBias, v => EngineSettings.LodBias = v, v => $"{v:0.00}x");
        // Screen-space error an imported mesh is allowed to show before the renderer drops it to a
        // cheaper level of itself. This never removes an object, only triangles.
        Slider(path, "meshlod", "Settings.Graphics.MeshDetail".AsLocale("Mesh Detail"), SettingsCatalog.MeshLodThreshold,
            () => EngineSettings.MeshLodThreshold, v => EngineSettings.MeshLodThreshold = v,
            EngineSettings.DescribeMeshLodThreshold);
        // Multiplier on every directional light's cascade range and on how far away a lamp keeps its
        // shadow - the cheapest real cut on the shadow pass, and it works on worlds whose lights someone
        // else authored.
        Slider(path, "shadowdistance", "Settings.Graphics.ShadowDistance".AsLocale("Shadow Distance"), SettingsCatalog.ShadowDistanceScale,
            () => EngineSettings.ShadowDistanceScale, v => EngineSettings.ShadowDistanceScale = v, v => $"{v:0.00}x");
        // Other people's avatars, from where you stand. Yours is never touched by any of the three.
        Toggle(path, "reducedistantavatars", "Settings.Graphics.ReduceDistantAvatars".AsLocale("Ease Off Distant Avatars"),
            () => EngineSettings.ReduceDistantAvatars, v => EngineSettings.ReduceDistantAvatars = v,
            "Settings.Graphics.ReduceDistantAvatars.Hint".AsLocale("Avatars behind you or far away animate and swing less often"));
        Slider(path, "avatarshadowdistance", "Settings.Graphics.AvatarShadowDistance".AsLocale("Avatar Shadow Distance"), SettingsCatalog.AvatarShadowDistance,
            () => EngineSettings.AvatarShadowDistance, v => EngineSettings.AvatarShadowDistance = v, v => v <= 0f ? "Always" : $"{v:0} m");
        Slider(path, "avatardrawdistance", "Settings.Graphics.AvatarDrawDistance".AsLocale("Avatar Draw Distance"), SettingsCatalog.AvatarDrawDistance,
            () => EngineSettings.AvatarDrawDistance, v => EngineSettings.AvatarDrawDistance = v, v => v <= 0f ? "Always" : $"{v:0} m");
        // Past this, or further down the list than the nearest few, an avatar is a picture of itself.
        Slider(path, "avatarstandindistance", "Settings.Graphics.AvatarStandInDistance".AsLocale("Avatar Picture Distance"), SettingsCatalog.AvatarStandInDistance,
            () => EngineSettings.AvatarStandInDistance, v => EngineSettings.AvatarStandInDistance = v, v => v <= 0f ? "Never" : $"{v:0} m");
        Slider(path, "avatarfulldetail", "Settings.Graphics.AvatarFullDetailCount".AsLocale("Full Avatars Nearby"), SettingsCatalog.AvatarFullDetailCount,
            () => EngineSettings.AvatarFullDetailCount, v => EngineSettings.AvatarFullDetailCount = (int)MathF.Round(v), v => v <= 0f ? "Everyone" : $"{v:0}");
        _entries.Add(path, new ListingChoice("othersavatarshaders", "Settings.Graphics.OthersAvatarShaders".AsLocale("Other Avatars' Shaders"))
        {
            Options = EngineSettings.OthersAvatarShaderOptions,
            Read = () => EngineSettings.OthersAvatarShaders,
            Write = index => EngineSettings.OthersAvatarShaders = index,
            DetailText = "Settings.Graphics.OthersAvatarShaders.Hint".AsLocale("Simple draws other people's toon-shaded avatars with plain colours and textures, many times cheaper up close"),
        });
        if (DrawsOwnScreen)
        {
            // Point and spot lights that cast at once, nearest and brightest first; the rest light
            // without a shadow. A world with no more than this many looks exactly as it was built.
            Slider(path, "shadowedlights", "Settings.Graphics.ShadowedLights".AsLocale("Shadowed Lights"), SettingsCatalog.ShadowedLights,
                () => EngineSettings.ShadowedLights, v => EngineSettings.ShadowedLights = (int)MathF.Round(v),
                v => v <= 0f ? "Off" : $"{(int)MathF.Round(v)}");
            Slider(path, "shadowedlightsvr", "Settings.Graphics.ShadowedLightsVR".AsLocale("Shadowed Lights (VR)"), SettingsCatalog.ShadowedLights,
                () => EngineSettings.ShadowedLightsVR, v => EngineSettings.ShadowedLightsVR = (int)MathF.Round(v),
                v => v <= 0f ? "Off" : $"{(int)MathF.Round(v)}");
            // How often a spinning or sliding thing's shadow catches up. Every frame keeps every lamp
            // around it re-rendering; a few times a second lets them cache in between.
            Slider(path, "movingshadows", "Settings.Graphics.MovingShadows".AsLocale("Moving Shadows"), SettingsCatalog.MovingShadowRate,
                () => EngineSettings.MovingShadowRate, v => EngineSettings.MovingShadowRate = (int)MathF.Round(v),
                v => v <= 0f ? "Every frame" : $"{(int)MathF.Round(v)} per second");
        }
    }

    private void BuildMovement()
    {
        const string path = "movement";
        // A headset that draws no screen of its own has no mouse, but it still points, so the section and
        // the laser row are there everywhere.
        Header(path, "Look", "Settings.Movement.Section.Look".AsLocale("Look"));
        if (DrawsOwnScreen)
        {
            Slider(path, "mousesens", "Settings.Movement.MouseSensitivity".AsLocale("Mouse Sensitivity"), SettingsCatalog.MouseSensitivity,
                () => EngineSettings.MouseSensitivity, v => EngineSettings.MouseSensitivity = v, v => $"{v:0.00}x");
            Slider(path, "mousesmooth", "Settings.Movement.MouseSmoothing".AsLocale("Mouse Smoothing"), SettingsCatalog.MouseSmoothing,
                () => EngineSettings.MouseSmoothing, v => EngineSettings.MouseSmoothing = v,
                v => v <= 0.001f ? "Off" : $"{v:0.00}");
        }
        // Read by every VR laser as it casts. Off is the raw controller ray.
        Slider(path, "lasersmooth", "Settings.Movement.LaserSmoothing".AsLocale("Laser Smoothing"), SettingsCatalog.LaserSmoothing,
            () => EngineSettings.LaserSmoothing, v => EngineSettings.LaserSmoothing = v,
            v => v <= 0.001f ? "Off" : $"{v:0.00}");

        Header(path, "Locomotion", "Settings.Movement.Section.Locomotion".AsLocale("Locomotion"));
        _entries.Add(path, new ListingCustom("locomotion", KindLocomotion, "Settings.Movement.Mode".AsLocale("Mode")));
        // Read live by the walk and noclip modules. A desktop has one direction, the view's, and ignores it.
        var direction = ListingChoice.FromEnum<MovementDirection>("movedirection", "Settings.Movement.Direction".AsLocale("Direction"),
            () => EngineSettings.MovementDirection, v => EngineSettings.MovementDirection = v);
        direction.DetailText = "Settings.Movement.Direction.Hint".AsLocale("In VR, move where you look or where the hand on the move stick points");
        _entries.Add(path, direction);
        Slider(path, "noclip", "Settings.Movement.NoclipSpeed".AsLocale("Noclip Speed"), SettingsCatalog.NoclipSpeed,
            () => EngineSettings.NoclipSpeed, v => EngineSettings.NoclipSpeed = v, v => $"{v:0.#} m/s");

        Header(path, "Turning", "Settings.Movement.Section.Turning".AsLocale("Turning"));
        _entries.Add(path, ListingChoice.FromEnum<EngineSettings.TurnStyle>("turnmode", "Settings.Movement.TurnStyle".AsLocale("Turn Style"),
            () => EngineSettings.TurnMode, v => EngineSettings.TurnMode = v));
        // Only one of these two does anything at a time; the idle one greys out instead of vanishing so
        // the list does not reflow while you are reading it.
        var snap = Slider(path, "snapangle", "Settings.Movement.SnapAngle".AsLocale("Snap Angle"), SettingsCatalog.SnapTurnAngle,
            () => EngineSettings.SnapTurnAngle, v => EngineSettings.SnapTurnAngle = v, v => $"{v:0}°");
        snap.Tag = EngineSettings.TurnStyle.Snap;
        var smooth = Slider(path, "smoothspeed", "Settings.Movement.SmoothTurnSpeed".AsLocale("Smooth Turn Speed"), SettingsCatalog.SmoothTurnSpeed,
            () => EngineSettings.SmoothTurnSpeed, v => EngineSettings.SmoothTurnSpeed = v, v => $"{v:0}°/s");
        smooth.Tag = EngineSettings.TurnStyle.Smooth;
    }

    // Everything that shapes what you look at, in one place: the language the interface speaks, the
    // desktop cursor, and the dashboard's own placement. Language and the dashboard toggles used to sit
    // under General, which left this category holding a single reticle group and no reason to exist.
    private void BuildInterface()
    {
        const string path = "interface";
        // How the interface reads, its language and its units, leads the page. Nothing else groups with
        // the two, and a section header over them would only repeat the names.
        _entries.Add(path, BuildLanguageChoice());
        // The Height label under General reads it; the value itself stays in metres.
        Toggle(path, "imperialunits", "Settings.Interface.ImperialUnits".AsLocale("Imperial Units"),
            () => EngineSettings.ImperialUnits, v => EngineSettings.ImperialUnits = v,
            "Settings.Interface.ImperialUnits.Hint".AsLocale("Feet and inches instead of metres"));

        Header(path, "Editing", "Settings.Interface.Section.Editing".AsLocale("Editing"));
        _entries.Add(path, new ListingChoice("gizmocontrols", "Settings.Interface.GizmoControls".AsLocale("Gizmo controls"))
        {
            Options = new[] { "Context Menu", "Cubes", "All Gizmo" },
            Read = () => (int)EngineSettings.GizmoControls,
            Write = index => EngineSettings.GizmoControls = (EngineSettings.GizmoControlStyle)index,
            DetailText = "Settings.Interface.GizmoControls.Hint".AsLocale(
                "Choose modes in the menu, use floating cubes, or grab move, rotate and scale handles together"),
        });

        Header(path, "Photos", "Settings.Interface.Section.Photos".AsLocale("Photos"));
        Toggle(path, "handframe", "Settings.Photos.HandFrame".AsLocale("Hand Viewfinder"),
            () => EngineSettings.HandFrame, v => EngineSettings.HandFrame = v,
            "Settings.Photos.HandFrame.Hint".AsLocale("An L with the left hand, index up, opens a viewfinder; the right trigger takes the photo"));
        _entries.Add(path, new ListingChoice("photosize", "Settings.Photos.Size".AsLocale("Photo Size"))
        {
            Options = new[] { "1920 x 1080", "2560 x 1440", "3840 x 2160" },
            Read = () => EngineSettings.PhotoSize,
            Write = index => EngineSettings.PhotoSize = index,
        });
        _entries.Add(path, new ListingChoice("photoformat", "Settings.Photos.Format".AsLocale("Photo Format"))
        {
            Options = new[] { "WebP", "PNG", "JPG" },
            Read = () => (int)EngineSettings.PhotoEncoding,
            Write = index => EngineSettings.PhotoEncoding = (Lumora.Core.Assets.ImageEncoding)index,
        });
        Slider(path, "phototimer", "Settings.Photos.Timer".AsLocale("Timer"), SettingsCatalog.PhotoTimerSeconds,
            () => EngineSettings.PhotoTimerSeconds, v => EngineSettings.PhotoTimerSeconds = v, v => $"{v:0} s");
        Toggle(path, "photosinventory", "Settings.Photos.ToInventory".AsLocale("Save Photos to Inventory"),
            () => EngineSettings.PhotosToInventory, v => EngineSettings.PhotosToInventory = v,
            "Settings.Photos.ToInventory.Hint".AsLocale("Every photo also goes into the Photos folder of your inventory"));
        Toggle(path, "showcapture", "Settings.Photos.ShowCapture".AsLocale("Show When I'm Recording"),
            () => EngineSettings.ShowCapture, v => EngineSettings.ShowCapture = v,
            "Settings.Photos.ShowCapture.Hint".AsLocale("Others see REC or LIVE over your name while OBS records or streams"));

        Header(path, "Reticle", "Settings.Interface.Section.Reticle".AsLocale("Reticle"));
        // The desktop cursor drawer reads these through InterfaceSettings, which forwards to the same
        // fields, so a change repaints the cursor on the next frame.
        _entries.Add(path, ListingChoice.FromEnum<EngineSettings.ReticleShape>("reticlestyle", "Settings.Interface.ReticleStyle".AsLocale("Style"),
            () => EngineSettings.ReticleStyle, v => EngineSettings.ReticleStyle = v));
        var size = Slider(path, "reticlesize", "Settings.Interface.ReticleSize".AsLocale("Size"), SettingsCatalog.ReticleSize,
            () => EngineSettings.ReticleSize, v => EngineSettings.ReticleSize = v, v => $"{v:0} px");
        size.Tag = "reticle";
        var thickness = Slider(path, "reticlethickness", "Settings.Interface.ReticleThickness".AsLocale("Thickness"), SettingsCatalog.ReticleThickness,
            () => EngineSettings.ReticleThickness, v => EngineSettings.ReticleThickness = v, v => $"{v:0.#} px");
        thickness.Tag = "reticle";

        Header(path, "Tooltips", "Settings.Interface.Section.Tooltips".AsLocale("Tooltips"));
        // The laser draws the label beside its cursor, from the name the thing under it gives.
        Toggle(path, "tooltips", "Settings.Interface.Tooltips".AsLocale("Show Tooltips"),
            () => EngineSettings.Tooltips, v => EngineSettings.Tooltips = v);
        _tooltipDelayRow = Slider(path, "tooltipdelay", "Settings.Interface.TooltipDelay".AsLocale("Tooltip Delay"), SettingsCatalog.TooltipDelay,
            () => EngineSettings.TooltipDelay, v => EngineSettings.TooltipDelay = v, v => $"{v:0.0} s");

        Header(path, "People", "Settings.Interface.Section.People".AsLocale("People"));
        // A glowing stand-in with a progress readout while somebody's avatar is still arriving; off shows the
        // avatar as it builds.
        Toggle(path, "avatararrival", "Settings.Interface.AvatarArrival".AsLocale("Avatar Arrival"),
            () => EngineSettings.AvatarArrival, v => EngineSettings.AvatarArrival = v,
            "Settings.Interface.AvatarArrival.Hint".AsLocale("Show a glowing stand-in with progress while someone's avatar loads"));

        Header(path, "Chat", "Settings.Interface.Section.Chat".AsLocale("Chat"));
        Toggle(path, "chatbubbles", "Settings.Interface.ChatBubbles".AsLocale("Show Chat Bubbles"),
            () => EngineSettings.ShowChatBubbles, v => EngineSettings.ShowChatBubbles = v,
            "Settings.Interface.ChatBubbles.Hint".AsLocale("A bubble beside someone's head while they type and after they send"));
        Toggle(path, "desktopchat", "Settings.Interface.DesktopChat".AsLocale("Show Chat Box on Desktop"),
            () => EngineSettings.ShowDesktopChat, v => EngineSettings.ShowDesktopChat = v,
            "Settings.Interface.DesktopChat.Hint".AsLocale("New lines show in the bottom left corner for a while. Off, it only shows while you type"));

        Header(path, "Dashboard", "Settings.Interface.Section.Dashboard".AsLocale("Dashboard"));
        // Freeform leaves the panel where you put it instead of pinning it in front of your view. VR
        // only; desktop is window-projected.
        Toggle(path, "freeform", "Settings.Interface.Freeform".AsLocale("Freeform"),
            () => UserspaceDashboard.LocalInstance?.Freeform.Value ?? false,
            v => UserspaceDashboard.LocalInstance?.SetFreeform(v),
            "Settings.Interface.Freeform.Hint".AsLocale("Stays where you put it"));
        // Edit mode boosts every spawned widget panel's grab above its canvas so you can pick it up;
        // off, the canvas takes clicks again.
        Toggle(path, "editwidgets", "Settings.Interface.EditWidgets".AsLocale("Edit Widgets"),
            () => WidgetPanel.EditMode, v => WidgetPanel.EditMode = v,
            "Settings.Interface.EditWidgets.Hint".AsLocale("Grab widgets to move them"));
    }

    // CONTROLS
    //
    // Generated from the action map rather than hand-listed: a set added in code shows up here with its
    // bindings and its rebind buttons, and nothing has to be kept in step by hand.
    //
    // Keyboard and mouse share one column because they share one desk - a rebind there takes whichever
    // of the two you reach for, and clearing it clears both. Bindings save the moment they change
    // rather than on exit; a remap you cannot undo because you remapped the key that reaches this
    // screen is a trap. -xlinka
    private void BuildControls()
    {
        const string path = "controls";
        var map = Engine.Current?.InputInterface?.Actions;

        Header(path, "Devices", "Settings.Controls.Section.Devices".AsLocale("Devices"));
        _entries.Add(path, new ListingChoice("primaryhand", "Settings.Controls.PrimaryHand".AsLocale("Primary hand"))
        {
            Options = new[] { "Left", "Right" },
            Read = () => EngineSettings.PrimaryHand == Input.Chirality.Left ? 0 : 1,
            Write = index => EngineSettings.PrimaryHand = index == 0 ? Input.Chirality.Left : Input.Chirality.Right,
            DetailText = "Settings.Controls.PrimaryHand.Hint".AsLocale("The hand you lead with, which menus and world scripts can ask about"),
        });
        // Scales every pulse on its way to the controllers. Off sends none.
        Slider(path, "haptics", "Settings.Controls.Haptics".AsLocale("Haptics"), SettingsCatalog.HapticsStrength,
            () => EngineSettings.HapticsStrength, v => EngineSettings.HapticsStrength = v,
            v => v <= 0.001f ? "Off" : $"{v * 100f:0}%");
        _entries.Add(path, new ListingLabel("pad", "Settings.Controls.Gamepad".AsLocale("Gamepad"), DescribePad));
        // A pad's look stick on a screen. A headset turns with its own stick and Turn instead.
        if (DrawsOwnScreen)
        {
            Slider(path, "sticklook", "Settings.Controls.StickLookSpeed".AsLocale("Stick Look Speed"), SettingsCatalog.StickLookSpeed,
                () => EngineSettings.StickLookSpeed, v => EngineSettings.StickLookSpeed = v, v => $"{v:0.00}x");
            Toggle(path, "invertsticklook", "Settings.Controls.InvertStickLook".AsLocale("Invert Stick Look"),
                () => EngineSettings.InvertStickLook, v => EngineSettings.InvertStickLook = v,
                "Settings.Controls.InvertStickLook.Hint".AsLocale("Push the look stick up to look down"));
        }
        _entries.Add(path, new ListingLabel("lastinput", "Settings.Controls.LastInput".AsLocale("Last input"), DescribeLastInput));
        _entries.Add(path, new ListingAction("resetall", "Settings.Controls.ResetAll".AsLocale("Every control back to stock"))
        {
            Invoke = ResetAllBindings,
            ButtonLabel = "Settings.Controls.ResetAllButton".AsLocale("Reset All"),
            Destructive = true,
        });
        _entries.Add(path, new ListingLabel("status", "Settings.Controls.Status".AsLocale("Status"), () => _controlsStatus.Resolve()));

        if (map == null)
            return;

        foreach (var set in map.Sets)
        {
            bool wroteHeader = false;
            foreach (var action in set.Actions)
            {
                if (!action.Rebindable)
                    continue;
                if (!wroteHeader)
                {
                    // Set labels come out of the action map, which is code-owned rather than translated.
                    Header(path, set.Label, set.Label);
                    wroteHeader = true;
                }
                _entries.Add(path, new ListingCustom("bind." + set.Label + "." + action.Name, KindBinding, action.Label)
                {
                    Tag = action,
                });
            }
        }
    }

    // ENTRY HELPERS

    // Keyed on an id, not on the label: an item key has to hold still when the interface language
    // changes, or every row in the list reads as brand new to the reconcile the moment somebody
    // switches. -xlinka
    private void Header(string path, string id, LocaleText label)
        => _entries.Add(path, new ListingHeader(path + ".h." + id, label));

    // hint is the second line under the label, for a setting whose name alone does not say what it
    // does. It replaces the parenthetical that used to be stuffed into the label itself and wrapped it
    // onto two lines.
    private ListingToggle Toggle(string path, string key, LocaleText label, Func<bool> read, Action<bool> write,
        LocaleText hint = default)
        => _entries.Add(path, new ListingToggle(key, label) { Read = read, Write = write, DetailText = hint });

    // Range-taking overload. Every slider whose setting has bounds goes through this one, so the row can
    // no longer offer a narrower span than the engine accepts. Three of them already did. -xlinka
    private ListingSlider Slider(string path, string key, LocaleText label, in SettingRange range,
        Func<float> read, Action<float> write, Func<float, string> format)
        => Slider(path, key, label, range.Min, range.Max, range.Step, read, write, format);

    private ListingSlider Slider(string path, string key, LocaleText label, float min, float max, float step,
        Func<float> read, Action<float> write, Func<float, string> format)
        => _entries.Add(path, new ListingSlider(key, label)
        {
            Min = min,
            Max = max,
            Step = step,
            Read = read,
            Write = write,
            Format = format,
        });

    // Rounded to whole inches before the split, so 1.80 m reads 5 ft 11 in and never 5 ft 12 in.
    private static string DescribeHeight(float metres)
    {
        if (!EngineSettings.ImperialUnits)
            return $"{metres:0.00} m";
        int inches = (int)MathF.Round(metres / MetresPerInch);
        return $"{inches / 12} ft {inches % 12} in";
    }

    // The list shows whatever locale tables actually loaded, in their own language, plus the generated
    // pseudo locale. Nothing is offered that has no table behind it: a language you can pick and that
    // then changes nothing is the exact kind of placebo setting this screen does not carry. -xlinka
    private static ListingDropdown BuildLanguageChoice()
    {
        var available = LocaleManager.Available;
        var codes = new List<string>(available.Count);
        var names = new List<string>(available.Count);
        for (int i = 0; i < available.Count; i++)
        {
            codes.Add(available[i].Code);
            names.Add(available[i].NativeName);
        }
        return new ListingDropdown("language", "Settings.Interface.Language".AsLocale("Language"))
        {
            Options = names,
            Read = () => LocaleManager.IndexOf(LocaleManager.CurrentLocale),
            Write = index =>
            {
                if (index >= 0 && index < codes.Count)
                    LocaleManager.SetLocale(codes[index]);
            },
        };
    }

    private static string[] ResolutionOptionLabels()
    {
        var options = EngineSettings.ResolutionHeightOptions;
        var labels = new string[options.Length];
        for (int i = 0; i < options.Length; i++)
            labels[i] = EngineSettings.DescribeResolutionHeight(options[i]);
        return labels;
    }

    private static int ResolutionIndex(int height)
    {
        var options = EngineSettings.ResolutionHeightOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == height)
                return i;
        }
        return 0;
    }

    private static int TextureSizeIndex(int size)
    {
        var options = EngineSettings.TextureSizeOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == size)
                return i;
        }
        return 0;
    }

    private static int AntiAliasingIndex(int mode)
    {
        var options = EngineSettings.AntiAliasingOptions;
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == mode)
                return i;
        }
        return 0;
    }

    // LIVE STATE

    protected override void OnShow()
    {
        base.OnShow();
        // A pad plugged in, a rebind made elsewhere, or an avatar that finished spawning while this
        // screen sat closed.
        BuildDeviceChoices(input: true);
        BuildDeviceChoices(input: false);
        BuildStreamDeviceChoices();
        BuildWebAccessRows();
        _entryView?.RefreshValues();
        MarkDirty();
    }

    protected override void OnHide()
    {
        CancelPendingRebind();
        base.OnHide();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!Slot.ActiveSelf.Value)
            return;

        // The rows take their shape from the list's width, so a new width measures every row again.
        if (_entryViewport != null && _rowFit.Update(_entryViewport.LocalComputeRect.width))
            _entryView?.Refresh();
        PumpRebindCapture();

        _refreshAccum += delta;
        if (_refreshAccum < RefreshInterval)
            return;
        _refreshAccum = 0f;

        ApplyTurnModeGating();
        ApplyTooltipGating();
        ApplyHeightStep();
        if (WebAccess.Shared.Version != _webAccessVersion)
            BuildWebAccessRows();
        if (Favourites.Version != _favouritesVersion)
            BuildFavouriteRows();
        if (_activeCategory == NetworkPath)
            RefreshNodeChoice(delta: RefreshInterval);
        _entryView?.RefreshValues();
    }

    // Snap angle and smooth speed each only mean something in their own mode. Flipping Interactable
    // here (rather than rebuilding the page) keeps the row where it is and just greys it.
    private void ApplyTurnModeGating()
    {
        var items = _entries.ItemsAt("movement");
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Tag is EngineSettings.TurnStyle style)
                items[i].Interactable = EngineSettings.TurnMode == style;
        }
    }

    // The delay means nothing with tooltips off, so it greys out the same way.
    private void ApplyTooltipGating()
    {
        if (_tooltipDelayRow != null)
            _tooltipDelayRow.Interactable = EngineSettings.Tooltips;
    }

    // An inch a notch while imperial is on, so every step of the bar is a new label; a centimetre
    // otherwise. Set on the tick so units brought by the account change it too.
    private void ApplyHeightStep()
    {
        if (_heightRow != null)
            _heightRow.Step = EngineSettings.ImperialUnits ? MetresPerInch : SettingsCatalog.UserHeight.Step;
    }

    // Polls the map's listener rather than driving it: the capture itself happens inside the input
    // pass, where the raw devices live, so all this has to do is notice when it finished.
    private void PumpRebindCapture()
    {
        if (_activeCategory != "controls")
            return;
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;

        switch (map.CaptureStatus)
        {
            case InputBindingMap.CaptureState.Listening:
                _controlsStatus = CapturePromptText(map.CaptureTarget?.Label ?? string.Empty, map.CapturePrompt);
                break;

            case InputBindingMap.CaptureState.Captured:
                var bound = map.CaptureConflicts;
                if (bound.Count > 0)
                {
                    var names = new List<string>(bound.Count);
                    foreach (var conflict in bound)
                        names.Add(conflict.Label);
                    _controlsStatus = "Settings.Controls.BoundConflicts"
                        .AsLocale("Bound. Also used by: {0}.", string.Join(", ", names));
                }
                else
                {
                    _controlsStatus = "Settings.Controls.Bound".AsLocale("Bound.");
                }
                map.ClearCapture();
                PersistBindings();
                _entryView?.RefreshValues();
                break;

            case InputBindingMap.CaptureState.Cancelled:
                map.ClearCapture();
                _controlsStatus = "Settings.Controls.RebindCancelled".AsLocale("Rebind cancelled.");
                _entryView?.RefreshValues();
                break;
        }
    }

    internal void BeginRebind(InputAction action, InputDeviceKind[] devices)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ClearCapture();
        map.BeginCapture(action, devices);
        _controlsStatus = CapturePromptText(action.Label, map.CapturePrompt);
        _entryView?.RefreshValues();
    }

    // A pad has no Escape. Its listener lets go on its own when nothing comes.
    private static LocaleText CapturePromptText(string label, string prompt)
        => InputHints.PadNames
            ? "Settings.Controls.CapturePromptPad".AsLocale("For \"{0}\": {1}. Leave it a few seconds to cancel.", label, prompt)
            : "Settings.Controls.CapturePrompt".AsLocale("For \"{0}\": {1}. Escape cancels.", label, prompt);

    internal void ClearAllBindings(InputAction action)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ClearBindings(action, InputDeviceKind.Keyboard, InputDeviceKind.Mouse, InputDeviceKind.Gamepad, InputDeviceKind.VRController);
        PersistBindings();
        _controlsStatus = "Settings.Controls.Cleared".AsLocale("Cleared every binding for \"{0}\".", action.Label);
        _entryView?.RefreshValues();
    }

    internal void ResetBinding(InputAction action)
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ResetToDefaults(action);
        PersistBindings();
        _controlsStatus = "Settings.Controls.Restored".AsLocale("Restored the stock binding for \"{0}\".", action.Label);
        _entryView?.RefreshValues();
    }

    private void ResetAllBindings()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null)
            return;
        map.ResetAllToDefaults();
        PersistBindings();
        _controlsStatus = "Settings.Controls.AllReset".AsLocale("Every control is back to stock.");
        _entryView?.RefreshValues();
    }

    private static void PersistBindings() => Engine.Current?.InputInterface?.SaveBindingOverrides();

    private void CancelPendingRebind()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        if (map == null || map.CaptureStatus != InputBindingMap.CaptureState.Listening)
            return;
        map.ClearCapture();
        _controlsStatus = "Settings.Controls.RebindCancelled".AsLocale("Rebind cancelled.");
    }

    private static string DescribePad()
    {
        var input = Engine.Current?.InputInterface;
        var pad = input?.Gamepad;
        if (pad == null || !pad.IsConnected)
            return "none connected";
        int count = input?.GetGamepadDriver()?.ConnectedPadCount ?? 1;
        return count > 1 ? $"{pad.DeviceName} (+{count - 1} idle)" : pad.DeviceName;
    }

    private static string DescribeLastInput()
    {
        var map = Engine.Current?.InputInterface?.Actions;
        return map?.LastActivatedBinding?.Describe() ?? "-";
    }

    internal LocomotionController? GetLocalLocomotionController()
    {
        var userRoot = World?.LocalUser?.Root;
        if (userRoot == null)
            return null;
        return userRoot.GetRegisteredComponent<LocomotionController>() ?? userRoot.Slot?.GetComponent<LocomotionController>();
    }

    internal void SelectLocomotionModule(LocomotionModule module)
    {
        var locomotion = GetLocalLocomotionController();
        if (locomotion == null || !locomotion.IsModuleUsable(module))
            return;
        locomotion.ActivateModule(module);
        // A deliberate pick from Settings is a standing preference for future spawns, not just this
        // session - unlike the radial menu, which only ever changes the live module.
        EngineSettings.PreferredLocomotion = module.DisplayName;
    }

    private void MarkDirty() => Slot.GetComponentInParents<Canvas>()?.MarkDirty();
}
