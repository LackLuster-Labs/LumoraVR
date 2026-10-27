// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Chat;

// THE CHAT BOX, on a screen
//
// The focused world's chat in the bottom left corner of the window. Open (Open Chat, G as shipped) it is a
// pane of glass with the history on it, newest at the bottom, and a field under it with the keyboard: Enter
// sends and stays open, Escape drops what is typed, and Escape or Enter with nothing typed closes it. Shut,
// the last few lines stand there on their own for a while after each new one and then fade out.
//
// While the field has the keyboard nothing else hears a key: a focused field gates the whole keyboard off
// the action map (TextFocusHeld), which takes walking, the tool keys and the dash with it.
//
// Drawn the way the dash is on a screen: a Helio canvas in this world's hidden band, captured by its own
// offscreen camera only when it changed, and laid over the window by the platform (ChatBoxOverlay) rather
// than hung in front of the camera, so nothing about the view can make it swim. Never in a headset.
//
// Open, it takes the mouse too: the cursor goes free and the view holds still, and a name in the history is a
// way to that person's profile like a name anywhere else (ProfileLink). The box is a picture on the window
// that no ray can reach, so the platform says where it put it (SetScreenRect) and a portal in userspace
// (ChatBoxPortal) turns the cursor's ray into a point on the canvas. A press anywhere in the box keeps the
// field; a press out in the world is a click away and shuts it. Opened from a pad the cursor stays locked,
// as the context menu leaves it. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class DesktopChatBox : Component, IKeepsTextFocus
{
    // The canvas, in pixels at a 1080-line window. The platform scales it with the window.
    public const float CanvasWidth = 460f;
    public const float CanvasHeight = 300f;
    private const float CanvasScale = 0.001f;
    private const float CaptureDistance = 1f;
    private const int Supersample = 2;
    // Far from the dash's rig, so neither capture sees the other.
    private static readonly float3 RigWorldPosition = new float3(0f, 1100f, 0f);

    private const float Inset = 10f;
    private const float FieldHeight = 36f;
    private const float FieldGap = 8f;
    private const float RowSpacing = 4f;
    private const float RowPadX = 10f;
    private const float RowPadY = 5f;
    private const float FontSize = DashTheme.FontBody;
    private const int CollapsedRows = 4;
    private const int ScrollStep = 3;
    private const float FeedHeight = CanvasHeight - Inset * 2f - FieldHeight - FieldGap;
    private const float RowTextWidth = CanvasWidth - Inset * 2f - RowPadX * 2f;

    // Shut, a new line keeps the box up this long, then it fades over the next.
    private const float ShowSeconds = 9f;
    private const float FadeSeconds = 1.5f;

    // A field with text in it and no key for this long stops saying you are typing.
    private const float TypingIdleSeconds = 8f;

    // A name takes presses where it is drawn and this much past its end.
    private const float NameSlack = 3f;
    private const float TipHeight = 24f;
    private const float TipPadX = 9f;
    private const float TipGap = 2f;
    // How far in front of the canvas the pointer's ray starts, in metres at the rig.
    private const float PointerReach = 0.5f;
    private const UIInteractionSource PointerSource = UIInteractionSource.Desktop;

    private static readonly color RowFill = DashTheme.GlassCard;
    private static readonly color OwnRowFill = new color(DashTheme.Accent.r, DashTheme.Accent.g, DashTheme.Accent.b, 0.26f);

    public static DesktopChatBox? LocalInstance { get; private set; }

    // Holds the capture so it is made at all: a provider only makes its asset while something refers to it, and
    // the window composite reads the texture straight off the provider rather than through a material.
    public readonly AssetRef<TextureAsset> Capture;

    private Slot? _rig;
    private Slot? _canvasSlot;
    private Canvas? _canvas;
    private RenderTextureProvider? _capture;
    private FontProvider? _font;
    private Slot? _glass;
    private Slot? _rowsSlot;
    private Slot? _fieldSlot;
    private TextInput? _input;
    private Text? _hint;
    private Slot? _tipSlot;
    private RectTransform? _tipRect;
    private Text? _tipText;
    private bool _built;

    private sealed class Row
    {
        public Slot Slot = null!;
        public LayoutElement Element = null!;
        public RoundedPanel Panel = null!;
        public Text Text = null!;
        public RectTransform NameRect = null!;
        public Text Name = null!;
        public ProfileLink Link = null!;
    }

    private readonly List<Row> _rows = new();
    private readonly List<WorldChatLine> _visibleLines = new();
    private readonly List<float> _visibleHeights = new();
    private readonly TextShaper _shaper = new();
    private readonly StringBuilder _builder = new(256);

    private World? _world;
    private WorldChat? _chat;
    private int _shownVersion = -1;
    private int _shownScroll = -1;
    private bool _shownOpen;
    private int _shownFontGeneration = -1;
    private int _lastFontGeneration = -1;
    private WorldChatLine? _newest;

    private bool _open;
    private bool _focusPending;
    private bool _closing;
    private int _scrollBack;
    private double _lastActivity = double.NegativeInfinity;
    private string _lastDraft = string.Empty;
    private double _lastKeyAt;
    private bool _typingSent;
    private bool _wasShown;
    private RenderTexture? _appliedAsset;
    private bool _appliedRender;

    private bool _freesCursor;
    private bool _holdsPointer;
    private UserInputState? _heldState;
    private bool _heldInput;
    private int _pointerId = -1;
    private float _wheel;

    // Where the platform drew the box, as shares of the window, top left first.
    private float2 _screenMin;
    private float2 _screenSize;

    private ProfileLink? _tipFor;
    private double _tipSince;
    private bool _tipDismissed;

    public DesktopChatBox()
    {
        Capture = new AssetRef<TextureAsset>(this);
    }

    public RenderTextureProvider? RenderTextureSource => _capture;
    public float2 CanvasSize => new float2(CanvasWidth, CanvasHeight);
    public bool IsOpen => _open;
    public bool IsShown { get; private set; }
    public float Opacity { get; private set; }
    public TextInput? Input => _input;
    public WorldChat? Chat => _chat;

    // What the box holds right now, oldest first, for anything checking it from outside.
    public IReadOnlyList<WorldChatLine> VisibleLines => _visibleLines;

    // Whether the box has the cursor loose for itself, and whether the cursor's ray reaches it now.
    public bool HoldsPointer => _holdsPointer;
    public bool TakesPointer
    {
        get
        {
            var input = Engine.Current?.InputInterface;
            return _built && _open && IsShown && _screenSize.x > 0f && _screenSize.y > 0f && input != null
                && !input.IsVRActive && !input.IsDashboardOpen && input.CursorFree && input.DesktopCursorRayValid
                && input.DesktopCameraPoseValid;
        }
    }

    // The tip showing over the history, null when none is.
    public string? ShownTip => _tipSlot != null && _tipSlot.ActiveSelf.Value ? _tipText?.Content.Value : null;

    public override void OnStart()
    {
        base.OnStart();
        LocalInstance = this;
        Build();
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(LocalInstance, this))
            LocalInstance = null;
        if (_typingSent)
            _chat?.SetTyping(false);
        HoldPointer(false);
        base.OnDestroy();
    }

    public void Open()
    {
        if (_open || !_built)
            return;
        _open = true;
        _focusPending = true;
        _scrollBack = 0;
        _lastActivity = World.Time.TotalTime;
        _freesCursor = Engine.Current?.InputInterface?.Actions?.HintDevice != InputDeviceKind.Gamepad;
    }

    // The platform's placement of the box on the window, as shares of its width and height from the top left.
    public void SetScreenRect(float left, float top, float width, float height)
    {
        _screenMin = new float2(left, top);
        _screenSize = new float2(width, height);
    }

    public void Close()
    {
        HoldPointer(false);
        if (!_open)
            return;
        _open = false;
        _focusPending = false;
        _scrollBack = 0;
        if (_input != null && _input.IsFocused)
        {
            _closing = true;
            try
            {
                _input.Unfocus();
            }
            finally
            {
                _closing = false;
            }
        }
        ClearPoint(_pointerId);
        HideTip();
        _lastActivity = World.Time.TotalTime;
    }

    // THE MOUSE

    // Open, the cursor goes loose and the view stops following the mouse, as the context menu does. Held on
    // the input state it was taken on, so a world switch while open lets go of the right one.
    private void HoldPointer(bool hold)
    {
        if (hold == _holdsPointer)
            return;
        _holdsPointer = hold;
        var input = Engine.Current?.InputInterface;
        if (hold)
        {
            input?.SetCursorFree(this, true);
            var state = _world?.LocalUser?.Root?.Slot?.GetComponent<UserInputState>() ?? UserInputState.ForFocusedLocalUser;
            _heldState = state;
            if (state == null)
                return;
            if (input?.DesktopExternalCameraAim == true)
            {
                state.SetDesktopInputSuppressed(this, true);
                _heldInput = true;
            }
            else
            {
                // The view's own hold, not the third-person look flag: a camera switch while the box is open
                // must not leave either one stuck. -xlinka
                state.SetViewHeld(this, true);
            }
            return;
        }

        input?.SetCursorFree(this, false);
        var held = _heldState;
        _heldState = null;
        if (_heldInput)
        {
            _heldInput = false;
            if (held is { IsDestroyed: false })
                held.SetDesktopInputSuppressed(this, false);
        }
        if (held is { IsDestroyed: false })
            held.SetViewHeld(this, false);
    }

    // Where a ray from the desktop camera crosses the window, as a point on the canvas (its centre at zero, y
    // up). False for a ray turned away from the view; inside says whether the point is on the box.
    internal bool TryCanvasPoint(in float3 direction, out float2 point, out bool inside)
    {
        point = default;
        inside = false;
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.DesktopCameraPoseValid || !(_screenSize.x > 0f) || !(_screenSize.y > 0f))
            return false;
        var local = input.DesktopCameraRotation.Inverse * direction;
        if (!(local.z < -1e-5f))
            return false;
        float tanY = System.MathF.Tan(input.DesktopCameraFovY * (System.MathF.PI / 180f) * 0.5f);
        float tanX = tanY * input.DesktopViewportAspect;
        if (!(tanY > 0f) || !(tanX > 0f))
            return false;
        float x = local.x / -local.z / tanX;
        float y = local.y / -local.z / tanY;
        float across = ((x + 1f) * 0.5f - _screenMin.x) / _screenSize.x;
        float down = ((1f - y) * 0.5f - _screenMin.y) / _screenSize.y;
        if (!float.IsFinite(across) || !float.IsFinite(down))
            return false;
        point = new float2((across - 0.5f) * CanvasWidth, (0.5f - down) * CanvasHeight);
        inside = across >= 0f && across <= 1f && down >= 0f && down <= 1f;
        return true;
    }

    // A pointer on the canvas at a point, from in front of it, the way the dash feeds its own.
    internal void Point(int pointerId, in float2 point, bool pressed)
    {
        var canvas = _canvas;
        var slot = _canvasSlot;
        if (canvas == null || slot == null || canvas.IsDestroyed || slot.IsDestroyed)
            return;
        if (_pointerId >= 0 && _pointerId != pointerId)
            ClearPoint(_pointerId);
        _pointerId = pointerId;
        var at = slot.LocalPointToGlobal(new float3(point.x, point.y, 0f));
        var forward = slot.Forward;
        canvas.UpdatePointer(PointerSource, pointerId, at + forward * PointerReach, -forward, pressed, World?.LocalUser);
    }

    internal void ClearPoint(int pointerId)
    {
        if (pointerId < 0 || pointerId != _pointerId)
            return;
        _pointerId = -1;
        if (_canvas is { IsDestroyed: false } canvas)
            canvas.ClearPointer(PointerSource, pointerId, World?.LocalUser);
    }

    // The wheel over the history, open only: up goes back.
    internal void Wheel(float notches)
    {
        if (!_open || !float.IsFinite(notches))
            return;
        _wheel += notches;
        int whole = (int)_wheel;
        if (whole == 0)
            return;
        _wheel -= whole;
        _scrollBack = System.Math.Max(0, _scrollBack + whole * ScrollStep);
    }

    // BUILD

    private void Build()
    {
        if (_built || Slot == null || Slot.IsDestroyed)
            return;
        _built = true;

        var fonts = Slot.AddSlot("Fonts");
        _font = fonts.AttachComponent<FontProvider>();
        _font.URL.Value = DashTheme.FontRegular;
        _font.FallbackURLs.Add(DashTheme.FontMono);

        _rig = Slot.AddSlot("ChatRenderRig");
        _rig.Persistent.Value = false;
        _rig.GlobalPosition = RigWorldPosition;
        _rig.GlobalRotation = floatQ.Identity;
        _rig.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.HiddenLayer;
        _rig.ActiveSelf.Value = false;

        _canvasSlot = _rig.AddSlot("ChatCanvas");
        _canvasSlot.LocalScale.Value = float3.One * CanvasScale;
        var rootRect = _canvasSlot.AttachComponent<RectTransform>();
        rootRect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rootRect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rootRect.OffsetMin.Value = new float2(CanvasWidth * -0.5f, CanvasHeight * -0.5f);
        rootRect.OffsetMax.Value = new float2(CanvasWidth * 0.5f, CanvasHeight * 0.5f);
        _canvas = _canvasSlot.AttachComponent<Canvas>();

        // The pane only while open. Shut, each line stands on its own card.
        _glass = SettingsUI.Fill(_canvasSlot, "Glass");
        SettingsUI.Panel(_glass, DashTheme.GlassWindow, DashTheme.OutlineStrong, DashTheme.RadiusPanel);
        _glass.ActiveSelf.Value = false;

        // Their own chunks, over the pane: inside one chunk a rounded panel can land over what follows it.
        var feed = SettingsUI.Fill(_canvasSlot, "Feed", Inset, Inset + FieldHeight + FieldGap, Inset, Inset);
        feed.AttachComponent<GraphicChunkRoot>();
        feed.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        _rowsSlot = SettingsUI.Fill(feed, "Rows");
        var column = _rowsSlot.AttachComponent<VerticalLayout>();
        column.Spacing.Value = RowSpacing;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;
        column.MainAlignment.Value = MainAxisAlignment.End;

        _fieldSlot = SettingsUI.Child(_canvasSlot, "Field", new float2(0f, 0f), new float2(1f, 0f),
            new float2(Inset, Inset), new float2(-Inset, Inset + FieldHeight));
        _fieldSlot.AttachComponent<GraphicChunkRoot>();
        _input = DashTextField.Attach(_fieldSlot, _font, FontSize, string.Empty, ChatText.MaxLength);
        _input.FocusLost += OnFocusLost;
        _input.EditingFinished += OnSubmitted;
        _hint = SettingsUI.Label(_fieldSlot, "Hint", _font, DashTheme.FontSmall, DashTheme.TextMuted, TextHorizontalAlignment.Left,
            float2.Zero, float2.One, new float2(DashTextField.TextInset, 0f), new float2(-DashTextField.TextInset, 0f));
        _hint.Content.Value = ChatLocale.Hint.Resolve();
        _hint.InteractionTarget.Value = false;
        _fieldSlot.ActiveSelf.Value = false;

        // Solid rather than glass, over the rows: through glass the line under it reads as a ghost behind the
        // words. A band of its own, since two nested chunks on one band draw in no set order.
        _tipSlot = SettingsUI.Child(_canvasSlot, "Tip", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f), float2.Zero, float2.Zero);
        _tipRect = _tipSlot.GetComponent<RectTransform>();
        _tipSlot.AttachComponent<GraphicChunkRoot>().OverlayLevel = 1;
        SettingsUI.Panel(_tipSlot, DashTheme.Panel, DashTheme.OutlineStrong, DashTheme.RadiusChip);
        _tipText = SettingsUI.Label(_tipSlot, "Text", _font, DashTheme.FontSmall, DashTheme.Text, TextHorizontalAlignment.Center,
            float2.Zero, float2.One, float2.Zero, float2.Zero);
        _tipText.Ellipsis.Value = false;
        _tipText.InteractionTarget.Value = false;
        _tipSlot.ActiveSelf.Value = false;

        Slot.AddSlot("Pointer").AttachComponent<ChatBoxPortal>();

        _capture = _rig.AttachComponent<RenderTextureProvider>();
        _capture.Size.Value = new int2((int)CanvasWidth * Supersample, (int)CanvasHeight * Supersample);
        // A clear capture: the UI comes out with its colour multiplied by its own coverage, which is what the
        // window composite expects.
        _capture.ClearColor.Value = color.Transparent;
        _capture.CullMask.Value = RenderLayerOverride.HiddenLayer;
        _capture.OrthographicSize.Value = CanvasHeight * CanvasScale;
        _capture.CameraPosition.Value = RigWorldPosition + new float3(0f, 0f, CaptureDistance);
        _capture.CameraRotation.Value = floatQ.Identity;
        Capture.Target = _capture;
    }

    // UPDATE

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!_built || World == null)
            return;

        var engine = Engine.Current;
        var input = engine?.InputInterface;
        var world = engine?.WorldManager?.FocusedWorld;
        bool desktop = input != null && !input.IsVRActive;
        bool usable = desktop && world != null && !world.IsDestroyed && !world.IsUserspace && world.LocalUser != null;
        Bind(usable ? world : null);

        double now = World.Time.TotalTime;
        if (!usable || input!.IsDashboardOpen || _chat == null)
        {
            Close();
            if (_typingSent)
            {
                _typingSent = false;
                _chat?.SetTyping(false);
            }
            ApplyShown(false, 0f);
            return;
        }

        // A frame after the key, so the letter that opened the box is not typed into it: the field reads this
        // frame's typed text in its own update, which can come after this one.
        if (_focusPending && _input != null)
        {
            _focusPending = false;
            if (_fieldSlot != null && !_fieldSlot.ActiveSelf.Value)
                _fieldSlot.ActiveSelf.Value = true;
            _input.Focus();
        }

        if (!_open && input.Actions?.Menu.OpenChat.Pressed == true && TextInput.Focused == null)
            Open();
        HoldPointer(_open && _freesCursor);

        if (_open)
            Scroll(input);
        UpdateTyping(now);

        int fontGeneration = _font?.Asset?.CacheGeneration ?? -1;
        if (_chat.Version != _shownVersion || _scrollBack != _shownScroll || _open != _shownOpen || fontGeneration != _shownFontGeneration)
            Refresh(now, fontGeneration);

        float opacity;
        if (_open)
            opacity = 1f;
        else if (!EngineSettings.ShowDesktopChat || _visibleLines.Count == 0)
            opacity = 0f;
        else
        {
            float since = (float)(now - _lastActivity);
            opacity = since <= ShowSeconds ? 1f : 1f - System.Math.Clamp((since - ShowSeconds) / FadeSeconds, 0f, 1f);
        }
        ApplyShown(opacity > 0.002f, opacity);
        UpdateTip(now);

        if (IsShown)
            PumpCapture(fontGeneration);
    }

    private void Bind(World? world)
    {
        if (ReferenceEquals(world, _world) && (world == null || _chat != null && !_chat.IsDestroyed))
            return;

        if (!ReferenceEquals(world, _world))
        {
            if (_typingSent)
                _chat?.SetTyping(false);
            _typingSent = false;
            Close();
            _world = world;
            _chat = null;
            _requestedFor = null;
            _shownVersion = -1;
            _newest = null;
            _lastActivity = double.NegativeInfinity;
        }
        if (world == null)
            return;

        // The chat is that world's: made inside its own update, never from here.
        _chat = WorldChat.Find(world);
        if (_chat == null && !ReferenceEquals(_requestedFor, world))
        {
            _requestedFor = world;
            world.RunSynchronously(() => WorldChat.For(world));
        }
    }

    private World? _requestedFor;

    private void Scroll(InputInterface input)
    {
        var keyboard = input.Keyboard;
        if (keyboard == null || _input == null || !_input.IsFocused)
            return;
        if (keyboard.IsKeyJustPressed(Key.PageUp))
            _scrollBack += ScrollStep;
        if (keyboard.IsKeyJustPressed(Key.PageDown))
            _scrollBack = System.Math.Max(0, _scrollBack - ScrollStep);
    }

    // Typing while the field holds anything and a key went down lately; every change goes to the record, and
    // nothing else does.
    private void UpdateTyping(double now)
    {
        string draft = _input?.Text.Value ?? string.Empty;
        if (!string.Equals(draft, _lastDraft, StringComparison.Ordinal))
        {
            _lastDraft = draft;
            _lastKeyAt = now;
        }
        bool typing = _open && _input != null && _input.IsFocused && draft.Trim().Length > 0
            && now - _lastKeyAt < TypingIdleSeconds;

        bool hint = _open && draft.Length == 0;
        if (_hint != null && !_hint.IsDestroyed && _hint.Slot.ActiveSelf.Value != hint)
            _hint.Slot.ActiveSelf.Value = hint;
        if (hint)
            NameHintKeys();

        if (typing == _typingSent)
            return;
        _typingSent = typing;
        _chat?.SetTyping(typing);
    }

    // The hint names the keys of whatever was pressed last, the pad's when that was the pad.
    private bool? _hintPad;

    private void NameHintKeys()
    {
        bool pad = InputHints.PadNames;
        if (_hint == null || _hint.IsDestroyed || _hintPad == pad)
            return;
        _hintPad = pad;
        var menu = Engine.Current?.InputInterface?.Actions?.Menu;
        _hint.Content.Value = pad && menu != null
            ? ChatLocale.HintPad(InputHints.Name(menu.Click), InputHints.Name(menu.Back)).Resolve()
            : ChatLocale.Hint.Resolve();
    }

    private void OnFocusLost(TextInput input, string text)
    {
        if (_closing || !_open)
            return;
        var keyboard = Engine.Current?.InputInterface?.Keyboard;
        // Enter: the submit that comes straight after this decides.
        if (keyboard != null && (keyboard.IsKeyJustPressed(Key.Return) || keyboard.IsKeyJustPressed(Key.KeypadEnter)))
            return;
        if (keyboard != null && keyboard.IsKeyJustPressed(Key.Escape) && !string.IsNullOrEmpty(text))
        {
            input.Text.Value = string.Empty;
            input.Focus();
            return;
        }
        Close();
    }

    private void OnSubmitted(TextInput input, string value)
    {
        if (_closing || !_open)
            return;
        if (ChatText.Clean(value).Length == 0)
        {
            input.Text.Value = string.Empty;
            Close();
            return;
        }
        // A full queue keeps the line in the field to go again.
        if (_chat != null && _chat.TrySend(value))
            input.Text.Value = string.Empty;
        _scrollBack = 0;
        input.Focus();
    }

    // CONTENT

    private void Refresh(double now, int fontGeneration)
    {
        if (_chat == null)
            return;
        var history = _chat.History;
        var newest = history.Count > 0 ? history[history.Count - 1] : null;
        if (!ReferenceEquals(newest, _newest))
        {
            if (newest != null && !newest.IsBlocked && _shownVersion >= 0)
                _lastActivity = now;
            _newest = newest;
        }
        _shownVersion = _chat.Version;
        _shownOpen = _open;
        _shownFontGeneration = fontGeneration;

        if (_glass != null && _glass.ActiveSelf.Value != _open)
            _glass.ActiveSelf.Value = _open;
        if (_fieldSlot != null && _fieldSlot.ActiveSelf.Value != _open && !_open)
            _fieldSlot.ActiveSelf.Value = false;

        int shown = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (!history[i].IsBlocked)
                shown++;
        }
        if (_scrollBack > System.Math.Max(0, shown - 1))
            _scrollBack = System.Math.Max(0, shown - 1);
        _shownScroll = _scrollBack;

        _visibleLines.Clear();
        _visibleHeights.Clear();
        float used = 0f;
        int skipped = 0;
        int limit = _open ? int.MaxValue : CollapsedRows;
        for (int i = history.Count - 1; i >= 0 && _visibleLines.Count < limit; i--)
        {
            var line = history[i];
            if (line.IsBlocked)
                continue;
            if (skipped < _scrollBack)
            {
                skipped++;
                continue;
            }
            float height = MeasureRow(line);
            float next = used + height + (_visibleLines.Count > 0 ? RowSpacing : 0f);
            if (next > FeedHeight && _visibleLines.Count > 0)
                break;
            _visibleLines.Insert(0, line);
            _visibleHeights.Insert(0, height);
            used = next;
        }

        for (int i = 0; i < _visibleLines.Count; i++)
            Paint(RowAt(i), _visibleLines[i], _visibleHeights[i]);
        for (int i = _visibleLines.Count; i < _rows.Count; i++)
        {
            if (_rows[i].Slot.ActiveSelf.Value)
                _rows[i].Slot.ActiveSelf.Value = false;
        }
    }

    private Row RowAt(int index)
    {
        while (_rows.Count <= index)
        {
            var slot = _rowsSlot!.AddSlot("Row");
            slot.AttachComponent<RectTransform>();
            var element = slot.AttachComponent<LayoutElement>();
            element.FlexibleHeight.Value = 0f;
            element.FlexibleWidth.Value = 1f;
            var panel = SettingsUI.Panel(slot, RowFill, DashTheme.Outline, DashTheme.RadiusControl);
            // Top down, so the name laid over the line's first one sits on it however many lines the line takes.
            // The row's two spare units split one above the text, one below.
            var text = SettingsUI.Label(slot, "Text", _font, FontSize, DashTheme.Text, TextHorizontalAlignment.Left,
                float2.Zero, float2.One, new float2(RowPadX, RowPadY), new float2(-RowPadX, -RowPadY - 1f));
            text.VerticalAlignment.Value = TextVerticalAlignment.Top;
            text.Ellipsis.Value = false;
            text.WordWrap.Value = true;
            text.RichText.Value = true;
            text.InteractionTarget.Value = false;
            var name = SettingsUI.Label(slot, "Name", _font, FontSize, DashTheme.Text, TextHorizontalAlignment.Left,
                new float2(0f, 1f), new float2(0f, 1f), new float2(RowPadX, -RowPadY - 1f - FontSize), new float2(RowPadX + 1f, -RowPadY - 1f));
            name.VerticalAlignment.Value = TextVerticalAlignment.Top;
            name.Ellipsis.Value = false;
            name.RichText.Value = true;
            name.InteractionTarget.Value = false;
            _rows.Add(new Row
            {
                Slot = slot, Element = element, Panel = panel, Text = text,
                NameRect = SettingsUI.Rect(name.Slot), Name = name, Link = ProfileLink.On(name),
            });
        }
        return _rows[index];
    }

    private void Paint(Row row, WorldChatLine line, float height)
    {
        if (!row.Slot.ActiveSelf.Value)
            row.Slot.ActiveSelf.Value = true;
        if (row.Element.MinHeight.Value != height)
            row.Element.MinHeight.Value = height;
        if (row.Element.PreferredHeight.Value != height)
            row.Element.PreferredHeight.Value = height;
        SettingsUI.SetFill(row.Panel, line.Own ? OwnRowFill : RowFill);
        string name = ChatText.Clean(line.Name);
        string content = Markup(name, ChatText.Clean(line.Text));
        if (!string.Equals(row.Text.Content.Value, content, StringComparison.Ordinal))
            row.Text.Content.Value = content;

        string shown = "<b><noparse=" + name.Length.ToString(CultureInfo.InvariantCulture) + ">" + name;
        if (!string.Equals(row.Name.Content.Value, shown, StringComparison.Ordinal))
            row.Name.Content.Value = shown;
        var size = MeasureName(name);
        var min = new float2(RowPadX, -RowPadY - 1f - size.y);
        var max = new float2(RowPadX + System.MathF.Min(size.x, RowTextWidth) + NameSlack, -RowPadY - 1f);
        if (row.NameRect.OffsetMin.Value != min)
            row.NameRect.OffsetMin.Value = min;
        if (row.NameRect.OffsetMax.Value != max)
            row.NameRect.OffsetMax.Value = max;
        row.Link.Bind(line.AccountId, line.Ink);
    }

    // The line with its name held clear: the name is drawn by a text of its own laid over the gap, which can
    // light up and take a press on its own, and the line still wraps exactly as it would with the name in it.
    // Both runs go in literal, so nothing in either can be read as a tag even if a clean somewhere missed one.
    private string Markup(string name, string text)
    {
        var builder = _builder;
        builder.Clear();
        builder.Append("<b><alpha=#00><noparse=").Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append('>').Append(name)
            .Append("</alpha></b>  <noparse=").Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append('>').Append(text);
        return builder.ToString();
    }

    // A name's width and line height off the face it is drawn with; a guess until the face is in.
    private float2 MeasureName(string name)
    {
        var font = _font?.Asset;
        if (font != null && font.IsValid)
        {
            TextShaper.RequestGlyphs(font, name, FontSize);
            _shaper.Shape(font, name, FontSize, wrap: false, maxWidth: 100000f);
            float width = _shaper.Lines.Count > 0 ? _shaper.Lines[0].Width : 0f;
            return new float2(width, font.GetLineHeight(FontSize));
        }
        return new float2(name.Length * FontSize * 0.6f, FontSize * 1.3f);
    }

    // TIP

    // "View profile" over a name the pointer has rested on, as the beam's own label says it out in the world:
    // the beam's label is drawn in the room, under this picture on the window, so the box draws its own. Gone
    // on a press until the pointer goes to another name.
    private void UpdateTip(double now)
    {
        Row? hovered = null;
        if (_open && IsShown)
        {
            for (int i = 0; i < _visibleLines.Count && i < _rows.Count && hovered == null; i++)
            {
                var row = _rows[i];
                if (!row.Link.IsLive)
                    continue;
                foreach (var button in row.Link.Buttons)
                {
                    if (!button.IsDestroyed && button.CanInteract && button.IsHovering.Value)
                    {
                        hovered = row;
                        break;
                    }
                }
            }
        }

        if (!ReferenceEquals(hovered?.Link, _tipFor))
        {
            _tipFor = hovered?.Link;
            _tipSince = now;
            _tipDismissed = false;
        }
        if (hovered == null)
        {
            HideTip();
            return;
        }
        foreach (var button in hovered.Link.Buttons)
        {
            if (!button.IsDestroyed && button.IsPressed.Value)
                _tipDismissed = true;
        }
        string tip = hovered.Link.Buttons.Count > 0 ? hovered.Link.Buttons[0].Tip.Value ?? string.Empty : string.Empty;
        if (_tipDismissed || tip.Length == 0 || !EngineSettings.Tooltips || now - _tipSince < EngineSettings.TooltipDelay)
        {
            HideTip();
            return;
        }
        ShowTip(hovered, tip);
    }

    private void ShowTip(Row row, string tip)
    {
        var slot = _tipSlot;
        var rect = _tipRect;
        var text = _tipText;
        var root = _canvasSlot?.GetComponent<RectTransform>();
        if (slot == null || rect == null || text == null || root == null)
            return;
        var name = row.NameRect.LocalComputeRect;
        var card = SettingsUI.Rect(row.Slot).LocalComputeRect;
        var canvas = root.LocalComputeRect;
        if (name.IsEmpty || card.IsEmpty || canvas.IsEmpty)
        {
            HideTip();
            return;
        }

        float width = MeasureTip(tip) + TipPadX * 2f;
        float left = System.Math.Clamp(name.xMin - 2f, canvas.xMin + 2f, System.MathF.Max(canvas.xMin + 2f, canvas.xMax - 2f - width));
        // Just above the name's card, or under it where the top of the box leaves no room.
        float bottom = card.yMax + TipGap;
        if (bottom + TipHeight > canvas.yMax - 2f)
            bottom = card.yMin - TipGap - TipHeight;
        var centre = canvas.Center;
        var min = new float2(left - centre.x, bottom - centre.y);
        var max = new float2(left + width - centre.x, bottom + TipHeight - centre.y);
        if (rect.OffsetMin.Value != min)
            rect.OffsetMin.Value = min;
        if (rect.OffsetMax.Value != max)
            rect.OffsetMax.Value = max;
        if (!string.Equals(text.Content.Value, tip, StringComparison.Ordinal))
            text.Content.Value = tip;
        if (!slot.ActiveSelf.Value)
            slot.ActiveSelf.Value = true;
    }

    private void HideTip()
    {
        if (_tipSlot != null && !_tipSlot.IsDestroyed && _tipSlot.ActiveSelf.Value)
            _tipSlot.ActiveSelf.Value = false;
    }

    private float MeasureTip(string tip)
    {
        var font = _font?.Asset;
        if (font == null || !font.IsValid)
            return tip.Length * DashTheme.FontSmall * 0.55f;
        TextShaper.RequestGlyphs(font, tip, DashTheme.FontSmall);
        _shaper.Shape(font, tip, DashTheme.FontSmall, wrap: false, maxWidth: 100000f);
        return _shaper.Lines.Count > 0 ? _shaper.Lines[0].Width : tip.Length * DashTheme.FontSmall * 0.55f;
    }

    // A row's height off the same shaper the text draws with, so the stack is exact the frame a line lands
    // instead of waiting on a layout pass. Without the face yet, a generous guess.
    private float MeasureRow(WorldChatLine line)
    {
        string plain = ChatText.Clean(line.Name) + "  " + ChatText.Clean(line.Text);
        var font = _font?.Asset;
        int lines;
        float lineHeight;
        if (font != null && font.IsValid)
        {
            TextShaper.RequestGlyphs(font, plain, FontSize);
            // A little narrower than the row: the name is bold and runs wider than it shapes here.
            _shaper.Shape(font, plain, FontSize, wrap: true, maxWidth: RowTextWidth - 8f);
            lines = System.Math.Max(1, _shaper.Lines.Count);
            lineHeight = font.GetLineHeight(FontSize);
        }
        else
        {
            int perLine = System.Math.Max(8, (int)(RowTextWidth / (FontSize * 0.5f)));
            lines = System.Math.Max(1, (plain.Length + perLine - 1) / perLine);
            lineHeight = FontSize * 1.3f;
        }
        return lines * lineHeight + RowPadY * 2f + 2f;
    }

    // SHOWING

    private void ApplyShown(bool shown, float opacity)
    {
        Opacity = shown ? opacity : 0f;
        IsShown = shown;
        if (_rig != null && !_rig.IsDestroyed && _rig.ActiveSelf.Value != shown)
            _rig.ActiveSelf.Value = shown;
        // A parked rig coming back does not dirty its canvas by itself.
        if (shown && !_wasShown)
            _canvas?.MarkDirty();
        _wasShown = shown;

        // The asset can turn up a few frames after the first show, so this follows the asset, not the edge.
        var asset = _capture?.Asset;
        if (asset == null || (ReferenceEquals(asset, _appliedAsset) && _appliedRender == shown))
            return;
        _appliedAsset = asset;
        _appliedRender = shown;
        asset.SetRenderEnabled(shown);
        if (shown)
            asset.RequestRender();
    }

    // The capture renders only when the canvas changed, and once more after new glyphs reach the atlas.
    private void PumpCapture(int fontGeneration)
    {
        var canvas = _canvas;
        var asset = _capture?.Asset;
        if (canvas == null || asset == null)
            return;
        if (canvas.ConsumeRenderRequested())
            asset.RequestRender();
        if (fontGeneration != _lastFontGeneration && !canvas.IsRebuildInProgress)
        {
            _lastFontGeneration = fontGeneration;
            asset.RequestRender();
        }
    }
}
