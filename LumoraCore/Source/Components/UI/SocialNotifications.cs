// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Localization;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

[ComponentCategory("Hidden")]
public sealed class SocialNotifications : Component
{
    private ISocialService? _social;
    private string? _account;
    private readonly Queue<ChatMessageInfo> _waiting = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private double _nextNotice;

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        var social = Engine.Current?.Social;
        if (!ReferenceEquals(social, _social))
        {
            if (_social != null)
                _social.MessageReceived -= Received;
            _social = social;
            if (_social != null)
                _social.MessageReceived += Received;
        }
        CheckAccount();
        double now = World.Time.TotalTime;
        if (_waiting.Count == 0 || now < _nextNotice)
            return;
        var focus = Engine.Current?.WorldManager?.FocusedWorld;
        if (focus?.LocalUser?.Root == null || focus.IsDestroyed)
            return;
        var message = _waiting.Dequeue();
        if (!ShouldShow(social, message))
            return;
        string name = social!.FindFriend(message.SenderId)?.Username ?? message.SenderId;
        if (name.Length > 48)
            name = name[..45] + "...";
        string title = message.IsInvite
            ? "Social.Toast.Invite".AsLocale("{0} invited you", name).Resolve()
            : "Social.Toast.Message".AsLocale("Message from {0}", name).Resolve();
        string body = message.Invite?.WorldName ?? message.Item?.Name ?? message.Body;
        body = body.Replace('\r', ' ').Replace('\n', ' ');
        if (body.Length > 180)
            body = body[..177] + "...";
        body += "\n" + "Social.Toast.OpenChat".AsLocale("Open Friends to view or reply.").Resolve();
        focus.RunSynchronously(() => FavouriteNotice.ShowLocal(focus, null, title, body));
        _nextNotice = now + 6.5;
    }

    private static bool ShouldShow(ISocialService? social, ChatMessageInfo message)
        => social?.SelfId is { Length: > 0 } self && message.RecipientId == self
           && message.SenderId != self && message.ReadAt == null && !social.IsBlocked(message.SenderId)
           && (!message.IsInvite || message.ClaimableUntil == null || message.ClaimableUntil > DateTime.UtcNow);

    private void CheckAccount()
    {
        if (_account != _social?.SelfId)
        {
            _account = _social?.SelfId;
            _waiting.Clear();
            _seen.Clear();
            _seenOrder.Clear();
        }
    }

    private void Received(ChatMessageInfo message)
    {
        CheckAccount();
        if (!ShouldShow(_social, message) || string.IsNullOrEmpty(message.Id) || !_seen.Add(message.Id))
            return;
        _seenOrder.Enqueue(message.Id);
        while (_seenOrder.Count > 128)
            _seen.Remove(_seenOrder.Dequeue());
        // Bound a burst of messages while the user is loading. History replay and outgoing messages
        // never reach the toast queue, and private text is not written to the log. -xlinka
        if (_waiting.Count >= 8)
            _waiting.Dequeue();
        _waiting.Enqueue(message);
    }

    public override void OnDestroy()
    {
        if (_social != null)
            _social.MessageReceived -= Received;
        _waiting.Clear();
        base.OnDestroy();
    }
}
