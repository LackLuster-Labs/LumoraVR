// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Audio;
using Lumora.Core.Components.Import;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The dash's top bar: one quiet strip of glass the width of the dash. On the left who you are and your
// microphone, one button that shows how you are talking and opens the ranges; in the middle the status row;
// on the right the tools as line icons, the ones that do not fit behind a "more" button, and the way out of
// the world at the far end. Every control is the same height, every glyph is in the text colour, and only
// something switched on is lit. The tools are the same widgets the desk can carry, built as icons, so a press
// here does exactly what the card does; their names come up as a tip under the bar. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class DashTopBar : UIComponent
{
    public const float Height = 46f;
    public const float Control = 34f;
    public const float ToolWidth = Control;
    public const float ToolHeight = Control;
    private const float Pad = 6f;
    private const float ProfileWidth = 150f;
    private const float Disc = 26f;
    private const float ToolGap = 2f;
    private const float IslandGap = 16f;
    private const float PopoverGap = 6f;
    private const float TipGap = 6f;
    private const float PopoverPad = 8f;
    private const float AccountWidth = 340f;
    // A popover hangs over the dock and the windows, so it draws at the modal level and anything inside it
    // that makes a chunk of its own one above that. As plain shell chunks the dock's plates, packed into
    // the same band, drew over the account card. -xlinka
    private const int PopoverLevel = 2;
    private const int PopoverContentLevel = 3;
    private const float VoiceStripHeight = 40f;
    private const float MoreRow = 38f;
    private const float MoreWidth = 220f;
    private const float PollInterval = 0.25f;
    private const float MeterInterval = 0.05f;
    private const float MeterWidth = 96f;
    private const float MeterHeight = 10f;
    private const float MeterPad = 2f;
    private const float MeterGap = 8f;
    private const float PeakWidth = 3f;

    private static readonly VoiceMode[] Modes = { VoiceMode.Whisper, VoiceMode.Normal, VoiceMode.Shout, VoiceMode.Broadcast };
    private static readonly string[] ModeIcons = { "whisper", "mic", "shout", "broadcast" };
    private static readonly string[] ModeNames = { "Whisper", "Normal", "Shout", "Broadcast" };

    private sealed class BarButton
    {
        public Slot Slot = null!;
        public RoundedPanel Plate = null!;
        public Image Glyph = null!;
        public RoundedButton Button = null!;
        public bool Hovered;
        public color HoverFill = DashTheme.GlassCardHover;
        public color HoverInk = DashTheme.Text;
        public Func<bool> IsOpen = () => false;
        public Func<bool> IsLive = () => true;
    }

    private Dashboard? _dash;
    private DashStatusIsland? _island;
    private Slot? _profile;
    private RoundedPanel? _profilePaint;
    private Text? _profileName;
    private Text? _profileStorage;
    private ArcSegment? _storageRing;
    private Image? _profileGlyph;
    private bool _profileHovered;
    private BarButton? _mic;
    private Slot? _micMeter;
    private RectTransform? _micLevel;
    private RectTransform? _micPeak;
    private BarButton? _more;
    private BarButton? _leave;
    private Slot? _accountPopover;
    private float _accountHeight;
    private Slot? _voicePopover;
    private Slot? _morePopover;
    private AccountWidgetPreset? _account;
    private VoiceWidgetPreset? _voice;
    private readonly List<HomeWidgetPreset> _tools = new();
    private readonly List<(HomeWidgetPreset Tool, Slot Row, Text Name)> _overflow = new();
    private readonly Dictionary<HomeWidgetPreset, float> _toolCenters = new();
    private readonly HashSet<HomeWidgetPreset> _hooked = new();
    private HomeWidgetPreset? _tipFor;
    private string? _tipOwner;
    private float _laidWidth = -1f;
    private float _poll = PollInterval;
    private float _meterTimer;
    private float _level;
    private float _shownLevel = -1f;
    private float _peak;
    private float _shownPeak = -1f;
    private AudioInput? _metered;
    private bool? _signedShown;
    private string? _nameShown;
    private string? _micIconShown;
    private bool _leaveKnown;
    private bool _leaveLive;
    private int _shownTools = -1;

    public AccountWidgetPreset? Account => _account != null && !_account.IsDestroyed ? _account : null;
    public VoiceWidgetPreset? Voice => _voice != null && !_voice.IsDestroyed ? _voice : null;
    public IReadOnlyList<HomeWidgetPreset> Tools => _tools;
    public Slot? ProfileSlot => _profile;
    public Slot? MicSlot => _mic?.Slot;
    public Slot? MoreSlot => _more?.Slot;
    public Slot? LeaveSlot => _leave?.Slot;
    public Slot? Popover => _accountPopover;
    public Slot? VoicePopover => _voicePopover;
    public Slot? MorePopover => _morePopover;
    public bool PopoverOpen => Shown(_accountPopover);
    public bool VoiceOpen => Shown(_voicePopover);
    public bool MoreOpen => Shown(_morePopover);
    public string ProfileLabel => _profileName?.Content.Value ?? string.Empty;
    public string MicIcon => _micIconShown ?? string.Empty;
    public float MicLevel => _shownLevel < 0f ? 0f : _shownLevel;
    public bool LeaveLive => _leaveLive;
    public HomeWidgetPreset? TipFor => _tipFor;
    // How many tools stand on the bar; the rest are behind the more button.
    public int ToolsShown => _shownTools < 0 ? _tools.Count : _shownTools;

    private static bool Shown(Slot? slot) => slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value;

    internal void Bind(Dashboard dash, DashStatusIsland island)
    {
        _dash = dash;
        _island = island;
        var rect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(Dashboard.Outer, -(Dashboard.IslandTop + Height));
        rect.OffsetMax.Value = new float2(-Dashboard.Outer, -Dashboard.IslandTop);
        Slot.AttachComponent<GraphicChunkRoot>();
        SettingsUI.Panel(Slot, DashTheme.GlassChrome, DashTheme.OutlineStrong, Height * 0.5f);

        BuildProfile(dash);
        _mic = AddButton(dash, "Mic", "mic", new float2(0f, 0.5f), ToggleVoice);
        _mic.IsOpen = () => VoiceOpen;
        _mic.Button.HoverEntered += _ => ShowOwnTip("mic", VoiceTip(), _mic.Slot);
        _mic.Button.HoverExited += _ => HideOwnTip("mic");
        BuildMicMeter();
        Slot.ActiveChanged += OnSlotActiveChanged;

        AddTool<AvatarStudioWidgetPreset>("AvatarStudio");
        AddTool<CameraWidgetPreset>("Camera");
        AddTool<StreamAudioWidgetPreset>("StreamAudio");
        // No platform clipboard bridge means a paste can never do anything, so the icon is not there rather
        // than sitting greyed out forever.
        if (ImportHandlers.Clipboard != null)
            AddTool<PasteWidgetPreset>("Paste");
        AddTool<FreeformDashWidgetPreset>("FreeformDash");
        AddTool<EditWidgetsWidgetPreset>("EditWidgets");
        AddTool<NewWorldWidgetPreset>("NewWorld");
        if (!Engine.IsMobilePlatform)
            AddTool<OpenLogsWidgetPreset>("Logs");
        AddTool<OpenDebugWidgetPreset>("Debug");

        _more = AddButton(dash, "More", "more", new float2(1f, 0.5f), ToggleMore);
        _more.IsOpen = () => MoreOpen;
        _more.Button.HoverEntered += _ => ShowOwnTip("more", "More tools", _more.Slot);
        _more.Button.HoverExited += _ => HideOwnTip("more");
        _leave = AddButton(dash, "Leave", "leave", new float2(1f, 0.5f), Leave);
        _leave.HoverFill = DashTheme.Negative.WithAlpha(0.16f);
        _leave.HoverInk = DashTheme.Negative;
        _leave.IsLive = () => _leaveLive;
        _leave.Button.HoverEntered += _ => ShowOwnTip("leave", "Leave this world", _leave.Slot);
        _leave.Button.HoverExited += _ => HideOwnTip("leave");

        var shell = Slot.Parent ?? Slot;
        _accountPopover = BuildAccountPopover(dash, shell);
        _voicePopover = BuildVoicePopover(dash, shell);
        _morePopover = BuildMorePopover(dash, shell);
        LiftContent(_accountPopover);
        LiftContent(_voicePopover);
        LiftContent(_morePopover);
        Layout();
        RefreshProfile();
        RefreshMic();
        RefreshLeave();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_dash == null || World == null)
            return;
        if (_dash.Size.Value.x != _laidWidth)
            Layout();
        HookTools();
        float delta = World.Time.RawDelta;
        _meterTimer += delta;
        if (_meterTimer >= MeterInterval)
        {
            float step = _meterTimer;
            _meterTimer = 0f;
            Meter(step);
        }
        _poll += delta;
        if (_poll < PollInterval)
            return;
        _poll = 0f;
        RefreshProfile();
        RefreshMic();
        RefreshLeave();
        RefreshOverflowNames();
    }

    public override void OnDestroy()
    {
        Slot.ActiveChanged -= OnSlotActiveChanged;
        SetMetering(false);
        base.OnDestroy();
    }

    private void OnSlotActiveChanged(Slot slot)
    {
        if (!slot.IsActive)
            SetMetering(false);
    }

    private void BuildMicMeter()
    {
        _micMeter = SettingsUI.Child(Slot, "MicMeter", new float2(0f, 0.5f), new float2(0f, 0.5f),
            float2.Zero, new float2(MeterWidth, MeterHeight));
        _micMeter.AttachComponent<GraphicChunkRoot>();
        SettingsUI.Panel(_micMeter, DashTheme.MeterTrack, DashTheme.Outline, MeterHeight * 0.5f);
        var inner = SettingsUI.Child(_micMeter, "Inner", float2.Zero, float2.One,
            new float2(MeterPad, MeterPad), new float2(-MeterPad, -MeterPad));
        var fill = SettingsUI.Child(inner, "Fill", float2.Zero, new float2(0f, 1f), float2.Zero, float2.Zero);
        _micLevel = SettingsUI.Rect(fill);
        SettingsUI.Panel(fill, DashTheme.Positive, color.Transparent, (MeterHeight - 2f * MeterPad) * 0.5f);
        var peak = SettingsUI.Child(inner, "Peak", float2.Zero, new float2(0f, 1f), float2.Zero, new float2(PeakWidth, 0f));
        _micPeak = SettingsUI.Rect(peak);
        SettingsUI.Panel(peak, DashTheme.MeterPeak, color.Transparent, PeakWidth * 0.5f);
    }

    // BUILD

    private void BuildProfile(Dashboard dash)
    {
        _profile = SettingsUI.Child(Slot, "Profile", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(Pad, -Control * 0.5f), new float2(Pad + ProfileWidth, Control * 0.5f));
        _profile.AttachComponent<GraphicChunkRoot>();
        _profilePaint = SettingsUI.Panel(_profile, color.Transparent, color.Transparent, Control * 0.5f);

        var disc = SettingsUI.Child(_profile, "Disc", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(4f, -Disc * 0.5f), new float2(4f + Disc, Disc * 0.5f));
        SettingsUI.Panel(disc, DashTheme.Field.WithAlpha(0.7f), DashTheme.OutlineStrong, Disc * 0.5f);
        var storageRing = SettingsUI.Fill(disc, "StorageUsage");
        _storageRing = storageRing.AttachComponent<ArcSegment>();
        _storageRing.InnerRadius.Value = Disc * 0.5f - 1.5f;
        _storageRing.OuterRadius.Value = Disc * 0.5f;
        _storageRing.AngleStart.Value = -90f;
        _storageRing.ArcLength.Value = 0f;
        _storageRing.OutlineThickness.Value = 0f;
        storageRing.ActiveSelf.Value = false;
        var glyphSlot = SettingsUI.Child(disc, "Glyph", float2.Zero, float2.One, new float2(5f, 5f), new float2(-5f, -5f));
        _profileGlyph = glyphSlot.AttachComponent<Image>();
        _profileGlyph.Texture.Target = dash.IconProvider("avatar");
        _profileGlyph.PreserveAspect.Value = true;
        _profileGlyph.InteractionTarget.Value = false;
        _profileGlyph.Tint.Value = DashTheme.TextDim;

        _profileName = SettingsUI.Label(_profile, "Name", dash.FontSemibold.Target ?? dash.Font.Target, DashTheme.FontSmall,
            DashTheme.Text, TextHorizontalAlignment.Left, float2.Zero, float2.One, new float2(Disc + 12f, 0f), new float2(-8f, 0f),
            shrinkToFit: true);

        _profileStorage = SettingsUI.Label(_profile, "Storage", dash.Font.Target, 11f,
            DashTheme.TextDim, TextHorizontalAlignment.Left, float2.Zero, new float2(1f, 0.42f),
            new float2(Disc + 12f, 0f), new float2(-8f, 0f), shrinkToFit: true);
        _profileStorage.Slot.ActiveSelf.Value = false;

        var button = _profile.AttachComponent<RoundedButton>();
        button.CornerRadius.Value = Control * 0.5f;
        button.Clicked += (_, _) => PressProfile();
        button.HoverEntered += _ =>
        {
            _profileHovered = true;
            PaintProfile();
            if (Account?.IsSignedIn == true)
                ShowOwnTip("account", "Cloud storage: " + Account.StorageLabel, _profile);
        };
        button.HoverExited += _ => { _profileHovered = false; PaintProfile(); HideOwnTip("account"); };
    }

    private BarButton AddButton(Dashboard dash, string name, string icon, in float2 anchor, Action press)
    {
        var slot = SettingsUI.Child(Slot, name, anchor, anchor, new float2(0f, -Control * 0.5f), new float2(Control, Control * 0.5f));
        slot.AttachComponent<GraphicChunkRoot>();
        var entry = new BarButton { Slot = slot };
        entry.Plate = SettingsUI.Panel(slot, color.Transparent, color.Transparent, DashTheme.RadiusControl);
        var glyphSlot = SettingsUI.Child(slot, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-9f, -9f), new float2(9f, 9f));
        entry.Glyph = glyphSlot.AttachComponent<Image>();
        entry.Glyph.Texture.Target = dash.IconProvider(icon);
        entry.Glyph.PreserveAspect.Value = true;
        entry.Glyph.InteractionTarget.Value = false;
        entry.Glyph.Tint.Value = DashTheme.TextDim;
        entry.Button = slot.AttachComponent<RoundedButton>();
        entry.Button.HitOutset.Value = ToolGap * 0.5f;
        entry.Button.Clicked += (_, _) => press();
        entry.Button.HoverEntered += _ => { entry.Hovered = true; PaintButton(entry); };
        entry.Button.HoverExited += _ => { entry.Hovered = false; PaintButton(entry); };
        return entry;
    }

    // Clear at rest, glass under the pointer, a faint accent while what it opens is open; a dead button does
    // not light at all.
    private static void PaintButton(BarButton entry)
    {
        if (entry.Plate.IsDestroyed)
            return;
        bool open = entry.IsOpen();
        bool live = entry.IsLive();
        var fill = !live ? color.Transparent : open ? DashTheme.AccentSoft : entry.Hovered ? entry.HoverFill : color.Transparent;
        var ink = !live ? DashTheme.TextMuted.WithAlpha(0.5f) : open ? DashTheme.Accent : entry.Hovered ? entry.HoverInk : DashTheme.TextDim;
        SettingsUI.SetFill(entry.Plate, fill);
        if (!entry.Glyph.Tint.Value.Equals(ink))
            entry.Glyph.Tint.Value = ink;
    }

    private void AddTool<T>(string name) where T : HomeWidgetPreset, new()
    {
        var slot = SettingsUI.Child(Slot, name, new float2(1f, 0.5f), new float2(1f, 0.5f), float2.Zero, float2.Zero);
        slot.AttachComponent<GraphicChunkRoot>();
        _tools.Add(MakeTool<T>(slot));
    }

    private static T MakeTool<T>(Slot slot) where T : HomeWidgetPreset, new()
    {
        var tool = slot.AttachComponent<T>();
        tool.IconOnly = true;
        tool.Background.Value = color.Transparent;
        tool.BorderColor.Value = color.Transparent;
        tool.CornerRadius.Value = DashTheme.RadiusControl;
        return tool;
    }

    private Slot NewPopover(Dashboard dash, Slot shell, string name)
    {
        var popover = shell.AddSlot(name);
        var rect = popover.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(0f, 1f);
        popover.AttachComponent<GraphicChunkRoot>().OverlayLevel = PopoverLevel;
        var panel = popover.AttachComponent<RoundedPanel>();
        panel.CornerRadius.Value = DashTheme.RadiusPanel;
        // Solid, not the windows' glass: a popover hangs over the dock and whatever window is open, and
        // through glass their icons read as ghosts behind the text.
        panel.Color.Value = DashTheme.Panel;
        panel.OutlineColor.Value = DashTheme.OutlineStrong;
        panel.OutlineThickness.Value = DashTheme.OutlineWidth;
        panel.Material.Target = dash.BackingMaterial!;
        // Takes every press its contents do not, so nothing under it hears one.
        popover.AttachComponent<InteractionElement>();
        popover.ActiveSelf.Value = false;
        return popover;
    }

    private static void LiftContent(Slot popover)
    {
        foreach (var chunk in popover.GetComponentsInChildren<GraphicChunkRoot>(includeSelf: false))
            chunk.OverlayLevel = PopoverContentLevel;
    }

    // Hung below the bar at a left edge, kept on the dash.
    private void PlacePopover(Slot? popover, float left, float width, float height)
    {
        if (popover == null || popover.IsDestroyed || _dash == null)
            return;
        float canvas = _dash.Size.Value.x;
        left = MathF.Max(Dashboard.Outer, MathF.Min(left, canvas - Dashboard.Outer - width));
        float top = Dashboard.IslandTop + Height + PopoverGap;
        SettingsUI.SetOffsets(SettingsUI.Rect(popover), new float2(left, -(top + height)), new float2(left + width, -top));
    }

    private Slot BuildAccountPopover(Dashboard dash, Slot shell)
    {
        var popover = NewPopover(dash, shell, "AccountPopover");
        var card = SettingsUI.Fill(popover, "Account");
        _account = card.AttachComponent<AccountWidgetPreset>();
        _account.Background.Value = color.Transparent;
        _account.BorderColor.Value = color.Transparent;
        // Its quota feeds the always-visible bar, even before the popover has been opened.
        _account.EnsureBuilt();
        _accountHeight = _account.CardHeight;
        PlacePopover(popover, Dashboard.Outer, AccountWidth, _accountHeight);
        return popover;
    }

    // The voice switches stay in the dropdown; the live meter is always on the bar.
    private Slot BuildVoicePopover(Dashboard dash, Slot shell)
    {
        var popover = NewPopover(dash, shell, "VoicePopover");
        var strip = SettingsUI.Child(popover, "Voice", float2.Zero, float2.One, new float2(PopoverPad, PopoverPad), new float2(-PopoverPad, -PopoverPad));
        _voice = strip.AttachComponent<VoiceWidgetPreset>();
        _voice.Quiet = true;
        _voice.ShowMeter = false;
        _voice.MinSize.Value = new float2(VoiceWidgetPreset.ControlsWidth, 32f);
        _voice.PreferredSize.Value = new float2(VoiceWidgetPreset.ControlsWidth, VoiceStripHeight);
        _voice.Background.Value = color.Transparent;
        _voice.BorderColor.Value = color.Transparent;
        return popover;
    }

    private float VoiceWidth => VoiceWidgetPreset.ControlsWidth + PopoverPad * 2f;

    // Every tool once more, as a row with its name: shown here only while it does not fit on the bar.
    private Slot BuildMorePopover(Dashboard dash, Slot shell)
    {
        var popover = NewPopover(dash, shell, "MorePopover");
        for (int i = 0; i < _tools.Count; i++)
        {
            var row = SettingsUI.Child(popover, "Row" + i, new float2(0f, 1f), new float2(1f, 1f),
                new float2(PopoverPad, -(PopoverPad + (i + 1) * MoreRow)), new float2(-PopoverPad, -(PopoverPad + i * MoreRow)));
            var host = SettingsUI.Child(row, "Tool", new float2(0f, 0.5f), new float2(0f, 0.5f), new float2(0f, -Control * 0.5f), new float2(Control, Control * 0.5f));
            host.AttachComponent<GraphicChunkRoot>();
            var twin = CopyTool(_tools[i], host);
            var name = SettingsUI.Label(row, "Name", dash.Font.Target, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Left,
                float2.Zero, float2.One, new float2(Control + 10f, 0f), float2.Zero, shrinkToFit: true);
            row.ActiveSelf.Value = false;
            _overflow.Add((twin, row, name));
        }
        return popover;
    }

    private static HomeWidgetPreset CopyTool(HomeWidgetPreset tool, Slot host) => tool switch
    {
        AvatarStudioWidgetPreset => MakeTool<AvatarStudioWidgetPreset>(host),
        CameraWidgetPreset => MakeTool<CameraWidgetPreset>(host),
        StreamAudioWidgetPreset => MakeTool<StreamAudioWidgetPreset>(host),
        PasteWidgetPreset => MakeTool<PasteWidgetPreset>(host),
        FreeformDashWidgetPreset => MakeTool<FreeformDashWidgetPreset>(host),
        EditWidgetsWidgetPreset => MakeTool<EditWidgetsWidgetPreset>(host),
        NewWorldWidgetPreset => MakeTool<NewWorldWidgetPreset>(host),
        OpenLogsWidgetPreset => MakeTool<OpenLogsWidgetPreset>(host),
        _ => MakeTool<OpenDebugWidgetPreset>(host),
    };

    // LAYOUT

    private void Layout() => LayoutAt(_dash?.Size.Value.x ?? 0f);

    // The left group and the right group hold their sizes; the status row takes what lies between, centred on
    // the dash when it fits there and pushed off centre only as far as the groups make it. Tools that would
    // squeeze the status row under its floor go behind the more button, from the last one in.
    public void LayoutAt(float canvas)
    {
        if (_dash == null || canvas <= 0f)
            return;
        _laidWidth = _dash.Size.Value.x;
        float inner = MathF.Max(1f, canvas - Dashboard.Outer * 2f);

        float micLeft = Pad + ProfileWidth + 4f;
        Place(_mic?.Slot, new float2(micLeft, -Control * 0.5f), new float2(micLeft + Control, Control * 0.5f));
        float meterLeft = micLeft + Control + MeterGap;
        Place(_micMeter, new float2(meterLeft, -MeterHeight * 0.5f), new float2(meterLeft + MeterWidth, MeterHeight * 0.5f));
        float leftEnd = meterLeft + MeterWidth;

        float step = ToolWidth + ToolGap;
        float room = inner - leftEnd - IslandGap * 2f - DashStatusIsland.MinWidth - (Pad + Control + ToolGap);
        int fit = System.Math.Max(0, (int)MathF.Floor((room + ToolGap) / step));
        bool more = fit < _tools.Count;
        int shown = more ? System.Math.Max(0, (int)MathF.Floor((room - Control) / step)) : _tools.Count;
        _shownTools = shown;

        float right = Pad;
        Place(_leave?.Slot, new float2(-(right + Control), -Control * 0.5f), new float2(-right, Control * 0.5f));
        right += Control + ToolGap;
        if (_more != null)
        {
            SetActive(_more.Slot, more);
            if (more)
            {
                Place(_more.Slot, new float2(-(right + Control), -Control * 0.5f), new float2(-right, Control * 0.5f));
                right += Control + ToolGap;
            }
            else
            {
                SetOpen(_morePopover, false);
            }
        }
        _toolCenters.Clear();
        for (int i = _tools.Count - 1; i >= 0; i--)
        {
            var tool = _tools[i];
            bool onBar = i < shown;
            SetActive(tool.Slot, onBar);
            if (i < _overflow.Count)
                SetActive(_overflow[i].Row, !onBar);
            if (!onBar)
                continue;
            Place(tool.Slot, new float2(-(right + ToolWidth), -ToolHeight * 0.5f), new float2(-right, ToolHeight * 0.5f));
            _toolCenters[tool] = Dashboard.Outer + inner - right - ToolWidth * 0.5f;
            right += step;
        }
        LayOverflowRows(shown);
        float rightStart = inner - (right - ToolGap);

        float left = leftEnd + IslandGap;
        float end = rightStart - IslandGap;
        float width = MathF.Max(DashStatusIsland.MinWidth, MathF.Min(DashStatusIsland.MaxWidth, end - left));
        float center = MathF.Max(left + width * 0.5f, MathF.Min(inner * 0.5f, end - width * 0.5f));
        _island?.Place(center - inner * 0.5f, width);

        PlacePopover(_voicePopover, Dashboard.Outer + micLeft, VoiceWidth, VoiceStripHeight + PopoverPad * 2f);
    }

    private void LayOverflowRows(int shown)
    {
        int row = 0;
        for (int i = shown; i < _overflow.Count; i++, row++)
        {
            SettingsUI.SetOffsets(SettingsUI.Rect(_overflow[i].Row), new float2(PopoverPad, -(PopoverPad + (row + 1) * MoreRow)),
                new float2(-PopoverPad, -(PopoverPad + row * MoreRow)));
        }
        if (_more != null && _dash != null)
        {
            float moreRight = Dashboard.Outer + MathF.Max(1f, _dash.Size.Value.x - Dashboard.Outer * 2f) - (Pad + Control + ToolGap);
            PlacePopover(_morePopover, moreRight - MoreWidth, MoreWidth, PopoverPad * 2f + row * MoreRow);
        }
    }

    private static void Place(Slot? slot, in float2 min, in float2 max)
    {
        if (slot != null && !slot.IsDestroyed)
            SettingsUI.SetOffsets(SettingsUI.Rect(slot), min, max);
    }

    private static void SetActive(Slot? slot, bool active)
    {
        if (slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value != active)
            slot.ActiveSelf.Value = active;
    }

    // POPOVERS: one at a time.

    private void SetOpen(Slot? popover, bool open)
    {
        if (open)
        {
            foreach (var other in new[] { _accountPopover, _voicePopover, _morePopover })
            {
                if (!ReferenceEquals(other, popover))
                    SetActive(other, false);
            }
        }
        SetActive(popover, open);
        PaintProfile();
        if (_mic != null)
            PaintButton(_mic);
        if (_more != null)
            PaintButton(_more);
    }

    public void SetPopover(bool open)
    {
        if (open && Account?.IsSignedIn != true)
            open = false;
        SetOpen(_accountPopover, open);
    }

    public void SetVoiceOpen(bool open) => SetOpen(_voicePopover, open);

    public void SetMoreOpen(bool open) => SetOpen(_morePopover, open && _more != null && _more.Slot.ActiveSelf.Value);

    private void ToggleVoice() => SetVoiceOpen(!VoiceOpen);

    private void ToggleMore() => SetMoreOpen(!MoreOpen);

    // A press anywhere on the dash but an open popover or what opened it puts it away.
    internal void OnDashPress(Slot? slot)
    {
        if (PopoverOpen && !(Within(slot, _accountPopover) || Within(slot, _profile)))
            SetPopover(false);
        if (VoiceOpen && !(Within(slot, _voicePopover) || Within(slot, _mic?.Slot)))
            SetVoiceOpen(false);
        if (MoreOpen && !(Within(slot, _morePopover) || Within(slot, _more?.Slot)))
            SetMoreOpen(false);
    }

    private static bool Within(Slot? slot, Slot? scope)
        => slot != null && scope != null && (ReferenceEquals(slot, scope) || slot.IsDescendantOf(scope));

    // TIPS

    // A tool's button exists once the tool has built, which is a frame after the bar did.
    private void HookTools()
    {
        if (_hooked.Count == _tools.Count)
            return;
        foreach (var tool in _tools)
        {
            if (tool.IsDestroyed || _hooked.Contains(tool))
                continue;
            var button = tool.Slot.GetComponent<Button>();
            if (button == null)
                continue;
            _hooked.Add(tool);
            // The tools sit in a row with hairline gaps; each answers out to the middle of its gap.
            button.HitOutset.Value = ToolGap * 0.5f;
            var hovered = tool;
            button.HoverEntered += _ => ShowTip(hovered);
            button.HoverExited += _ => HideTip(hovered);
        }
    }

    public void ShowTip(HomeWidgetPreset tool)
    {
        if (_dash == null || !_toolCenters.TryGetValue(tool, out float center))
            return;
        _tipFor = tool;
        _tipOwner = null;
        _dash.ShowTipBelow(tool.Tip, center, Dashboard.IslandTop + Height + TipGap);
    }

    public void HideTip(HomeWidgetPreset tool)
    {
        if (!ReferenceEquals(_tipFor, tool))
            return;
        _tipFor = null;
        _dash?.ShowTipBelow(null, 0f, 0f);
    }

    private void ShowOwnTip(string owner, string text, Slot slot)
    {
        if (_dash == null)
            return;
        var rect = slot.GetComponent<RectTransform>()?.LocalComputeRect;
        if (rect == null)
            return;
        float center = rect.Value.x + rect.Value.width * 0.5f + _dash.Size.Value.x * 0.5f;
        _tipFor = null;
        _tipOwner = owner;
        _dash.ShowTipBelow(text, center, Dashboard.IslandTop + Height + TipGap);
    }

    private void HideOwnTip(string owner)
    {
        if (_tipOwner != owner)
            return;
        _tipOwner = null;
        _dash?.ShowTipBelow(null, 0f, 0f);
    }

    private void RefreshOverflowNames()
    {
        foreach (var (tool, row, name) in _overflow)
        {
            if (row.ActiveSelf.Value && !name.IsDestroyed)
                ListingStyle.SetText(name, tool.Tip);
        }
    }

    // PROFILE

    private void PressProfile()
    {
        var account = Account;
        if (account == null)
            return;
        if (!account.IsSignedIn)
        {
            SetPopover(false);
            if (_dash?.DeskScreen is HomeScreen home)
                home.OpenSignIn();
            else
                account.OpenSignInDialog();
            return;
        }
        SetPopover(!PopoverOpen);
    }

    private void RefreshProfile()
    {
        var account = Account;
        bool signedIn = account?.IsSignedIn == true;
        if (signedIn)
            account!.PollStorage();
        else
            HideOwnTip("account");
        RefreshStorage(account, signedIn);
        // The card grows a row when the account has group storage to show.
        if (account != null && account.CardHeight != _accountHeight)
        {
            _accountHeight = account.CardHeight;
            PlacePopover(_accountPopover, Dashboard.Outer, AccountWidth, _accountHeight);
        }
        if (!signedIn && PopoverOpen)
            SetPopover(false);
        string name = !signedIn ? "Sign in"
            : string.IsNullOrEmpty(account!.ProfileName) ? "Account" : account.ProfileName;
        if (_signedShown == signedIn && string.Equals(_nameShown, name, StringComparison.Ordinal))
            return;
        _signedShown = signedIn;
        _nameShown = name;
        if (_profileName != null && !_profileName.IsDestroyed)
        {
            SettingsUI.SetAnchors(SettingsUI.Rect(_profileName.Slot), new float2(0f, signedIn ? 0.42f : 0f), float2.One);
            ListingStyle.SetText(_profileName, name);
            ListingStyle.SetTextColor(_profileName, signedIn ? DashTheme.Text : DashTheme.TextDim);
        }
        PaintProfile();
    }

    private void RefreshStorage(AccountWidgetPreset? account, bool signedIn)
    {
        if (_profileStorage == null || _storageRing == null)
            return;
        SetActive(_profileStorage.Slot, signedIn);
        bool known = signedIn && account?.HasStorageUsage == true;
        SetActive(_storageRing.Slot, known);
        if (!signedIn || account == null)
            return;
        float fraction = account.StorageFraction;
        var tint = fraction >= 1f ? DashTheme.Negative : fraction >= 0.85f ? DashTheme.Warning : DashTheme.Accent;
        ListingStyle.SetText(_profileStorage, account.StorageLabel);
        ListingStyle.SetTextColor(_profileStorage, known && fraction >= 0.85f ? tint : DashTheme.TextDim);
        float arc = 360f * fraction;
        if (_storageRing.ArcLength.Value != arc)
            _storageRing.ArcLength.Value = arc;
        if (!_storageRing.Tint.Value.Equals(tint))
            _storageRing.Tint.Value = tint;
    }

    private void PaintProfile()
    {
        if (_profilePaint == null || _profilePaint.IsDestroyed)
            return;
        var fill = PopoverOpen ? DashTheme.AccentSoft : _profileHovered ? DashTheme.GlassCardHover : color.Transparent;
        SettingsUI.SetFill(_profilePaint, fill);
    }

    // MICROPHONE

    private string VoiceTip()
    {
        var user = World?.LocalUser;
        if (EngineSettings.MicrophoneMuted || user == null || user.ActiveVoiceMode == VoiceMode.Mute)
            return "Voice: muted";
        int index = Array.IndexOf(Modes, user.ActiveVoiceMode);
        return "Voice: " + ModeNames[index < 0 ? 1 : index];
    }

    private void RefreshMic()
    {
        if (_mic == null || _dash == null)
            return;
        var user = World?.LocalUser;
        bool muted = EngineSettings.MicrophoneMuted || user == null || user.ActiveVoiceMode == VoiceMode.Mute;
        string icon = "mute";
        if (!muted)
        {
            int index = Array.IndexOf(Modes, user!.ActiveVoiceMode);
            icon = ModeIcons[index < 0 ? 1 : index];
        }
        if (icon != _micIconShown)
        {
            _micIconShown = icon;
            var provider = _dash.IconProvider(icon);
            if (!ReferenceEquals(_mic.Glyph.Texture.Target, provider))
                _mic.Glyph.Texture.Target = provider;
        }
        PaintButton(_mic);
    }

    // The level beside the microphone, so you can see you are heard without opening anything. The capture device only
    // runs while something listens, so the bar holds a subscription of its own while the dash is up and the
    // microphone is on; muted, nothing is held open for a meter.
    private void Meter(float step)
    {
        var user = World?.LocalUser;
        bool muted = EngineSettings.MicrophoneMuted || user == null || user.ActiveVoiceMode == VoiceMode.Mute;
        SetMetering(Slot.IsActive && !muted);
        float peak = _metered?.LastPeak ?? 0f;
        float target = muted ? 0f : MathF.Min(1f, MathF.Sqrt(MathF.Max(0f, peak)));
        _level = target >= _level ? target : MathF.Max(target, _level - 4f * step);
        _peak = muted ? 0f : MathF.Max(_level, _peak - 0.6f * step);
        if (muted)
            _level = 0f;
        if (_micLevel != null && !_micLevel.IsDestroyed && MathF.Abs(_level - _shownLevel) > 0.01f)
        {
            _shownLevel = _level;
            _micLevel.AnchorMax.Value = new float2(_level, 1f);
        }
        if (_micPeak != null && !_micPeak.IsDestroyed && MathF.Abs(_peak - _shownPeak) > 0.01f)
        {
            _shownPeak = _peak;
            _micPeak.AnchorMin.Value = new float2(_peak, 0f);
            _micPeak.AnchorMax.Value = new float2(_peak, 1f);
            SettingsUI.SetOffsets(_micPeak, new float2(-PeakWidth * _peak, 0f), new float2(PeakWidth * (1f - _peak), 0f));
        }
    }

    private void SetMetering(bool on)
    {
        var wanted = on ? Engine.Current?.AudioSystem?.DefaultAudioInput : null;
        if (ReferenceEquals(wanted, _metered))
            return;
        _metered?.RemoveSubscriber();
        _metered = wanted;
        _metered?.AddSubscriber();
    }

    // LEAVE

    private static World? FocusedWorld
    {
        get
        {
            var world = Engine.Current?.WorldManager?.FocusedWorld;
            return world != null && !world.IsDestroyed ? world : null;
        }
    }

    private static bool IsLocalHome(World? world)
        => world != null && !world.IsDestroyed && world.WorldName?.Value == "LocalHome";

    private void RefreshLeave()
    {
        if (_leave == null)
            return;
        var world = FocusedWorld;
        bool live = world != null && !IsLocalHome(world);
        if (_leaveKnown && live == _leaveLive)
            return;
        _leaveKnown = true;
        _leaveLive = live;
        ListingStyle.SetInteractable(_leave.Button, live);
        PaintButton(_leave);
    }

    // The confirm, the fallback world and the focus hand-off all belong to the Worlds screen.
    private void Leave()
    {
        var world = FocusedWorld;
        var dash = _dash;
        if (world == null || dash == null || IsLocalHome(world))
            return;
        foreach (var screen in dash.Screens)
        {
            if (screen is WorldsScreen worlds)
            {
                worlds.CloseWorld(world);
                return;
            }
        }
    }
}
