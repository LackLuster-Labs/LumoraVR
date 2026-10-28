// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Networking.Session;

namespace Lumora.Core.Components.UI;

// Someone asking to edit this machine's user, as a modal on the local dashboard: who, in which world, then Deny and
// Allow for this session or remember on this device. After a yes a second card offers Take it back, so the person
// who said yes sees straight away how to undo it. Closing either without a choice counts as no answer.
//
// Allow stays dead for its first three seconds on screen, the same as a web access question, so a click already on
// its way when the card appeared cannot land on it. Asks queue; one card at a time. -xlinka
[ComponentCategory("Hidden")]
public sealed class EditConsentPrompt : Component
{
    public const float AllowDelay = 3f;

    private const int DenyIndex = 0;
    private const int AllowIndex = 1;
    private const int RememberIndex = 2;
    private bool _remembered;
    private const int TakeBackIndex = 0;
    private const int NameLength = 48;

    private readonly List<EditConsentAsk> _queue = new();
    private EditConsentAsk? _current;
    private ModalRequest? _request;
    private bool _followUp;
    private ModalHost? _host;
    private Button? _allowButton;
    private bool _allowReady;
    private double _allowAt;
    private ulong _lastUpdate;
    // Set once the dash was opened for an ask. It closes again when nothing else is waiting.
    private bool _closeDash;

    public EditConsentAsk? Current => _current;

    // Whether the card up is the one after a yes, with Take it back on it.
    public bool ShowsFollowUp => _current != null && _followUp;

    public bool AllowReady => _current != null && !_followUp && _allowReady;

    public int Waiting => _queue.Count;

    public static EditConsentPrompt? For(Dashboard? dashboard)
    {
        if (dashboard == null || dashboard.IsDestroyed || dashboard.Slot == null || dashboard.Slot.IsDestroyed)
            return null;
        return dashboard.Slot.GetComponent<EditConsentPrompt>() ?? dashboard.Slot.AttachComponent<EditConsentPrompt>();
    }

    public void Enqueue(EditConsentAsk ask)
    {
        if (ask == null || (_current != null && ask.Is(_current.World, _current.Asker)))
            return;
        foreach (var queued in _queue)
        {
            if (ask.Is(queued.World, queued.Asker))
                return;
        }
        _queue.Add(ask);
        if (_request == null)
            Next();
    }

    // The ask is over elsewhere: answered from another surface, up too long, or the asker left. A card already past
    // the question (the one after a yes) stays.
    public void Withdraw(World world, User asker)
    {
        _queue.RemoveAll(a => a.Is(world, asker));
        if (_current == null || _followUp || !_current.Is(world, asker))
            return;
        TakeDown();
        Next();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        Gate(restart: false);
    }

    public override void OnDestroy()
    {
        if (_host != null)
            _host.Closed -= OnHostClosed;
        _host = null;
        var current = _current;
        bool asking = current != null && !_followUp;
        _current = null;
        _request = null;
        _allowButton = null;
        if (asking)
            Post(current!, world => SessionEditConsent.Unanswered(world, current!.Asker));
        foreach (var queued in _queue)
            Post(queued, world => SessionEditConsent.Unanswered(world, queued.Asker));
        _queue.Clear();
        base.OnDestroy();
    }

    private void Next()
    {
        while (_queue.Count > 0)
        {
            var ask = _queue[0];
            _queue.RemoveAt(0);
            if (ask.Asker.IsDestroyed || ask.World.IsDestroyed || ask.World.IsDisposed)
                continue;
            if (Present(ask))
                return;
        }
        ScheduleDashClose();
    }

    private bool Present(EditConsentAsk ask)
    {
        var host = ModalHost.For(Slot);
        if (host == null)
        {
            Post(ask, world => SessionEditConsent.Unanswered(world, ask.Asker));
            return false;
        }
        Hook(host);

        var dash = UserspaceDashboard.LocalInstance;
        if (dash != null && !dash.IsDestroyed && !dash.IsOpen.Value)
        {
            dash.Open();
            _closeDash = true;
        }

        string name = Clip(ask.Name);
        var request = new ModalRequest
        {
            Title = EditConsentLocale.PromptTitle(name),
            Message = EditConsentLocale.PromptMessage(name, Clip(ask.WorldName)),
            Invoker = Slot,
            CancelIndex = -1,
            DefaultIndex = -1,
            DismissOnScrim = true,
        };
        request.AddOption(EditConsentLocale.Deny);
        request.AddOption(EditConsentLocale.Allow, ModalOptionStyle.Accent);
        if (RememberedEditConsent.CanRemember(ask.World, ask.Asker))
            request.AddOption(EditConsentLocale.Remember);
        request.Completed = result => OnCompleted(request, result);

        _current = ask;
        _request = request;
        _followUp = false;
        _allowReady = false;
        if (!host.Open(request))
        {
            _current = null;
            _request = null;
            Post(ask, world => SessionEditConsent.Unanswered(world, ask.Asker));
            return false;
        }
        Gate(restart: true);
        return true;
    }

    // After a yes: what they can do now, and the way back.
    private bool PresentFollowUp(EditConsentAsk ask)
    {
        var host = _host;
        if (host == null || host.IsDestroyed)
            return false;
        var request = new ModalRequest
        {
            Title = EditConsentLocale.GrantedTitle(Clip(ask.Name)),
            Message = _remembered ? EditConsentLocale.RememberedMessage : EditConsentLocale.GrantedMessage,
            Invoker = Slot,
            CancelIndex = 1,
            DefaultIndex = 1,
            DismissOnScrim = true,
        };
        request.AddOption(EditConsentLocale.TakeBack, ModalOptionStyle.Destructive);
        request.AddOption(EditConsentLocale.Ok, ModalOptionStyle.Accent);
        request.Completed = result => OnCompleted(request, result);

        _current = ask;
        _request = request;
        _followUp = true;
        if (host.Open(request))
            return true;
        _current = null;
        _request = null;
        _followUp = false;
        return false;
    }

    private void OnCompleted(ModalRequest request, ModalResult result)
    {
        if (!ReferenceEquals(_request, request) || _current == null)
            return;
        var ask = _current;
        bool followUp = _followUp;
        bool ready = _allowReady;
        _current = null;
        _request = null;
        _allowButton = null;
        _allowReady = false;
        _followUp = false;

        if (followUp)
        {
            if (result.Index == TakeBackIndex)
                Post(ask, world => SessionEditConsent.Revoke(world, ask.Asker));
            Next();
            return;
        }

        // Only a live Allow counts; anything that completes the card on that index early is no answer.
        if ((result.Index == AllowIndex || result.Index == RememberIndex && RememberedEditConsent.CanRemember(ask.World, ask.Asker)) && ready)
        {
            _remembered = result.Index == RememberIndex;
            bool remember = _remembered;
            Post(ask, world => SessionEditConsent.Answer(world, ask.Asker, allow: true, remember: remember));
            if (!PresentFollowUp(ask))
                Next();
            return;
        }
        if (result.Index == DenyIndex)
            Post(ask, world => SessionEditConsent.Answer(world, ask.Asker, allow: false));
        else
            Post(ask, world => SessionEditConsent.Unanswered(world, ask.Asker));
        Next();
    }

    // Allow is live only once it has sat on screen for AllowDelay. A new button instance means the card was rebuilt,
    // and a gap in updates means the dash was closed and nothing could be seen.
    private void Gate(bool restart)
    {
        var host = _host;
        if (_request == null || _followUp || host == null || host.IsDestroyed || !ReferenceEquals(host.Current, _request))
            return;
        var buttons = host.OptionButtons;
        if (buttons.Count <= AllowIndex)
            return;
        var allow = buttons[AllowIndex];
        if (allow == null || allow.IsDestroyed)
            return;

        var time = World.Time;
        if (restart || !ReferenceEquals(allow, _allowButton) || time.UpdateIndex > _lastUpdate + 1)
        {
            _allowButton = allow;
            _allowAt = time.TotalTime + AllowDelay;
        }
        _lastUpdate = time.UpdateIndex;
        _allowReady = time.TotalTime >= _allowAt;
        if (allow.Interactable.Value != _allowReady)
            allow.Interactable.Value = _allowReady;
        if (buttons.Count > RememberIndex && buttons[RememberIndex] is { IsDestroyed: false } remember)
            remember.Interactable.Value = _allowReady && _current != null && RememberedEditConsent.CanRemember(_current.World, _current.Asker);
    }

    // Takes the card down without an answer; the ask already ended somewhere else.
    private void TakeDown()
    {
        var request = _request;
        var host = _host;
        _current = null;
        _request = null;
        _allowButton = null;
        _allowReady = false;
        _followUp = false;
        if (request != null && host != null && !host.IsDestroyed && ReferenceEquals(host.Current, request))
            host.Complete(-1);
    }

    private void Hook(ModalHost host)
    {
        if (ReferenceEquals(_host, host))
            return;
        if (_host != null)
            _host.Closed -= OnHostClosed;
        _host = host;
        host.Closed += OnHostClosed;
    }

    // A card stacked over this one closed and the host rebuilt ours with fresh buttons: gate them now.
    private void OnHostClosed(ModalHost host, Slot? invoker)
    {
        if (_request != null && ReferenceEquals(host.Current, _request))
            Gate(restart: true);
    }

    private void ScheduleDashClose()
    {
        if (!_closeDash)
            return;
        World?.RunInUpdates(2, () =>
        {
            if (IsDestroyed || _current != null || _queue.Count > 0 || !_closeDash)
                return;
            _closeDash = false;
            var dash = UserspaceDashboard.LocalInstance;
            if (dash != null && !dash.IsDestroyed && ReferenceEquals(dash.World, World))
                dash.Close();
        });
    }

    // The answer belongs to the session world the ask came from, on its own update.
    private static void Post(EditConsentAsk ask, Action<World> act)
    {
        var world = ask.World;
        if (world == null || world.IsDestroyed || world.IsDisposed)
            return;
        world.RunSynchronously(() =>
        {
            if (!world.IsDestroyed && !world.IsDisposed)
                act(world);
        });
    }

    private static string Clip(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "?";
        return text.Length <= NameLength ? text : text.Substring(0, NameLength - 3) + "...";
    }
}
