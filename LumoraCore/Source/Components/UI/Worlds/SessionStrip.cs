// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Network;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI.Worlds;

// The sideways run of sessions under a world's detail view: one card per host of that world, arrows at
// the ends, a slim bar under it, and the wheel mapped onto x so a mouse over the strip moves it.
//
// The cards are keyed by session id and pooled, because this is looking at exactly the data that
// re-announces itself once a second. A session that goes away takes its card with it, and if it was
// the selected one the selection moves to whatever is still there rather than pointing at nothing.
// -xlinka
internal sealed class SessionStrip
{
    public Func<SessionListEntry, Lumora.Core.Assets.IAssetProvider<Lumora.Core.Assets.TextureAsset>?>? ThumbnailFor;
    public Action<string>? ForgetThumbnail;
    public const float CardWidth = 300f;
    public const float CardHeight = 150f;
    private const float CardGap = 12f;
    private const float ArrowWidth = 30f;
    private const float BarHeight = 8f;
    private const float BarGap = 8f;
    private const float MinHandleWidth = 36f;

    // Same "a fresh rect is 100x100 until the canvas lays it out" floor the other scrollers use: sizing
    // the handle against that default computes a bogus range.
    private const float UnlaidViewportFloor = 100f;

    public readonly Slot Root;
    public Action<SessionListEntry>? SessionPicked;
    public Action<SessionListEntry>? JoinRequested;

    private readonly BrowserParts _parts;
    private readonly ScrollRect _scroll;
    private readonly RectTransform _viewportRect;
    private readonly Slot _contentSlot;
    private readonly RectTransform _contentRect;
    private readonly PillButton _left;
    private readonly PillButton _right;
    private readonly Slot _barSlot;
    private readonly RectTransform _handleRect;
    private readonly Slot _emptySlot;

    private readonly Dictionary<string, SessionCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    private float _pressX;
    private float _pressScroll;
    private float _handleTarget;
    private bool _handleDragging;
    private int _sizeRetries;

    public string? SelectedId { get; private set; }

    public SessionStrip(BrowserParts parts, Slot parent)
    {
        _parts = parts;
        Root = parent.AddSlot("SessionStrip");
        Root.AttachComponent<RectTransform>();
        BrowserParts.Height(Root, CardHeight + BarGap + BarHeight);
        var column = Root.AttachComponent<VerticalLayout>();
        column.Spacing.Value = BarGap;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        var row = Root.AddSlot("Row");
        row.AttachComponent<RectTransform>();
        BrowserParts.Height(row, CardHeight);
        var rowLayout = row.AttachComponent<HorizontalLayout>();
        rowLayout.Spacing.Value = DashTheme.Gap;
        rowLayout.ForceExpandWidth.Value = false;
        rowLayout.ForceExpandHeight.Value = true;

        _left = _parts.AddButton(row, "‹", ArrowWidth, CardHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Bold, () => Page(-1));
        _left.Text.Size.Value = DashTheme.FontTitle;

        var viewport = row.AddSlot("Viewport");
        _viewportRect = viewport.AttachComponent<RectTransform>();
        BrowserParts.Flex(viewport, 1f, 1f);
        viewport.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        _scroll = viewport.AttachComponent<ScrollRect>();
        // Each card carries a Join.
        _scroll.RowsActOnRelease.Value = true;
        // The strip only travels sideways, so give the wheel somewhere to go: four notches of the
        // vertical step per notch puts a card and a bit under each click of the wheel.
        _scroll.WheelScrollsHorizontal.Value = true;
        _scroll.ScrollSensitivity.Value = new float2(1f, 4f);
        _scroll.ScrollChanged += (_, _) => UpdateBar();

        _contentSlot = viewport.AddSlot("Content");
        _contentRect = _contentSlot.AttachComponent<RectTransform>();
        // Left-anchored full-height strip whose WIDTH is written by SizeContent from the card count.
        // Nothing else may touch this rect: ScrollRect owns the position through the chunk offset.
        _contentRect.AnchorMin.Value = new float2(0f, 0f);
        _contentRect.AnchorMax.Value = new float2(0f, 1f);
        _contentRect.OffsetMin.Value = float2.Zero;
        _contentRect.OffsetMax.Value = new float2(CardWidth, 0f);
        var contentLayout = _contentSlot.AttachComponent<HorizontalLayout>();
        contentLayout.Spacing.Value = CardGap;
        contentLayout.ForceExpandWidth.Value = false;
        contentLayout.ForceExpandHeight.Value = true;
        _scroll.Content.Target = _contentRect;

        _emptySlot = viewport.AddSlot("Empty");
        BrowserParts.Fill(_emptySlot.AttachComponent<RectTransform>());
        _parts.Label(_emptySlot, "No sessions right now.", DashTheme.FontBody, DashTheme.TextMuted,
            BrowserParts.Weight.Regular, TextHorizontalAlignment.Center);
        _emptySlot.ActiveSelf.Value = false;

        _right = _parts.AddButton(row, "›", ArrowWidth, CardHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text,
            BrowserParts.Weight.Bold, () => Page(1));
        _right.Text.Size.Value = DashTheme.FontTitle;

        _barSlot = Root.AddSlot("Bar");
        _barSlot.AttachComponent<RectTransform>();
        _barSlot.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Height(_barSlot, BarHeight);
        BrowserParts.Panel(_barSlot, DashTheme.Field, BarHeight * 0.5f);

        var handle = _barSlot.AddSlot("Handle");
        _handleRect = handle.AttachComponent<RectTransform>();
        _handleRect.AnchorMin.Value = new float2(0f, 0f);
        _handleRect.AnchorMax.Value = new float2(0f, 1f);
        _handleRect.OffsetMin.Value = float2.Zero;
        _handleRect.OffsetMax.Value = new float2(MinHandleWidth, 0f);
        var handlePanel = BrowserParts.Panel(handle, DashTheme.TextMuted, BarHeight * 0.5f);
        var interaction = DashScrollbar.CreateHandleInteraction(handle, handlePanel.Color);
        interaction.Pressed += OnHandlePress;
        interaction.Dragged += OnHandleDrag;
        interaction.Released += OnHandleRelease;
    }

    public void SetActive(bool active) => BrowserParts.SetActive(Root, active);

    public SessionCard? Selected => SelectedId != null && _cards.TryGetValue(SelectedId, out var card) ? card : null;

    // Walk the group's sessions into the pool: reuse what is already there, append what is new, and
    // drop what has gone. Insertion order is deliberately kept - re-sorting on a live update is what
    // makes cards swap places under the cursor mid-click. -xlinka
    public void Apply(IReadOnlyList<SessionListEntry> sessions)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            bool stillThere = false;
            for (int j = 0; j < sessions.Count; j++)
            {
                if (string.Equals(sessions[j].SessionId, _order[i], StringComparison.OrdinalIgnoreCase))
                {
                    stillThere = true;
                    break;
                }
            }
            if (stillThere)
                continue;
            if (_cards.TryGetValue(_order[i], out var dead))
            {
                dead.Destroy();
                _cards.Remove(_order[i]);
                ForgetThumbnail?.Invoke(_order[i]);
            }
            _order.RemoveAt(i--);
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            var entry = sessions[i];
            if (string.IsNullOrEmpty(entry.SessionId))
                continue;
            if (!_cards.TryGetValue(entry.SessionId, out var card))
            {
                card = new SessionCard(_parts, _contentSlot);
                _cards[entry.SessionId] = card;
                _order.Add(entry.SessionId);
                var captured = entry.SessionId;
                card.Activate = () => Select(captured, notify: true);
                card.Join = () =>
                {
                    Select(captured, notify: true);
                    if (card.Entry != null)
                        JoinRequested?.Invoke(card.Entry);
                };
            }
            card.Apply(entry);
            card.ApplyArt(ThumbnailFor?.Invoke(entry));
        }

        // The selection has to survive a session vanishing: fall through to the first card that is
        // still standing rather than leaving the primary button pointed at nothing.
        if (SelectedId == null || !_cards.ContainsKey(SelectedId))
            Select(_order.Count > 0 ? _order[0] : null, notify: false);
        else
            PaintSelection();

        BrowserParts.SetActive(_emptySlot, _order.Count == 0);
        BrowserParts.SetActive(_barSlot, _order.Count > 0);
        SizeContent();
    }

    public void Select(string? sessionId, bool notify)
    {
        SelectedId = sessionId;
        PaintSelection();
        if (notify && sessionId != null && _cards.TryGetValue(sessionId, out var card) && card.Entry != null)
            SessionPicked?.Invoke(card.Entry);
    }

    public void Clear()
    {
        foreach (var id in _order) ForgetThumbnail?.Invoke(id);
        foreach (var card in _cards.Values)
            card.Destroy();
        _cards.Clear();
        _order.Clear();
        SelectedId = null;
        _scroll.NormalizedPosition = float2.Zero;
    }

    private void PaintSelection()
    {
        foreach (var pair in _cards)
            pair.Value.SetSelected(string.Equals(pair.Key, SelectedId, StringComparison.OrdinalIgnoreCase));
    }

    private void Page(int direction)
    {
        float step = CardWidth + CardGap;
        var absolute = _scroll.AbsolutePosition;
        _scroll.AbsolutePosition = new float2(absolute.x + step * direction, absolute.y);
        UpdateBar();
    }

    private void SizeContent()
    {
        int count = _order.Count;
        float width = count > 0 ? count * CardWidth + (count - 1) * CardGap : CardWidth;
        var offsetMax = _contentRect.OffsetMax.Value;
        if (MathF.Abs(offsetMax.x - width) > 0.01f)
            _contentRect.OffsetMax.Value = new float2(width, offsetMax.y);
        UpdateBar();
        Root.World?.RunInUpdates(1, UpdateBar);
    }

    private void UpdateBar()
    {
        if (Root.IsDestroyed)
            return;
        float viewportWidth = _viewportRect.LocalComputeRect.width;
        if (viewportWidth <= UnlaidViewportFloor)
        {
            if (_sizeRetries++ < 30)
                Root.World?.RunInUpdates(1, UpdateBar);
            return;
        }
        _sizeRetries = 0;

        float contentWidth = _contentRect.LocalComputeRect.width;
        float excess = MathF.Max(0f, contentWidth - viewportWidth);
        bool scrollable = excess > 0.5f;

        BrowserParts.SetActive(_barSlot, scrollable && _order.Count > 0);
        _left.SetEnabled(scrollable && _scroll.AbsolutePosition.x > 0.5f);
        _right.SetEnabled(scrollable && _scroll.AbsolutePosition.x < excess - 0.5f);
        if (!scrollable)
            return;

        float handleWidth = MathF.Max(MinHandleWidth, viewportWidth * (viewportWidth / contentWidth));
        float travel = viewportWidth - handleWidth;
        float fraction = System.Math.Clamp(_scroll.AbsolutePosition.x / excess, 0f, 1f);
        float offset = fraction * travel;
        // The bar spans the whole strip row, arrows included, but the travel it reports is the
        // viewport's - line the handle up with the viewport by shifting it past the left arrow.
        float barLead = ArrowWidth + DashTheme.Gap;
        var min = _handleRect.OffsetMin.Value;
        var max = _handleRect.OffsetMax.Value;
        float newMin = barLead + offset;
        float newMax = newMin + handleWidth;
        if (MathF.Abs(min.x - newMin) > 0.01f)
            _handleRect.OffsetMin.Value = new float2(newMin, min.y);
        if (MathF.Abs(max.x - newMax) > 0.01f)
            _handleRect.OffsetMax.Value = new float2(newMax, max.y);
    }

    private void OnHandlePress(UIInteractionContext context)
    {
        _scroll.StopScrolling();
        _pressX = context.LocalPoint.x;
        _pressScroll = _scroll.AbsolutePosition.x;
        _handleTarget = _pressScroll;
        _handleDragging = false;
    }

    private void OnHandleDrag(UIInteractionContext context)
    {
        float viewportWidth = _viewportRect.LocalComputeRect.width;
        float contentWidth = _contentRect.LocalComputeRect.width;
        if (viewportWidth <= UnlaidViewportFloor || contentWidth <= 0f)
            return;
        float excess = MathF.Max(0f, contentWidth - viewportWidth);
        if (excess <= 0f)
            return;
        float handleWidth = MathF.Max(MinHandleWidth, viewportWidth * (viewportWidth / contentWidth));
        float travel = viewportWidth - handleWidth;
        if (travel <= 0f)
            return;
        float delta = context.LocalPoint.x - _pressX;
        _handleTarget = System.Math.Clamp(_pressScroll + delta * (excess / travel), 0f, excess);
        _handleDragging = true;
        float shown = DashScrollbar.Follow(_scroll.AbsolutePosition.x, _handleTarget, _scroll.World);
        _scroll.AbsolutePosition = new float2(System.Math.Clamp(shown, 0f, excess), 0f);
    }

    // Let go, the strip lands where the last drag frame put the handle, not where the release jolted it.
    private void OnHandleRelease(UIInteractionContext context)
    {
        if (!_handleDragging)
            return;
        _handleDragging = false;
        if (!_scroll.IsDestroyed)
            _scroll.AbsolutePosition = new float2(_handleTarget, 0f);
    }
}

// One host of the world in the detail view's strip. Host, occupancy, how it was found, and a Join.
internal sealed class SessionCard
{
    public readonly Slot Root;
    public SessionListEntry? Entry;
    public Action? Activate;
    public Action? Join;

    private readonly RoundedPanel _frame;
    private readonly Text _host;
    private readonly ProfileLink _hostLink;
    private readonly Text _users;
    private readonly Chip _sourceChip;
    private readonly Chip _visibilityChip;
    private readonly PillButton _join;
    private readonly ArtBlock _art;

    public SessionCard(BrowserParts parts, Slot parent)
    {
        Root = parent.AddSlot("Session");
        Root.AttachComponent<RectTransform>();
        Root.AttachComponent<GraphicChunkRoot>();
        BrowserParts.Size(Root, SessionStrip.CardWidth, SessionStrip.CardHeight);
        _frame = BrowserParts.Panel(Root, DashTheme.Surface, DashTheme.RadiusCard,
            DashTheme.Outline, DashTheme.OutlineWidth);

        var button = Root.AttachComponent<Button>();
        button.Clicked += (_, _) => Activate?.Invoke();
        var driver = button.AddColorDriver(_frame.Color, DashTheme.Surface, InteractionColorMode.Direct);
        driver.HighlightColor.Value = DashTheme.SurfaceHover;
        driver.PressedColor.Value = DashTheme.SurfacePressed;
        driver.DisabledColor.Value = DashTheme.Surface;

        var hostSlot = Root.AddSlot("Host");
        BrowserParts.PinTop(hostSlot.AttachComponent<RectTransform>(), 14f, 24f, 14f);
        _host = parts.Label(hostSlot, string.Empty, DashTheme.FontBody, DashTheme.Text,
            BrowserParts.Weight.Semibold);
        hostSlot.GetComponent<RectTransform>()!.OffsetMax.Value = new float2(-114f, -14f);
        _hostLink = ProfileLink.On(_host);

        var usersSlot = Root.AddSlot("Users");
        BrowserParts.PinTop(usersSlot.AttachComponent<RectTransform>(), 40f, 20f, 14f);
        _users = parts.Label(usersSlot, string.Empty, DashTheme.FontSmall, DashTheme.TextDim,
            BrowserParts.Weight.Regular);
        usersSlot.GetComponent<RectTransform>()!.OffsetMax.Value = new float2(-114f, -40f);
        var picture = Root.AddSlot("Preview");
        BrowserParts.PinTopCorner(picture.AttachComponent<RectTransform>(), 12f, 92f, 58f, fromRight: true);
        _art = new ArtBlock(parts, picture, DashTheme.RadiusControl, 26f, 0f);

        _sourceChip = parts.AddChip(Root, string.Empty, DashTheme.Field, DashTheme.TextDim, 68f);
        BrowserParts.PinBottomCorner(_sourceChip.Root.GetComponent<RectTransform>()!, 14f, 68f,
            DashTheme.ChipHeight, fromRight: false);

        _visibilityChip = parts.AddChip(Root, string.Empty, DashTheme.Field, DashTheme.TextDim, 78f);
        BrowserParts.PinBottomCorner(_visibilityChip.Root.GetComponent<RectTransform>()!, 14f, 78f,
            DashTheme.ChipHeight, fromRight: false, xOffset: 74f);

        _join = parts.AddButton(Root, "Join", 84f, DashTheme.ControlHeight, DashTheme.Accent,
            DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent,
            BrowserParts.Weight.Semibold, () => Join?.Invoke());
        BrowserParts.PinBottomCorner(_join.Root.GetComponent<RectTransform>()!, 12f, 84f,
            DashTheme.ControlHeight, fromRight: true);
    }

    public void Destroy()
    {
        if (!Root.IsDestroyed)
            Root.Destroy();
    }

    public void Apply(SessionListEntry entry)
    {
        Entry = entry;
        _art.SetPlaceholder(WorldGroup.TryReadMode(entry.Tags, out var mode) ? mode : null, entry.Name);
        string host = string.IsNullOrEmpty(entry.HostUsername) ? "(unnamed host)" : entry.HostUsername;
        BrowserParts.SetText(_host, BrowserParts.Truncate(host, 28));
        _hostLink.Bind(entry.HostUserId, DashTheme.Text);
        BrowserParts.SetText(_users, $"{entry.ActiveUsers}/{entry.MaxUsers} users");
        _sourceChip.Set(entry.Source == SessionSource.Internet ? "Internet" : "Local",
            DashTheme.Field, DashTheme.TextDim);
        _visibilityChip.Set(entry.Visibility.ToString(), DashTheme.Field, DashTheme.TextDim);
        _join.SetEnabled(entry.HasSpace);
        _join.SetLabel(entry.HasSpace ? "Join" : "Full");
    }

    public void SetSelected(bool selected)
    {
        BrowserParts.SetOutline(_frame, selected ? DashTheme.Accent : DashTheme.Outline,
            selected ? 2f : DashTheme.OutlineWidth);
    }

    public void ApplyArt(Lumora.Core.Assets.IAssetProvider<Lumora.Core.Assets.TextureAsset>? texture) => _art.SetTexture(texture);
}
