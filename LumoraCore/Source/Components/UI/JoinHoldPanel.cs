// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Assets;
using Lumora.Core.Localization;
using Lumora.Core.Management;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The loading panel for a join that waits on a verified email. It takes the loading card's place in front of the
// person, says why, and keeps the join: Send again for the mail, a check once the link is opened, and Join now as
// soon as the account answers verified, which goes on to the same world at the same addresses. Cancel lets the join
// go and puts the dash back if it was up.
//
// A panel and not the card, because this one gets pressed and the card takes no pointer. On a screen it frees the
// cursor while it is up, as the context menu does, and it puts the dash away, which would cover it. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class JoinHoldPanel : Component
{
    // Canvas units; the slot's scale makes them a size in the room.
    private const float Width = 620f;
    private const float Height = 280f;
    private const float Pad = 24f;
    private const float BadgeHeight = 26f;
    private const float BadgeWidth = 210f;
    private const float TitleHeight = 36f;
    private const float BodyHeight = 64f;
    private const float StatusHeight = 22f;
    private const float ButtonHeight = 40f;
    private const float ButtonGap = 10f;
    private const float CancelWidth = 130f;
    private const float SendWidth = 190f;
    private const float CheckWidth = 170f;
    private const float JoinWidth = 200f;
    private const float FrameRing = 2f;

    private const float HeightMeters = 0.28f;
    private const float Distance = 0.75f;
    private const float BelowEyes = 0.08f;
    private const float RefreshInterval = 0.2f;
    // Walked this far off, or turned with it behind them, and it comes back in front.
    private const float LeftBehindDistance = 2f;
    private const int NameLength = 32;

    private static JoinHoldPanel? _current;

    private WorldLoadingOperation? _operation;
    private RoundedPanel? _frame;
    private RoundedPanel? _badge;
    private Text? _badgeText;
    private Text? _joining;
    private Text? _title;
    private Text? _body;
    private Text? _status;
    private SettingsChip? _cancel;
    private SettingsChip? _send;
    private SettingsChip? _check;
    private SettingsChip? _join;
    // The line over the buttons: how a send or a check went, or what the host said.
    private LocaleText? _line;
    private color _lineInk = DashTheme.Warning;
    private bool _sending;
    private bool _checking;
    private bool _built;
    private bool _dashWasOpen;
    private bool _cursorFreed;
    private bool _heldInput;
    private UserInputState? _heldState;
    private float _clock;

    public static JoinHoldPanel? Current => _current is { IsDestroyed: false } panel ? panel : null;

    public WorldLoadingOperation? Operation => _operation;

    // Join now is up: the account answered that it may join.
    public bool Ready { get; private set; }

    public string StatusText => _status?.Content.Value ?? string.Empty;

    // Up for this join; built, placed and given the pointer the first time. A join that replaced a held one keeps
    // the same panel. Userspace thread.
    public static JoinHoldPanel? Show(WorldLoadingOperation operation)
    {
        var panel = Current;
        if (panel == null)
        {
            var root = Templates.Userspace.LocalRoot;
            if (root == null || root.IsDestroyed)
                root = Engine.Current?.WorldManager?.UserspaceWorld?.RootSlot;
            if (root == null || root.IsDestroyed)
                return null;
            var slot = root.AddSlot("Join Hold Panel");
            slot.Persistent.Value = false;
            panel = slot.AttachComponent<JoinHoldPanel>();
            _current = panel;
            panel.EnsureBuilt();
            panel.PlaceInFront();
            panel.TakeView();
        }
        if (!ReferenceEquals(panel._operation, operation))
        {
            panel._operation = operation;
            panel._sending = false;
            panel._checking = false;
            panel.Say(operation.HeldByHost ? JoinHoldLocale.HostRefused : null, DashTheme.Warning);
            panel.Refresh();
        }
        return panel;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var operation = _operation;
        // Joining now, or let go from somewhere else: nothing left to show. Replaced by another join that is held
        // as well: that one is shown here instead.
        if (operation == null || operation.IsCancelled || !operation.IsHeld)
        {
            if (operation != null && Engine.Current?.WorldLoadingService?.HeldOperation is { } next && !ReferenceEquals(next, operation))
            {
                Show(next);
                return;
            }
            Slot.Destroy();
            return;
        }
        _clock += delta;
        if (_clock < RefreshInterval)
            return;
        _clock = 0f;
        KeepInView();
        Refresh();
    }

    public override void OnDestroy()
    {
        GiveViewBack();
        if (ReferenceEquals(_current, this))
            _current = null;
        base.OnDestroy();
    }

    // PRESSES

    public void Cancel()
    {
        var operation = _operation;
        _operation = null;
        if (operation != null)
            Engine.Current?.WorldLoadingService?.Drop(operation);
        bool reopen = _dashWasOpen;
        Slot.Destroy();
        if (reopen)
            UserspaceDashboard.LocalInstance?.Open();
    }

    public void JoinNow()
    {
        var operation = _operation;
        var loader = Engine.Current?.WorldLoadingService;
        if (operation == null || loader == null || !Ready)
            return;
        if (!loader.Resume(operation))
        {
            Refresh();
            return;
        }
        _operation = null;
        Slot.Destroy();
    }

    public void SendAgain()
    {
        if (_sending || AccountEmail.ResendWaitSeconds > 0)
            return;
        _sending = true;
        Refresh();
        var world = World;
        _ = Task.Run(async () =>
        {
            var outcome = await AccountEmail.ResendAsync().ConfigureAwait(false);
            world?.RunSynchronously(() =>
            {
                if (IsDestroyed)
                    return;
                _sending = false;
                bool good = outcome is ResendOutcome.Sent or ResendOutcome.AlreadyVerified;
                Say(AccountEmail.Describe(outcome), good ? DashTheme.Positive : DashTheme.Warning);
                Refresh();
            });
        });
    }

    public void CheckAgain()
    {
        if (_checking)
            return;
        _checking = true;
        Say(null, DashTheme.Warning);
        Refresh();
        var world = World;
        _ = Task.Run(async () =>
        {
            bool answered = await AccountEmail.RecheckAsync(force: true).ConfigureAwait(false);
            world?.RunSynchronously(() =>
            {
                if (IsDestroyed)
                    return;
                _checking = false;
                if (!answered)
                    Say(JoinHoldLocale.CheckFailed, DashTheme.Warning);
                else if (AccountEmail.JoinsHeld)
                    Say(AccountEmailLocale.StillNotVerified, DashTheme.Warning);
                Refresh();
            });
        });
    }

    // BUILDING

    private void EnsureBuilt()
    {
        if (_built)
            return;
        _built = true;

        var surface = PanelSurface.Create(Slot, new float2(Width, Height));
        var dash = UserspaceDashboard.LocalInstance?.Dashboard;
        if (dash != null && !dash.IsDestroyed && ReferenceEquals(dash.World, World))
        {
            surface.WatchFont(dash.Font.Target);
            surface.WatchFont(dash.FontSemibold.Target);
            surface.WatchFont(dash.FontBold.Target);
        }
        var canvas = surface.CanvasSlot!;
        _frame = SettingsUI.Panel(canvas, DashTheme.GlassBackdrop, DashTheme.Warning, DashTheme.RadiusPanel);
        _frame.OutlineThickness.Value = FrameRing;

        var top = new float2(0f, 1f);
        var badge = SettingsUI.Child(canvas, "Badge", top, top, new float2(Pad, -(Pad + BadgeHeight)), new float2(Pad + BadgeWidth, -Pad));
        _badge = SettingsUI.Panel(badge, Soft(DashTheme.Warning), color.Transparent, BadgeHeight * 0.5f);
        _badgeText = SettingsUI.FillLabel(badge, "Text", Font(1), DashTheme.FontSmall, DashTheme.Warning, shrinkToFit: true);

        _joining = SettingsUI.Label(canvas, "Joining", Font(0), DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Right,
            top, float2.One, new float2(Pad + BadgeWidth + 16f, -(Pad + BadgeHeight)), new float2(-Pad, -Pad));

        float titleTop = Pad + BadgeHeight + 14f;
        _title = SettingsUI.Label(canvas, "Title", Font(2), DashTheme.FontDisplay, DashTheme.Text, TextHorizontalAlignment.Left,
            top, float2.One, new float2(Pad, -(titleTop + TitleHeight)), new float2(-Pad, -titleTop), shrinkToFit: true);

        float bodyTop = titleTop + TitleHeight + 6f;
        _body = SettingsUI.Label(canvas, "Body", Font(0), DashTheme.FontBody + 1f, DashTheme.TextDim, TextHorizontalAlignment.Left,
            top, float2.One, new float2(Pad, -(bodyTop + BodyHeight)), new float2(-Pad, -bodyTop));
        _body.WordWrap.Value = true;
        _body.Ellipsis.Value = false;
        _body.VerticalAlignment.Value = TextVerticalAlignment.Top;

        float statusBottom = Pad + ButtonHeight + 10f;
        _status = SettingsUI.Label(canvas, "Status", Font(1), DashTheme.FontSmall, DashTheme.TextDim, TextHorizontalAlignment.Left,
            float2.Zero, new float2(1f, 0f), new float2(Pad, statusBottom), new float2(-Pad, statusBottom + StatusHeight), shrinkToFit: true);

        _cancel = Chip(canvas, "Cancel", float2.Zero, new float2(Pad, Pad), new float2(Pad + CancelWidth, Pad + ButtonHeight), Cancel);
        _cancel.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.Text);
        ListingStyle.SetText(_cancel.Text, JoinHoldLocale.Cancel.Resolve());

        var right = new float2(1f, 0f);
        _check = Chip(canvas, "Check", right, new float2(-(Pad + CheckWidth), Pad), new float2(-Pad, Pad + ButtonHeight), CheckAgain);
        _check.SetPaint(DashTheme.Accent, DashTheme.AccentHover, DashTheme.Accent, DashTheme.OnAccent);
        float sendRight = Pad + CheckWidth + ButtonGap;
        _send = Chip(canvas, "Send", right, new float2(-(sendRight + SendWidth), Pad), new float2(-sendRight, Pad + ButtonHeight), SendAgain);
        _send.SetPaint(DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.Outline, DashTheme.Text);
        _join = Chip(canvas, "Join", right, new float2(-(Pad + JoinWidth), Pad), new float2(-Pad, Pad + ButtonHeight), JoinNow);
        _join.SetPaint(DashTheme.Accent, DashTheme.AccentHover, DashTheme.Accent, DashTheme.OnAccent);
        ListingStyle.SetText(_join.Text, JoinHoldLocale.JoinNow.Resolve());
        ListingStyle.SetActive(_join.Slot, false);
    }

    private SettingsChip Chip(Slot canvas, string name, in float2 anchor, in float2 offsetMin, in float2 offsetMax, Action press)
    {
        var host = SettingsUI.Child(canvas, name, anchor, anchor, offsetMin, offsetMax);
        var chip = SettingsChip.Build(host, Font(1), DashTheme.FontBody, DashTheme.RadiusControl);
        chip.Button.Clicked += (_, _) => press();
        return chip;
    }

    private static color Soft(in color ink) => new(ink.r, ink.g, ink.b, 0.16f);

    // 0 regular, 1 semibold, 2 bold. The dash in this world hands out the faces; a Text with none draws nothing.
    private IAssetProvider<FontSet>? Font(int weight)
    {
        var dash = UserspaceDashboard.LocalInstance?.Dashboard;
        if (dash == null || dash.IsDestroyed || !ReferenceEquals(dash.World, World))
            return null;
        return weight switch
        {
            2 => dash.FontBold.Target ?? dash.FontSemibold.Target ?? dash.Font.Target,
            1 => dash.FontSemibold.Target ?? dash.Font.Target,
            _ => dash.Font.Target,
        };
    }

    private void Refresh()
    {
        var operation = _operation;
        if (!_built || operation == null)
            return;

        bool signedIn = Engine.Current?.CDNClient?.IsAuthenticated == true;
        bool ready = !AccountEmail.JoinsHeld && (AccountEmail.Known || !signedIn);
        Ready = ready;
        string world = Clip(operation.WorldName);
        var ink = ready ? DashTheme.Positive : DashTheme.Warning;

        if (_frame != null)
            SettingsUI.SetPaint(_frame, DashTheme.GlassBackdrop, ink, FrameRing);
        if (_badge != null)
            SettingsUI.SetFill(_badge, Soft(ink));
        SetText(_badgeText, (ready ? AccountEmailLocale.Verified : AccountEmailLocale.NotVerified).Resolve(), ink);
        SetText(_joining, JoinHoldLocale.Joining(world).Resolve(), DashTheme.TextDim);
        SetText(_title, (ready ? JoinHoldLocale.ReadyTitle : JoinHoldLocale.Title).Resolve(), DashTheme.Text);
        SetText(_body, (ready ? JoinHoldLocale.ReadyBody(world) : JoinHoldLocale.Body(AccountEmail.Address ?? string.Empty)).Resolve(),
            DashTheme.TextDim);
        SetText(_status, !ready && _line is { } line ? line.Resolve() : string.Empty, _lineInk);

        if (_send != null)
        {
            ListingStyle.SetActive(_send.Slot, !ready);
            int wait = AccountEmail.ResendWaitSeconds;
            var label = _sending ? AccountEmailLocale.Sending
                : wait > 0 ? AccountEmailLocale.SendAgainIn(wait)
                : AccountEmailLocale.SendAgain;
            ListingStyle.SetText(_send.Text, label.Resolve());
            _send.SetInteractable(!_sending && wait == 0 && signedIn);
        }
        if (_check != null)
        {
            ListingStyle.SetActive(_check.Slot, !ready);
            ListingStyle.SetText(_check.Text, (_checking ? AccountEmailLocale.Checking : AccountEmailLocale.Check).Resolve());
            _check.SetInteractable(!_checking);
        }
        if (_join != null)
            ListingStyle.SetActive(_join.Slot, ready);
    }

    private void Say(LocaleText? line, in color ink)
    {
        _line = line;
        _lineInk = ink;
    }

    private static void SetText(Text? text, string value, in color ink)
    {
        if (text == null || text.IsDestroyed)
            return;
        ListingStyle.SetText(text, value);
        ListingStyle.SetTextColor(text, ink);
    }

    private static string Clip(string? name)
    {
        string value = (name ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= NameLength ? value : value[..(NameLength - 1)] + "…";
    }

    // PLACE AND POINTER

    // Arm's length out along the head's flattened forward, a little under the eyes, turned to face back. Userspace
    // shares the focused world's space, so that world's head is where the person is.
    private void PlaceInFront()
    {
        var user = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        Slot.GlobalScale = float3.One * (HeightMeters / (Height * PanelSurface.UnitMeters));
        if (user?.HeadSlot == null)
        {
            Slot.GlobalPosition = new float3(0f, 1.4f, -Distance);
            Slot.GlobalRotation = floatQ.Identity;
            return;
        }
        var head = user.HeadPosition;
        var forward = Flat(user.HeadRotation * float3.Backward);
        Slot.GlobalPosition = head + forward * Distance + float3.Down * BelowEyes;
        var toViewer = head - Slot.GlobalPosition;
        toViewer.y = 0f;
        Slot.GlobalRotation = toViewer.LengthSquared > 1e-6f
            ? floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z))
            : floatQ.Identity;
    }

    private void KeepInView()
    {
        var user = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
        if (user?.HeadSlot == null)
            return;
        var head = user.HeadPosition;
        var toPanel = Slot.GlobalPosition - head;
        float distance = toPanel.Length;
        toPanel.y = 0f;
        bool behind = toPanel.LengthSquared > 1e-6f && float3.Dot(toPanel.Normalized, Flat(user.HeadRotation * float3.Backward)) < 0f;
        if (distance > LeftBehindDistance || behind)
            PlaceInFront();
    }

    private static float3 Flat(float3 direction)
    {
        direction.y = 0f;
        return direction.LengthSquared > 1e-6f ? direction.Normalized : float3.Backward;
    }

    // The dash would cover the panel on a screen and take the pointer in a headset, so it goes away while this is
    // up. On a screen the cursor comes loose to press with and the view holds still under it.
    private void TakeView()
    {
        var dash = UserspaceDashboard.LocalInstance;
        if (dash != null && !dash.IsDestroyed && dash.IsOpen.Value)
        {
            _dashWasOpen = true;
            dash.Close();
        }
        var input = Engine.Current?.InputInterface;
        if (input == null || input.IsVRActive)
            return;
        input.SetCursorFree(this, true);
        _cursorFreed = true;
        var state = UserInputState.ForFocusedLocalUser;
        _heldState = state;
        if (state == null)
            return;
        if (input.DesktopExternalCameraAim)
        {
            state.SetDesktopInputSuppressed(this, true);
            _heldInput = true;
        }
        else
        {
            state.SetViewHeld(this, true);
        }
    }

    private void GiveViewBack()
    {
        if (_cursorFreed)
        {
            _cursorFreed = false;
            Engine.Current?.InputInterface?.SetCursorFree(this, false);
        }
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
}
