// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core.Components.Import;
using Lumora.Core.Localization;
using Lumora.Core.Math;
using Lumora.Core.Social;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// THE CHAT: one person's messages, newest at the bottom, following the end until the reader scrolls up,
// and a line to type into. Only friends get one; anybody else gets a note saying so and the way to fix it.
public sealed partial class PeopleScreen
{
    private const int ChatPage = 50;
    private const float SendWidth = 76f;
    private const float PlusWidth = 36f;
    private const float TailSnap = 0.985f;

    private readonly List<ChatMessageInfo> _messages = new();
    // the newest of ours they have read, from the fetch and from their read markers since
    private DateTime? _seenUpTo;
    private const float SeenBubbleMin = 128f;
    private LoadState _chatLoad = LoadState.Idle;
    private string _chatFor = string.Empty;
    private bool _chatHasOlder;
    // The oldest message the service has handed over, which the next page back starts from. Held apart from the
    // list, since that one may have been deleted since; the service still pages from where it was.
    private string _chatOldest = string.Empty;
    private bool _chatVisible;
    private bool _followTail = true;
    private int _pinTail;
    private bool _applyingScroll;
    private Slot? _compose;
    private TextInput? _composeInput;

    // OURS ON THE WAY
    // A message shows the moment it is sent, as a row of its own under a local id, and gives way to the
    // service's copy when that lands, whichever of the answer and the feed brings it first. One the service
    // turns back stays where it was put, saying so, until it is pressed to go again. Kept per person, so
    // switching chats loses none of it. -xlinka
    private const string LocalPrefix = "local-";

    private enum SendState { Sending, Failed }

    private sealed class Outgoing
    {
        public required string Person;
        public required ChatMessageInfo Message;
        public SendState State;
        // removed while still on its way: if it lands after all, it is taken back from both sides at once
        public bool Withdrawn;
    }

    private readonly List<Outgoing> _outbox = new();
    private int _localIds;

    // TYPING
    // Ours: a ping when the line changes, at most every TypingPingSeconds, and after a send or a clear the
    // next one goes as soon as the service would take it. Theirs: shown until TypingShowSeconds after their
    // last ping, or until their message lands.
    private const double TypingPingSeconds = 3.0;
    private const double TypingRearmSeconds = 1.6;
    private const double TypingShowSeconds = 6.0;
    private const double TypingStepSeconds = 0.2;
    private readonly Dictionary<string, double> _typingUntil = new(StringComparer.Ordinal);
    private readonly List<string> _typingLapsed = new();
    private readonly List<PeopleTypingTemplate> _typingDots = new();
    private string _typedTo = string.Empty;
    private double _typedAt = double.NegativeInfinity;
    private bool _typedRearmed;
    private bool _typingShown;

    private double Clock => World?.Time.TotalTime ?? 0.0;

    private void BuildCompose(Slot pane)
    {
        _compose = SettingsUI.Child(pane, "Compose", float2.Zero, new float2(1f, 0f), float2.Zero, new float2(0f, PeopleMetrics.ComposeHeight));
        _compose.AttachComponent<GraphicChunkRoot>();
        SettingsUI.Panel(_compose, DashTheme.Surface, DashTheme.Outline, DashTheme.RadiusCard);

        var plus = SettingsUI.Child(_compose, "SendItem", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(5f, -17f), new float2(5f + PlusWidth, 17f));
        IconButton(plus, "plus", color.Transparent, () => OpenPicker(PickPurpose.Item));
        plus.GetComponent<Button>()!.Tip.Value = PeopleLocale.SendItem.Resolve();

        var field = SettingsUI.Child(_compose, "Field", new float2(0f, 0.5f), new float2(1f, 0.5f),
            new float2(5f + PlusWidth + 6f, -16f), new float2(-(SendWidth + 12f), 16f));
        _composeInput = DashTextField.Attach(field, _fonts.Body, DashTheme.FontBody, PeopleLocale.ChatHint.Resolve(), SocialLimits.MessageMax);
        _composeInput.EditingFinished += (_, text) => SendTyped(text);
        _composeInput.EditingChanged += (_, text) => OnComposeTyped(text);

        var send = SettingsChip.Build(SettingsUI.Child(_compose, "Send", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(SendWidth + 6f), -16f), new float2(-6f, 16f)), _fonts.Medium, DashTheme.FontSmall, DashTheme.RadiusControl);
        GroupBand.PaintAction(send, PeopleLocale.Send.Resolve(), true, false, true);
        send.Button.Clicked += (_, _) => SendTyped(_composeInput?.Text.Value ?? string.Empty);
        _compose.ActiveSelf.Value = false;
    }

    private bool ComposeShown()
        => _pane == PaneView.Person && _tab == PersonTab.Chat && RelationOf(_personId) == SocialRelations.Friend;

    private void EnterChat()
    {
        _chatVisible = true;
        _followTail = true;
        _pinTail = 4;
        if (!string.Equals(_chatFor, _personId, StringComparison.Ordinal) || _chatLoad is LoadState.Idle or LoadState.Error)
            LoadChat();
        else
            MarkRead();
    }

    private void LeaveChat()
    {
        _chatVisible = false;
        if (_composeInput != null && _composeInput.IsFocused)
            _composeInput.Unfocus();
    }

    private async void LoadChat()
    {
        var social = Social;
        string person = _personId;
        if (social == null || person.Length == 0 || RelationOf(person) != SocialRelations.Friend)
            return;
        _chatFor = person;
        _chatLoad = LoadState.Loading;
        _messages.Clear();
        _chatOldest = string.Empty;
        _seenUpTo = null;
        _paneDirty = true;
        var world = World;
        try
        {
            var result = await social.GetMessagesAsync(person, null, ChatPage);
            OnUi(world, () =>
            {
                if (!string.Equals(person, _chatFor, StringComparison.Ordinal))
                    return;
                if (result.Success && result.Data != null)
                {
                    _messages.Clear();
                    foreach (var message in result.Data)
                    {
                        if (!_deleting.ContainsKey(message.Id))
                            _messages.Add(message);
                    }
                    foreach (var outgoing in _outbox)
                    {
                        if (string.Equals(outgoing.Person, person, StringComparison.Ordinal) && !outgoing.Withdrawn)
                            _messages.Add(outgoing.Message);
                    }
                    _seenUpTo = Later(_seenUpTo, NewestSeen(result.Data));
                    _chatOldest = result.Data.Count > 0 ? result.Data[0].Id : string.Empty;
                    _chatHasOlder = result.Data.Count >= ChatPage;
                    _chatLoad = LoadState.Loaded;
                    _inviteStatesAt = double.NegativeInfinity;
                    _pinTail = 4;
                    MarkRead();
                }
                else
                {
                    _chatLoad = LoadState.Error;
                    _paneError = string.IsNullOrWhiteSpace(result.Message) ? PeopleLocale.LoadFailed.Resolve() : result.Message;
                }
                _paneDirty = true;
            });
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            OnUi(world, () =>
            {
                _chatLoad = LoadState.Error;
                _paneError = message;
                _paneDirty = true;
            });
        }
    }

    private async void LoadOlder()
    {
        var social = Social;
        string person = _chatFor;
        string before = _chatOldest.Length > 0 ? _chatOldest : _messages.Count > 0 ? _messages[0].Id : string.Empty;
        if (social == null || person.Length == 0 || before.Length == 0 || !_chatHasOlder)
            return;
        var world = World;
        try
        {
            var result = await social.GetMessagesAsync(person, before, ChatPage);
            OnUi(world, () =>
            {
                if (!string.Equals(person, _chatFor, StringComparison.Ordinal) || !result.Success || result.Data == null)
                    return;
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var message in _messages)
                    known.Add(message.Id);
                var older = new List<ChatMessageInfo>();
                foreach (var message in result.Data)
                {
                    if (known.Add(message.Id) && !_deleting.ContainsKey(message.Id))
                        older.Add(message);
                }
                _messages.InsertRange(0, older);
                if (result.Data.Count > 0)
                    _chatOldest = result.Data[0].Id;
                _chatHasOlder = result.Data.Count >= ChatPage;
                _followTail = false;
                _paneDirty = true;
                _body?.ResetToTop();
            });
        }
        catch (Exception)
        {
        }
    }

    private void MarkRead()
    {
        var social = Social;
        string person = _chatFor;
        if (social == null || person.Length == 0 || social.UnreadFrom(person) <= 0)
            return;
        _ = social.MarkReadAsync(person);
    }

    private void OnMessageReceived(ChatMessageInfo message)
    {
        _listDirty = true;
        string self = SelfId ?? string.Empty;
        bool fromSelf = string.Equals(message.SenderId, self, StringComparison.Ordinal);
        string other = fromSelf ? message.RecipientId : message.SenderId;
        // their message is what the dots were for
        if (!fromSelf && _typingUntil.Remove(other))
            TypingMoved(other);
        bool claimed = fromSelf && Claim(message);
        // one of ours being taken back as it lands
        if (_deleting.ContainsKey(message.Id) || !string.Equals(other, _chatFor, StringComparison.Ordinal))
            return;
        if ((claimed || Append(message)) && _chatVisible && _pane == PaneView.Person && _tab == PersonTab.Chat)
        {
            _paneDirty = true;
            if (_followTail)
                _pinTail = 4;
            if (!string.Equals(message.SenderId, self, StringComparison.Ordinal))
                MarkRead();
        }
        _headerDirty = true;
    }

    // They read ours up to upTo: the newest of ours at or before it says Seen.
    private void OnMessagesRead(string byUserId, DateTime upTo)
    {
        if (!string.Equals(byUserId, _chatFor, StringComparison.Ordinal))
            return;
        var seen = Later(_seenUpTo, upTo);
        if (seen == _seenUpTo)
            return;
        _seenUpTo = seen;
        _paneDirty = true;
    }

    private DateTime? NewestSeen(List<ChatMessageInfo> messages)
    {
        string self = SelfId ?? string.Empty;
        DateTime? newest = null;
        foreach (var message in messages)
        {
            if (message.ReadAt != null && string.Equals(message.SenderId, self, StringComparison.Ordinal))
                newest = Later(newest, message.CreatedAt);
        }
        return newest;
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
    {
        if (a == null)
            return b?.ToUniversalTime();
        if (b == null)
            return a;
        var bu = b.Value.ToUniversalTime();
        return bu > a.Value ? bu : a;
    }

    // A message comes back from the send and may also arrive over the hub; whichever lands second is dropped.
    private bool Append(ChatMessageInfo message)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_messages[i].Id, message.Id, StringComparison.Ordinal))
                return false;
        }
        _messages.Add(message);
        return true;
    }

    private void SendTyped(string text)
    {
        string person = _personId;
        string body = (text ?? string.Empty).Trim();
        if (body.Length == 0 || person.Length == 0 || RelationOf(person) != SocialRelations.Friend)
            return;
        if (body.Length > SocialLimits.MessageMax)
            body = body.Substring(0, SocialLimits.MessageMax);
        if (_composeInput != null)
        {
            _composeInput.Text.Value = string.Empty;
            // Enter let go of the keyboard; a chat keeps it, so the next line can be typed straight away.
            var input = _composeInput;
            World?.RunInUpdates(1, () =>
            {
                if (!input.IsDestroyed && _chatVisible)
                    input.Focus();
            });
        }
        // what comes next is a new message, so its first key pings as soon as the service would take one
        _typedRearmed = true;
        Queue(person, new ChatMessageInfo { Kind = "Text", Body = body });
    }

    private void SendItem(InventoryEntry entry)
    {
        string person = _personId;
        if (person.Length == 0)
            return;
        var item = new ChatItemInfo
        {
            Name = entry.Name,
            Kind = entry.Kind == InventoryEntryKind.World ? Inventory.WorldKind
                : entry.ItemKind.Length > 0 ? entry.ItemKind : Inventory.ObjectKind,
            FileHash = entry.Hash,
            ThumbnailHash = PeopleFormat.IsHash(entry.ThumbnailHash) ? entry.ThumbnailHash : null,
            FileBytes = entry.SizeBytes,
            WorldMode = entry.Kind == InventoryEntryKind.World ? Inventory.WorldModeOrNull(entry.ItemKind) : null,
        };
        Queue(person, new ChatMessageInfo { Kind = "Item", Item = item });
    }

    private void Queue(string person, ChatMessageInfo draft)
    {
        string? self = SelfId;
        if (Social == null || string.IsNullOrEmpty(self))
            return;
        var outgoing = new Outgoing
        {
            Person = person,
            Message = draft with { Id = LocalPrefix + (++_localIds), SenderId = self!, RecipientId = person, CreatedAt = DateTime.UtcNow },
            State = SendState.Sending,
        };
        _outbox.Add(outgoing);
        ShowOutgoing(outgoing);
        Post(outgoing);
    }

    // At the foot of the open chat, which the view follows down to.
    private void ShowOutgoing(Outgoing outgoing)
    {
        if (!string.Equals(outgoing.Person, _chatFor, StringComparison.Ordinal))
            return;
        _messages.Add(outgoing.Message);
        _followTail = true;
        _pinTail = 4;
        _paneDirty = true;
    }

    private async void Post(Outgoing outgoing)
    {
        var social = Social;
        var world = World;
        var message = outgoing.Message;
        ApiResponse<ChatMessageInfo> result;
        try
        {
            result = social == null ? ApiResponse<ChatMessageInfo>.Fail(HttpStatusCode.Unauthorized, "Not signed in")
                : message.Item is { } item ? await social.SendItemAsync(outgoing.Person, item)
                : await social.SendTextAsync(outgoing.Person, message.Body);
        }
        catch (Exception ex)
        {
            result = ApiResponse<ChatMessageInfo>.Fail(HttpStatusCode.InternalServerError, ex.Message);
        }
        OnUi(world, () => Posted(outgoing, result));
    }

    // The row says what became of it; the reason the service gave stays off the page, the edge and the
    // status are enough to act on.
    private void Posted(Outgoing outgoing, ApiResponse<ChatMessageInfo> result)
    {
        if (IsDestroyed)
            return;
        if (result.Success && result.Data != null)
        {
            // already out of the outbox when the feed's copy got here first
            if (_outbox.Remove(outgoing))
            {
                if (outgoing.Withdrawn)
                    TakeBack(outgoing, result.Data);
                else
                    Settle(outgoing, result.Data);
            }
            if (!outgoing.Withdrawn)
                _paneError = null;
        }
        else if (_outbox.Contains(outgoing))
        {
            // removed on its way and it never went: nothing left to do
            if (outgoing.Withdrawn)
                _outbox.Remove(outgoing);
            else
                outgoing.State = SendState.Failed;
        }
        if (string.Equals(outgoing.Person, _chatFor, StringComparison.Ordinal))
            _paneDirty = true;
    }

    // The feed's copy of one of ours: the oldest row still on its way with the same words, or the same
    // thing, gives way to it. True when it is taken care of and must not be added again; one removed on its
    // way is taken back rather than shown.
    private bool Claim(ChatMessageInfo real)
    {
        for (int i = 0; i < _outbox.Count; i++)
        {
            var outgoing = _outbox[i];
            if (outgoing.State != SendState.Sending || !string.Equals(outgoing.Person, real.RecipientId, StringComparison.Ordinal)
                || !SameContent(outgoing.Message, real))
                continue;
            _outbox.RemoveAt(i);
            if (outgoing.Withdrawn)
            {
                TakeBack(outgoing, real);
                return true;
            }
            return Settle(outgoing, real);
        }
        return false;
    }

    private static bool SameContent(ChatMessageInfo local, ChatMessageInfo real)
        => !real.IsInvite && (local.Item is { } item
            ? real.Item != null && string.Equals(item.FileHash, real.Item.FileHash, StringComparison.Ordinal)
            : real.Item == null && string.Equals(local.Body, real.Body, StringComparison.Ordinal));

    // The local row takes the service's copy in its place, or just goes when that copy is already shown.
    private bool Settle(Outgoing outgoing, ChatMessageInfo real)
    {
        int at = IndexOfMessage(outgoing.Message.Id);
        if (at < 0)
            return false;
        if (IndexOfMessage(real.Id) >= 0)
            _messages.RemoveAt(at);
        else
            _messages[at] = real;
        return true;
    }

    // Goes again as the newest, where it would have sat had it gone the first time.
    private void Retry(string localId)
    {
        var outgoing = FindOutgoing(localId);
        if (outgoing == null || outgoing.State != SendState.Failed || RelationOf(outgoing.Person) != SocialRelations.Friend)
            return;
        int at = IndexOfMessage(localId);
        if (at >= 0)
            _messages.RemoveAt(at);
        _outbox.Remove(outgoing);
        outgoing.Message = outgoing.Message with { CreatedAt = DateTime.UtcNow };
        outgoing.State = SendState.Sending;
        _outbox.Add(outgoing);
        ShowOutgoing(outgoing);
        _paneDirty = true;
        Post(outgoing);
    }

    // Cleared on this side: whatever never went goes with it, and anything still on its way lands as usual.
    private void ClearedChat(string person)
    {
        _messages.Clear();
        _outbox.RemoveAll(o => o.State == SendState.Failed && string.Equals(o.Person, person, StringComparison.Ordinal));
    }

    private Outgoing? FindOutgoing(string id)
    {
        if (!id.StartsWith(LocalPrefix, StringComparison.Ordinal))
            return null;
        for (int i = 0; i < _outbox.Count; i++)
        {
            if (string.Equals(_outbox[i].Message.Id, id, StringComparison.Ordinal))
                return _outbox[i];
        }
        return null;
    }

    private int IndexOfMessage(string id)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_messages[i].Id, id, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static bool IsLocal(ChatMessageInfo message) => message.Id.StartsWith(LocalPrefix, StringComparison.Ordinal);

    // Another account, or none: nothing of the last one's chats carries over.
    private void ForgetChatState()
    {
        _outbox.Clear();
        _messages.RemoveAll(IsLocal);
        _deleting.Clear();
        _inviteStates.Clear();
        DropHold();
        _heldRelease = null;
        _typingUntil.Clear();
        _typedTo = string.Empty;
        _typedAt = double.NegativeInfinity;
        _typingShown = false;
    }

    // HOLDING A MESSAGE
    // A press kept on a message for HoldSeconds opens the same menu as its "...": the "..." is for a pointer
    // that hovers, the hold for a finger or a phone screen that never does. The release that ends a hold is no
    // press, so holding a failed message does not send it again on the way. -xlinka
    private const double HoldSeconds = 0.5;
    private Button? _held;
    private ListingRow? _heldRow;
    private string? _heldId;
    private double _heldSince;
    private Button? _heldRelease;

    internal void HoldDown(Button button, ListingRow row)
    {
        _heldRelease = null;
        _held = button;
        _heldRow = row;
        _heldId = HeldMessage(row.Item)?.Id;
        _heldSince = Clock;
    }

    internal void HoldUp(Button button)
    {
        if (ReferenceEquals(_held, button))
            DropHold();
    }

    // The release that ended a hold, once; every other release is a press as usual.
    internal bool TakeHeldRelease(Button button)
    {
        if (!ReferenceEquals(_heldRelease, button))
            return false;
        _heldRelease = null;
        return true;
    }

    private void DropHold()
    {
        _held = null;
        _heldRow = null;
        _heldId = null;
    }

    private static ChatMessageInfo? HeldMessage(ListingItem? item) => item switch
    {
        PeopleMessageItem typed => typed.Message,
        PeopleItemMessageItem sent => sent.Message,
        PeopleInviteMessageItem invite => invite.Message,
        _ => null,
    };

    private void TickHold()
    {
        var held = _held;
        if (held == null)
            return;
        // let go, handed to the list as a scroll, or the row now shows another message
        var message = HeldMessage(_heldRow?.Item);
        if (held.IsDestroyed || !held.IsPressedNow || message == null || !string.Equals(message.Id, _heldId, StringComparison.Ordinal))
        {
            DropHold();
            return;
        }
        if (Clock - _heldSince < HoldSeconds)
            return;
        DropHold();
        _heldRelease = held;
        OpenMessageMenu(message);
    }

    // THE MESSAGE MENU
    // What can be done with one message, said in words in a dialog, so nothing goes on a slip: Delete for me on
    // any message, Delete for both as well on ours, and Remove on ours that never reached the service.
    private const int QuoteMax = 56;

    private void OpenMessageMenu(ChatMessageInfo message)
    {
        string person = _chatFor;
        string id = message.Id;
        if (person.Length == 0 || IndexOfMessage(id) < 0 || ModalHost.For(Slot) is not { } host)
            return;
        string name = ShownName();
        string quote = Quote(message);
        bool mine = string.Equals(message.SenderId, SelfId, StringComparison.Ordinal);
        var outgoing = FindOutgoing(id);
        var acts = new List<Action>();
        var request = new ModalRequest { Invoker = Slot, CancelIndex = 0 };
        request.AddOption(PeopleLocale.Cancel);
        if (outgoing != null)
        {
            request.Title = PeopleLocale.ChatRemoveTitle;
            request.Message = outgoing.State == SendState.Failed
                ? PeopleLocale.ChatRemoveNotSent(quote, name) : PeopleLocale.ChatRemoveSending(quote, name);
            request.AddOption(PeopleLocale.ChatRemove, ModalOptionStyle.Destructive);
            acts.Add(() => RemoveLocal(id));
        }
        else if (mine)
        {
            request.Title = PeopleLocale.ChatDeleteTitle;
            request.Message = message.IsItem ? PeopleLocale.ChatDeleteMineItem(quote, name) : PeopleLocale.ChatDeleteMine(quote, name);
            request.AddOption(PeopleLocale.ChatDeleteForMe);
            acts.Add(() => DeleteMessage(person, message, forBoth: false));
            request.AddOption(PeopleLocale.ChatDeleteForBoth, ModalOptionStyle.Destructive);
            acts.Add(() => DeleteMessage(person, message, forBoth: true));
        }
        else
        {
            request.Title = PeopleLocale.ChatDeleteTitle;
            request.Message = PeopleLocale.ChatDeleteTheirs(quote, name);
            request.AddOption(PeopleLocale.ChatDeleteForMe, ModalOptionStyle.Destructive);
            acts.Add(() => DeleteMessage(person, message, forBoth: false));
        }
        request.Completed = result =>
        {
            // an answer for a chat that has moved on, or for a message gone meanwhile, does nothing
            int at = result.Index - 1;
            if (IsDestroyed || at < 0 || at >= acts.Count || !string.Equals(person, _chatFor, StringComparison.Ordinal) || IndexOfMessage(id) < 0)
                return;
            acts[at]();
        };
        host.Open(request);
    }

    // The message on one line, cut short past QuoteMax at a word when one ends near there; an item by its name.
    private static string Quote(ChatMessageInfo message)
    {
        string text = message.Item is { } item ? item.Name : message.Invite is { } invite ? invite.WorldName : message.Body;
        string flat = string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length > QuoteMax)
        {
            int cut = QuoteMax - 1;
            int word = flat.LastIndexOf(' ', cut);
            if (word > cut - 14)
                cut = word;
            else if (char.IsHighSurrogate(flat[cut - 1]))
                cut--;
            flat = flat.Substring(0, cut).TrimEnd() + "…";
        }
        return "“" + flat + "”";
    }

    // ONE MESSAGE GOING
    // Out of the chat the moment it is chosen. The answer matters only when it says no: the message then comes
    // back where it was, with a line saying it could not go. Until the answer it stays out of a reload too.
    private sealed class Deleting
    {
        public required string Person;
        public required ChatMessageInfo Message;
        // the feed said it went meanwhile, so a refusal puts nothing back
        public bool Gone;
    }

    private readonly Dictionary<string, Deleting> _deleting = new(StringComparer.Ordinal);

    private async void DeleteMessage(string person, ChatMessageInfo message, bool forBoth)
    {
        var social = Social;
        if (social == null || person.Length == 0 || _deleting.ContainsKey(message.Id))
            return;
        var deleting = new Deleting { Person = person, Message = message };
        _deleting[message.Id] = deleting;
        Unshow(person, message.Id);
        var world = World;
        ApiResponse result;
        try
        {
            result = await social.DeleteMessageAsync(person, message.Id, forBoth);
        }
        catch (Exception ex)
        {
            result = ApiResponse.Fail(HttpStatusCode.InternalServerError, ex.Message);
        }
        OnUi(world, () => Deleted(deleting, result));
    }

    private void Deleted(Deleting deleting, ApiResponse result)
    {
        // gone with the account it was made for
        if (IsDestroyed || !_deleting.TryGetValue(deleting.Message.Id, out var known) || !ReferenceEquals(known, deleting))
            return;
        _deleting.Remove(deleting.Message.Id);
        // a chat not on show reads it back from the service when it next opens
        if (result.Success || deleting.Gone || !string.Equals(deleting.Person, _chatFor, StringComparison.Ordinal))
            return;
        if (IndexOfMessage(deleting.Message.Id) < 0)
            PutBack(deleting.Message);
        _paneError = PeopleLocale.ChatDeleteFailed.Resolve();
        _paneNotice = null;
        _paneDirty = true;
    }

    // Ours that never reached the service go from here only. One still on its way is marked, and if it lands
    // after all it is taken back from both sides as soon as the service names it.
    private void RemoveLocal(string localId)
    {
        var outgoing = FindOutgoing(localId);
        if (outgoing == null)
            return;
        Unshow(outgoing.Person, localId);
        if (outgoing.State == SendState.Failed)
            _outbox.Remove(outgoing);
        else
            outgoing.Withdrawn = true;
    }

    private void TakeBack(Outgoing outgoing, ChatMessageInfo real) => DeleteMessage(outgoing.Person, real, forBoth: true);

    // Gone on a machine of this account, this one included, or taken back by whoever sent it.
    private void OnMessageDeleted(ChatMessageDeletedInfo deleted)
    {
        if (_deleting.TryGetValue(deleted.MessageId, out var pending))
            pending.Gone = true;
        if (!string.Equals(deleted.WithUserId, _chatFor, StringComparison.Ordinal))
            return;
        int at = IndexOfMessage(deleted.MessageId);
        if (at < 0)
            return;
        _messages.RemoveAt(at);
        _headerDirty = true;
        if (_pane == PaneView.Person && _tab == PersonTab.Chat)
            _paneDirty = true;
    }

    private void Unshow(string person, string id)
    {
        if (!string.Equals(person, _chatFor, StringComparison.Ordinal))
            return;
        int at = IndexOfMessage(id);
        if (at < 0)
            return;
        _messages.RemoveAt(at);
        _paneDirty = true;
    }

    // Back where it sat by time, ahead of anything of ours still on its way.
    private void PutBack(ChatMessageInfo message)
    {
        var at = message.CreatedAt.ToUniversalTime();
        int index = _messages.Count;
        for (int i = 0; i < _messages.Count; i++)
        {
            var other = _messages[i];
            var when = other.CreatedAt.ToUniversalTime();
            if (IsLocal(other) || when > at || (when == at && string.CompareOrdinal(other.Id, message.Id) > 0))
            {
                index = i;
                break;
            }
        }
        _messages.Insert(index, message);
    }

    // THE DOTS

    private void OnComposeTyped(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _typedRearmed = true;
            return;
        }
        string person = _chatFor;
        if (!_chatVisible || _composeInput?.IsFocused != true || person.Length == 0
            || !string.Equals(person, _personId, StringComparison.Ordinal) || RelationOf(person) != SocialRelations.Friend)
            return;
        double now = Clock;
        if (string.Equals(person, _typedTo, StringComparison.Ordinal)
            && now - _typedAt < (_typedRearmed ? TypingRearmSeconds : TypingPingSeconds))
            return;
        _typedTo = person;
        _typedAt = now;
        _typedRearmed = false;
        _ = Social?.SendTypingAsync(person);
    }

    // The service passes on friends only; asked again here, so a block made a moment ago holds at once.
    private void OnTyping(string fromUserId)
    {
        if (string.IsNullOrEmpty(fromUserId) || RelationOf(fromUserId) != SocialRelations.Friend)
            return;
        bool was = IsTyping(fromUserId);
        _typingUntil[fromUserId] = Clock + TypingShowSeconds;
        if (!was)
            TypingMoved(fromUserId);
    }

    private bool IsTyping(string userId) => _typingUntil.TryGetValue(userId, out var until) && until > Clock;

    private void TypingMoved(string userId)
    {
        if (_pane != PaneView.Person || !string.Equals(userId, _personId, StringComparison.Ordinal))
            return;
        _headerDirty = true;
        if (_tab != PersonTab.Chat)
            return;
        _paneDirty = true;
        if (_followTail)
            _pinTail = 4;
    }

    private void TickTyping()
    {
        if (_typingUntil.Count == 0)
            return;
        double now = Clock;
        foreach (var pair in _typingUntil)
        {
            if (pair.Value <= now)
                _typingLapsed.Add(pair.Key);
        }
        if (_typingLapsed.Count > 0)
        {
            foreach (var id in _typingLapsed)
            {
                _typingUntil.Remove(id);
                TypingMoved(id);
            }
            _typingLapsed.Clear();
        }
        if (!_typingShown || !_chatVisible || _pane != PaneView.Person || _tab != PersonTab.Chat)
            return;
        long step = (long)(now / TypingStepSeconds);
        for (int i = 0; i < _typingDots.Count; i++)
            _typingDots[i].Animate(step);
    }

    private void BuildChatItems()
    {
        _typingShown = false;
        var shown = Shown();
        string relation = RelationOf(_personId, shown?.Relation);
        if (relation != SocialRelations.Friend)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("chat.closed", PeopleLocale.ChatNotFriends) { Centered = true, Height = 44f });
            var actions = new PeopleActionsItem("chat.join");
            if (relation == SocialRelations.None)
                actions.Add(PeopleLocale.AddFriend.Resolve(), PeopleAction.AddFriend, lit: true);
            else if (relation == SocialRelations.RequestReceived)
                actions.Add(PeopleLocale.Accept.Resolve(), PeopleAction.Accept, lit: true);
            if (actions.Buttons.Count > 0)
                _bodyItems.AddRoot(actions);
            return;
        }

        if (!string.Equals(_chatFor, _personId, StringComparison.Ordinal) || _chatLoad == LoadState.Loading)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("chat.loading", PeopleLocale.Loading));
            return;
        }
        if (_chatHasOlder)
        {
            _bodyItems.AddRoot(new PeopleActionsItem("chat.older").Add(PeopleLocale.ChatOlder.Resolve(), PeopleAction.LoadOlder, width: 150f));
        }
        if (_messages.Count == 0)
        {
            _bodyItems.AddRoot(new PeopleNoteItem("chat.empty", PeopleLocale.ChatEmpty) { Centered = true, Height = 60f });
            AddTypingItem();
            return;
        }

        string self = SelfId ?? string.Empty;
        float width = BodyWidth();
        float bubbleMax = MathF.Max(160f, width * PeopleMetrics.BubbleShare);
        DateTime lastDay = DateTime.MinValue;
        // Seen sits under the newest of ours they have read; the newest of ours that went, or is going, says
        // how far it got if that is something else. Older ones keep just their time, and one that did not go
        // says so wherever it is.
        int seenAt = -1;
        int newestMine = -1;
        for (int i = 0; i < _messages.Count; i++)
        {
            if (!string.Equals(_messages[i].SenderId, self, StringComparison.Ordinal))
                continue;
            if (FindOutgoing(_messages[i].Id)?.State != SendState.Failed)
                newestMine = i;
            if (_seenUpTo is DateTime seenUpTo && !IsLocal(_messages[i]) && _messages[i].CreatedAt.ToUniversalTime() <= seenUpTo)
                seenAt = i;
        }
        for (int i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var day = (message.CreatedAt.Kind == DateTimeKind.Utc ? message.CreatedAt.ToLocalTime() : message.CreatedAt).Date;
            if (day != lastDay)
            {
                lastDay = day;
                _bodyItems.AddRoot(new PeopleDayItem("day." + day.Ticks, PeopleFormat.DayLabel(message.CreatedAt)));
            }
            bool mine = string.Equals(message.SenderId, self, StringComparison.Ordinal);
            var outgoing = mine ? FindOutgoing(message.Id) : null;
            bool failed = outgoing?.State == SendState.Failed;
            bool sending = outgoing?.State == SendState.Sending;
            // a card's pill already says Retry; a bubble is pressed itself, so its line says so
            string? status = failed ? (message.IsItem ? PeopleLocale.ChatNotSent.Resolve()
                    : PeopleLocale.ChatNotSent.Resolve() + "  ·  " + PeopleLocale.ChatRetry.Resolve())
                : i == seenAt ? PeopleLocale.ChatSeen.Resolve()
                : i == newestMine ? (sending ? PeopleLocale.ChatSending : PeopleLocale.ChatSent).Resolve()
                : null;
            string meta = PeopleFormat.ClockTime(message.CreatedAt);
            if (status != null)
                meta += "  ·  " + status;
            if (message.IsInvite)
            {
                _bodyItems.AddRoot(InviteItem(message, mine, meta));
                continue;
            }
            if (message.IsItem)
            {
                bool expired = message.ClaimableUntil is DateTime until && until < DateTime.UtcNow;
                _bodyItems.AddRoot(new PeopleItemMessageItem("msg." + message.Id)
                {
                    Message = message,
                    Mine = mine,
                    Meta = meta,
                    Expired = expired,
                    IsWorld = string.Equals(message.Item!.Kind, Inventory.WorldKind, StringComparison.OrdinalIgnoreCase),
                    Kept = _kept.Contains(message.Id),
                    Sending = sending,
                    Failed = failed,
                });
                continue;
            }
            var (height, bubble) = BubbleSize(message.Body, bubbleMax);
            // a short line still leaves room for its time and status under it
            if (status != null)
                bubble = MathF.Min(bubbleMax, MathF.Max(bubble, MetaWidth(meta)));
            _bodyItems.AddRoot(new PeopleMessageItem("msg." + message.Id)
            {
                Message = message,
                Mine = mine,
                Text = message.Body,
                Meta = meta,
                Height = height,
                BubbleWidth = bubble,
                Sending = sending,
                Failed = failed,
            });
        }
        AddTypingItem();
    }

    private void AddTypingItem()
    {
        _typingShown = IsTyping(_personId);
        if (_typingShown)
            _bodyItems.AddRoot(new PeopleTypingItem("typing"));
    }

    private static float MetaWidth(string meta)
        => MathF.Max(SeenBubbleMin, meta.Length * (DashTheme.FontLabel - 1f) * 0.52f + PeopleMetrics.BubblePadX * 2f + 6f);

    // How tall and how wide a bubble is for its text: one short line hugs its words, anything longer takes
    // the full share of the pane and wraps.
    private static (float Height, float Width) BubbleSize(string text, float maxWidth)
    {
        float size = PeopleMetrics.BubbleFont;
        float inner = maxWidth - PeopleMetrics.BubblePadX * 2f;
        int perLine = System.Math.Max(8, (int)(inner / (size * PeopleMetrics.GlyphWidth)));
        int lines = 0;
        int longest = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            lines += System.Math.Max(1, (paragraph.Length + perLine - 1) / perLine);
            longest = System.Math.Max(longest, paragraph.Length);
        }
        float textHeight = lines * size * PeopleMetrics.LineHeight;
        float height = textHeight + PeopleMetrics.BubblePadY * 2f + PeopleMetrics.BubbleMetaHeight + 2f;
        float width = lines > 1 ? maxWidth
            : MathF.Min(maxWidth, MathF.Max(84f, longest * size * 0.47f + PeopleMetrics.BubblePadX * 2f + 8f));
        return (height, width);
    }

    // Follow the end: while the reader is at the bottom, the bottom is where the view stays, however the
    // rows grow under it (they land a frame or two after the message, and a window that is still opening
    // has no height to scroll yet). Scrolling up by hand lets go; coming back down to the end takes hold
    // again. -xlinka
    private void TickChat()
    {
        if (_body?.Scroll is not { IsDestroyed: false } scroll)
            return;
        if (!_chatVisible || _pane != PaneView.Person || _tab != PersonTab.Chat)
        {
            _pinTail = 0;
            return;
        }
        if (_pinTail > 0)
        {
            _pinTail--;
            _followTail = true;
        }
        if (!_followTail || !Overflows() || scroll.NormalizedPosition.y >= 0.999f)
            return;
        _applyingScroll = true;
        try
        {
            scroll.NormalizedPosition = new float2(scroll.NormalizedPosition.x, 1f);
            _pinnedAt = scroll.AbsolutePosition.y;
        }
        finally
        {
            _applyingScroll = false;
        }
        _bodyBar?.Refresh();
    }

    private float _pinnedAt = -1f;

    private bool Overflows()
    {
        var view = _body;
        if (view?.ContentSlot == null)
            return false;
        float content = view.ContentSlot.GetComponent<RectTransform>()?.LocalComputeRect.height ?? 0f;
        float viewport = view.Slot.GetComponent<RectTransform>()?.LocalComputeRect.height ?? 0f;
        return viewport > 1f && content > viewport + 1f;
    }

    // Only a move the reader made counts. The scrollbar parking a short list at the top, or rows growing
    // under a pinned view, leave the position where it was pinned and are not a reader scrolling away.
    private void OnBodyScrolled(ScrollRect scroll, float2 position)
    {
        if (_applyingScroll || !_chatVisible || _tab != PersonTab.Chat || !Overflows())
            return;
        if (_pinnedAt >= 0f && MathF.Abs(scroll.AbsolutePosition.y - _pinnedAt) < 0.5f)
            return;
        _followTail = scroll.NormalizedPosition.y >= TailSnap;
        if (_followTail)
            _pinnedAt = scroll.AbsolutePosition.y;
    }

    // SPAWNING WHAT WAS SENT
    // Fetched by its hash the way the inventory fetches a save, then loaded in front of you with the same
    // readout; a world opens instead. -xlinka
    private bool _spawning;

    private void SpawnFromChat(PeopleItemMessageItem sent)
    {
        var info = sent.Message.Item;
        var world = Engine.Current?.WorldManager?.FocusedWorld;
        if (info == null || sent.Expired || _spawning || world == null)
            return;
        var entry = new InventoryEntry("chat-" + sent.Message.Id, info.Name,
            sent.IsWorld ? InventoryEntryKind.World : InventoryEntryKind.Item, sent.Message.CreatedAt, info.FileBytes, 0,
            sent.IsWorld ? info.WorldMode ?? string.Empty : info.Kind, info.FileHash, info.ThumbnailHash, Inventory.Root, null);

        object? readout = null;
        if (!sent.IsWorld)
        {
            readout = ModelImportIndicator.Show(world, null, SpawnLocale.Spawning, info.Name);
            ModelImportIndicator.Report(new ImportProgress(ImportStage.Fetching, 0.1f), readout);
        }
        _spawning = true;
        var own = World;
        _ = InventorySource.FetchToCache(entry).ContinueWith(t =>
        {
            string? path = t.Status == TaskStatus.RanToCompletion ? t.Result.path : null;
            string? problem = t.Status == TaskStatus.RanToCompletion ? t.Result.problem : t.Exception?.GetBaseException().Message;
            own?.RunSynchronously(() =>
            {
                _spawning = false;
                if (IsDestroyed)
                    return;
                if (path == null)
                {
                    RefuseSpawn(problem ?? PeopleLocale.ActionFailed.Resolve(), readout);
                    return;
                }
                if (sent.IsWorld)
                {
                    var opened = Engine.Current?.WorldManager?.OpenSavedWorld(path, info.Name, fromCloudRecord: true);
                    if (opened == null)
                        RefuseSpawn(PeopleLocale.SpawnFailed(info.Name).Resolve(), readout);
                    return;
                }
                var target = Engine.Current?.WorldManager?.FocusedWorld ?? world;
                if (target.IsDestroyed)
                {
                    RefuseSpawn(PeopleLocale.ActionFailed.Resolve(), readout);
                    return;
                }
                if (!target.AllowsItemSpawning
                    || target.DataModelPermissions?.AllowsDomain(target.LocalUser, DataModelPermissionDomain.Spawn) == false)
                {
                    RefuseSpawn(PeopleLocale.SpawnRefused.Resolve(), readout);
                    return;
                }
                var spawned = Inventory.LoadItem(target, path, info.Name);
                if (spawned == null)
                {
                    RefuseSpawn(PeopleLocale.SpawnFailed(info.Name).Resolve(), readout);
                    return;
                }
                var root = target.LocalUser?.Root;
                spawned.GlobalPosition = root?.HeadSlot != null
                    ? root.HeadPosition + root.HeadRotation * (float3.Backward * 1.0f)
                    : new float3(0f, 1f, 0f);
                InspectorUndo.Record(target, SlotExistenceUndoBatch.Created(target, new[] { spawned }, UndoLocale.SpawnItem));
                ModelImportIndicator.Follow(readout, spawned, SpawnLocale.InFront, SpawnLocale.Spawned);
                _paneNotice = PeopleLocale.Spawned(info.Name).Resolve();
                _paneError = null;
                _paneDirty = true;
            });
        });
    }

    // KEEPING WHAT WAS SENT
    // Into the folder the inventory has open, like any save, so it outlives the chat's 30 days. -xlinka
    private readonly HashSet<string> _kept = new(StringComparer.Ordinal);
    private bool _keeping;

    private void KeepFromChat(PeopleItemMessageItem sent)
    {
        var info = sent.Message.Item;
        if (info == null || sent.Mine || sent.Expired || _keeping || _kept.Contains(sent.Message.Id))
            return;
        var entry = new InventoryEntry("chat-" + sent.Message.Id, info.Name,
            sent.IsWorld ? InventoryEntryKind.World : InventoryEntryKind.Item, sent.Message.CreatedAt, info.FileBytes, 0,
            sent.IsWorld ? info.WorldMode ?? string.Empty : info.Kind, info.FileHash, info.ThumbnailHash, Inventory.Root, null);
        _keeping = true;
        string id = sent.Message.Id;
        var own = World;
        _ = InventorySource.KeepSentHere(entry).ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result.Ok;
            string? why = t.Status == TaskStatus.RanToCompletion ? t.Result.Message : t.Exception?.GetBaseException().Message;
            own?.RunSynchronously(() =>
            {
                _keeping = false;
                if (IsDestroyed)
                    return;
                if (ok)
                {
                    _kept.Add(id);
                    _paneNotice = PeopleLocale.KeptNotice(info.Name).Resolve();
                    _paneError = null;
                }
                else
                {
                    _paneError = string.IsNullOrWhiteSpace(why) ? PeopleLocale.ActionFailed.Resolve() : why;
                    _paneNotice = null;
                }
                _paneDirty = true;
            });
        });
    }

    private void RefuseSpawn(string why, object? readout)
    {
        _paneError = why;
        _paneNotice = null;
        _paneDirty = true;
        if (readout != null)
            ModelImportIndicator.Fail(LocaleText.Plain(why), readout);
    }
}
