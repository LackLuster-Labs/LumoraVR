// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Input;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Networking.Streams;
using Lumora.Warden;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public enum HeadOutputDevice
{
    Server,
    Screen,
    VR,
    Camera
}

public enum Platform
{
    Windows,
    Linux,
    Android,
    Other
}

public class User : ContainerWorker<UserComponent>, ISyncObject, IDisposable, IPermissionActor
{
    private List<ISyncMember> _syncMembers = new();
    private readonly Dictionary<BodyNode, TrackingStreamPair> _trackingStreams = new();
    private bool _trackingStreamsInitialized;
    private readonly HashSet<IStream> _justAddedStreams = new();

    private const string TrackingGroupName = "Tracking";
    private static readonly BodyNode[] TrackingNodes =
    {
        // Root carries the whole-body translation (walking/locomotion). It MUST be streamed like the body
        // nodes - otherwise the user-root position can only replicate over the permission-gated delta channel
        // and a joined user stays frozen in place for everyone else. -xlinka
        BodyNode.Root,
        BodyNode.Head,
        BodyNode.LeftController,
        BodyNode.RightController
    };

    private struct TrackingStreamPair
    {
        public Float3ValueStream Position;
        public FloatQValueStream Rotation;

        public TrackingStreamPair(Float3ValueStream position, FloatQValueStream rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    public readonly Sync<string> UserName = new();
    public readonly Sync<string> UserID = new();
    public readonly Sync<string> MachineID = new();
    // The verified platform account id, set by the host only after the joiner proves account
    // ownership via a signed challenge. Empty for guests. This is the STABLE id to key things like bans
    // and friends on, unlike UserID which is the per-session allocator id. -xlinka
    public readonly Sync<string> AccountId = new();
    public readonly Sync<ulong> AllocationIDStart = new();
    public readonly Sync<ulong> AllocationIDEnd = new();
    public readonly Sync<byte> AllocationID = new();

    // The host's second byte, reserved when the host user is made. While the world is frozen what the host
    // makes for itself is minted here, so it reads as the host's own the way a guest's things read as
    // theirs, and the authority byte stays the world's. 0 for every other user. -xlinka
    public readonly Sync<byte> ItemAllocationID = new();
    public readonly Sync<int> Ping = new();
    public readonly Sync<bool> IsPresent = new();
    public readonly Sync<bool> IsSilenced = new();
    public readonly Sync<bool> IsMuted = new();
    public readonly Sync<Audio.VoiceMode> voiceMode = new(Audio.VoiceMode.Normal);
    public readonly Sync<bool> VRActive = new();
    public readonly Sync<bool> PresentInWorld = new();
    public readonly Sync<bool> PresentInHeadset = new();
    public readonly Sync<float> FPS = new();
    public readonly Sync<HeadOutputDevice> HeadDevice = new();
    public readonly Sync<Platform> UserPlatform = new();
    public readonly Sync<float> DownloadSpeed = new();
    public readonly Sync<float> UploadSpeed = new();
    public readonly Sync<ulong> DownloadedBytes = new();
    public readonly Sync<ulong> UploadedBytes = new();

    // Written by the user's own machine with the fields above: the highest rates this session, what it
    // runs, whether their dash is open and which camera a screen user looks through.
    public readonly Sync<float> DownloadSpeedMax = new();
    public readonly Sync<float> UploadSpeedMax = new();
    public readonly Sync<string> EngineVersion = new();
    public readonly Sync<string> RuntimeVersion = new();
    public readonly Sync<bool> DashOpen = new();
    public readonly Sync<Components.DesktopViewMode> ViewMode = new();
    public readonly Sync<Input.Chirality> PrimaryHand = new(Input.Chirality.Right);
    // Streaming, recording, or showing a camera on their screen: everyone sees a live mark on them.
    public readonly Sync<bool> IsLive = new();

    // The host's side of its link to this user, written by the host once a second: the share of packets
    // it had to send again, reliable packets still waiting to go out, what arrived from them in the last
    // second by kind, and lagging once nothing has arrived for LagSeconds while their world is in front.
    // Zero for the host's own user. -xlinka
    public readonly Sync<float> PacketLoss = new();
    public readonly Sync<int> QueuedMessages = new();
    public readonly Sync<int> DeltaMessages = new();
    public readonly Sync<int> StreamMessages = new();
    public readonly Sync<int> ControlMessages = new();
    public readonly Sync<bool> IsLagging = new();

    // THE GROUP THIS USER IS WEARING
    //
    // Host written: the account service says which group an account wears, and the host reads it from the
    // public record of the account the user proved, alongside the name. A peer never writes its own card,
    // so nobody can wear a group they are not in. A guest carries empty strings and a clear colour, which
    // is what the nametag reads as "no card". -xlinka
    public readonly Sync<string> GroupId = new();
    public readonly Sync<string> GroupTag = new();
    public readonly Sync<string> GroupName = new();
    public readonly Sync<string> GroupIconHash = new();
    public readonly Sync<string> GroupBannerHash = new();
    public readonly Sync<string> GroupRole = new();
    public readonly Sync<Math.color> GroupColor = new();

    // THE ACCOUNT THIS USER PROVED
    //
    // Host written, like the name: filled from the accounts service once the account has signed the host's
    // challenge, and cleared when the user signs out. A guest carries a clear colour and no supporter mark.
    // -xlinka
    public readonly Sync<Math.color> NameColor = new();
    public readonly Sync<bool> IsSupporter = new();
    public readonly Sync<string> SupporterTier = new();
    public readonly Sync<Math.color> SupporterColor = new();
    // How the tier's name tag moves ("Animated"), empty for a still one. The game keeps no list of tiers:
    // the tier file on the accounts service says which ones move.
    public readonly Sync<string> SupporterStyle = new();
    // Set by the accounts service's staff on the account record, never by anybody in a session.
    public readonly Sync<bool> IsStaff = new();
    public readonly UserStreamStorage userStreams = new();
    public readonly Sync<uint> streamConfiguration = new();
    // This user's own peer asking for a new body: theirs is gone or broken, or they pressed Respawn. The
    // host builds one and clears it. Kept last so every member before it keeps its place on the wire.
    public readonly Sync<bool> RespawnRequest = new();
    // What this user's own machine sees of the streaming program on it, for the mark over their name. Only that machine writes it, the same as IsLive. Last for the same reason as the one above.
    public readonly Sync<Photos.CaptureState> Capture = new();
    // This user's own machine in the world's chat: whether they are typing a line now, and their last few lines,
    // each with its number (ChatText packs them). Several lines, not one, so a line sent while the one before
    // is still on its way never writes over it before anybody read it. Only that machine writes either. Last
    // for the same reason as the two above. -xlinka
    public readonly Sync<bool> ChatTyping = new();
    public readonly Sync<string> ChatLog = new();

    // Network statistics (non-synced)
    public ulong SentBytes { get; set; }
    public ulong ReceivedBytes { get; set; }
    public DateTime LastSyncMessage { get; set; }
    public int ImmediateDeltaCount { get; set; }
    public int ImmediateControlCount { get; set; }
    public int ImmediateStreamCount { get; set; }

    // Seconds without a message from a user whose world is in front of them before the host calls them lagging.
    public const double LagSeconds = 1.5;

    // What the host has received from this user since it last looked, counted on the decode thread.
    private int _receivedDeltas;
    private int _receivedStreams;
    private int _receivedControls;

    internal void CountReceived(SyncMessage message)
    {
        switch (message)
        {
            case ControlMessage:
                System.Threading.Interlocked.Increment(ref _receivedControls);
                break;
            case StreamMessage or RawFrameMessage:
                System.Threading.Interlocked.Increment(ref _receivedStreams);
                break;
            default:
                System.Threading.Interlocked.Increment(ref _receivedDeltas);
                break;
        }
        LastSyncMessage = DateTime.UtcNow;
    }

    internal void TakeReceived(out int deltas, out int streams, out int controls)
    {
        deltas = System.Threading.Interlocked.Exchange(ref _receivedDeltas, 0);
        streams = System.Threading.Interlocked.Exchange(ref _receivedStreams, 0);
        controls = System.Threading.Interlocked.Exchange(ref _receivedControls, 0);
    }

    public new List<ISyncMember> SyncMembers => _syncMembers;
    public bool IsAuthority => World?.IsAuthority ?? false;

    public override string ParentHierarchyToString() => $"User:{UserName.Value ?? UserID.Value}";

    // The session's host, on every machine. On the host it is its own user; elsewhere the user holding the
    // authority's allocation byte. The byte reads 0 until the host's values arrive, so a user whose range
    // end is still 0 is one not synced yet, never the host. -xlinka
    public bool IsHost => World is { } world && (world.IsAuthority
        ? world.LocalUser == this
        : AllocationIDEnd.Value != 0 && AllocationID.Value == RefIDConstants.AUTHORITY_BYTE);
    public bool IsDisposed { get; private set; }

    // Whether this byte is the host's item byte. Never the authority byte.
    public bool IsItemByte(byte userByte) => RefIDConstants.IsValidUserByte(userByte) && ItemAllocationID.Value == userByte;

    // Whether ids minted in this byte are this user's: the byte it joined on, or the host's item byte.
    public bool HoldsByte(byte userByte) => AllocationID.Value == userByte || IsItemByte(userByte);

    // PERMISSION GATE VIEW
    // The gate identifies a user by the host-assigned allocation byte, never by anything the client
    // sends. AllocationID reads 0 until the host's value syncs across, which is why the gate falls back
    // to the byte baked into the reference id. -xlinka

    ulong IPermissionActor.Id => ReferenceID.RawValue;

    byte IPermissionActor.AllocationByte => AllocationID.Value;

    byte IPermissionActor.ItemAllocationByte => ItemAllocationID.Value;

    string? IPermissionActor.DisplayName => UserName?.Value ?? ReferenceID.ToString();

    IPermissionTarget? IPermissionActor.RootElement => Root?.Slot;

    // Durable identity for persisted role assignments and the denial ledger. Both members are MarkHostOnly
    // (see InitializeFromCollection), so what the gate reads here is what the HOST wrote during the
    // handshake - a client cannot rewrite either to inherit someone else's role or to shed its own denial
    // score by reconnecting. AccountId is empty until an account proved itself; MachineID always has a
    // value. -xlinka
    string? IPermissionActor.MachineKey => MachineID?.Value;

    string? IPermissionActor.AccountKey => AccountId?.Value;

    // This user's own yes, read off the record only their machine writes.
    bool IPermissionActor.AllowsEditsBy(IPermissionActor other)
        => other != null && Networking.Session.SessionEditConsent.Allows(this, other.Id);

    // Host side, from the proven account's record. Null clears the card, which is also what a signed-out
    // or guest user carries. Every write is equality-gated: a Sync field has no gate of its own
    // and this runs on every world the local user is in whenever the cloud client re-reads the profile, so
    // an ungated apply would push six deltas per world for a card that never moved. -xlinka
    public void ApplyRepresentedGroup(Nexus.Cloud.Cdn.RepresentedGroupInfo? group)
    {
        SetIfChanged(GroupId, group?.Id ?? string.Empty);
        SetIfChanged(GroupTag, group?.Tag ?? string.Empty);
        SetIfChanged(GroupName, group?.Name ?? string.Empty);
        SetIfChanged(GroupIconHash, group?.IconHash ?? string.Empty);
        SetIfChanged(GroupBannerHash, group?.BannerHash ?? string.Empty);
        SetIfChanged(GroupRole, group?.Role ?? string.Empty);

        var tint = ParseGroupColor(group?.Color);
        if (!GroupColor.Value.Equals(tint))
            GroupColor.Value = tint;
    }

    public bool IsGuest => string.IsNullOrEmpty(AccountId.Value);

    // Host side. A card is an account the host has seen prove itself; null is a guest, named after the
    // machine key the join proved and never after anything the peer typed. -xlinka
    public void ApplyAccount(AccountCard? card, string? machineKey)
    {
        if (World != null && !World.IsAuthority)
            return;

        SetIfChanged(UserName, card?.Username ?? AccountCard.GuestName(machineKey));
        SetIfChanged(AccountId, card?.AccountId ?? string.Empty);
        SetIfChanged(SupporterTier, card?.SupporterTier ?? string.Empty);
        if (IsSupporter.Value != (card?.IsSupporter ?? false))
            IsSupporter.Value = card?.IsSupporter ?? false;
        var nameColor = card?.NameColor ?? default;
        if (!NameColor.Value.Equals(nameColor))
            NameColor.Value = nameColor;
        var supporterColor = card?.SupporterColor ?? default;
        if (!SupporterColor.Value.Equals(supporterColor))
            SupporterColor.Value = supporterColor;
        SetIfChanged(SupporterStyle, card?.SupporterStyle ?? string.Empty);
        if (IsStaff.Value != (card?.IsStaff ?? false))
            IsStaff.Value = card?.IsStaff ?? false;
        SetIfChanged(AccountKind, card?.AccountKind ?? string.Empty);
        ApplyRepresentedGroup(card?.Group);
    }

    private static void SetIfChanged(Sync<string> field, string value)
    {
        if (field.Value != value)
            field.Value = value;
    }

    // "#RRGGBB" as the service stores it. Anything unparseable is a clear colour, which the nametag reads
    // as "fall back to the plate's own panel tint" rather than painting a card in whatever the garbage
    // happened to decode to.
    private static Math.color ParseGroupColor(string? hex) => AccountCard.ParseHex(hex);

    public void Destroy()
    {
        Dispose();
    }

    // A user does NOT receive replicated traffic (deltas, corrections, streams) until it has been told
    // its full world state is on the wire. Until then it would just be racing its own join, so the
    // authority's fan-out skips it. The flag flips true once we enqueue the JoinStartDelta for that user
    // (see StartTransmittingStreamData call sites), so live updates only ever follow full state.
    // Read on the sync thread, written from the control/connection path - volatile is enough for the
    // visibility guarantee without a lock. -xlinka
    private volatile bool _receivingStreams = false;

    // False while the user is still being initialized; true once it has been handed full state.
    public bool ReceiveStreams => _receivingStreams;

    // Called right after the user's full state + JoinStartDelta are enqueued.
    internal void StartTransmittingStreamData() => _receivingStreams = true;

    internal void StopTransmittingStreamData() => _receivingStreams = false;

    // one stream container per user (userStreams); the old parallel bag was dead state and is gone -xlinka
    public IEnumerable<Stream> Streams => userStreams.Streams;

    public int StreamCount => userStreams.Count;

    public StreamGroupManager StreamGroupManager { get; private set; } = null!;

    public uint StreamConfigurationVersion => streamConfiguration.Value;

    public bool IsLocal => World?.LocalUser == this;

    #region Voice

    // VOICE STATE
    //
    // Three layers, and they are deliberately not the same kind of thing:
    //
    //   voiceMode          replicated, the mode this person CHOSE. Everyone agrees on it.
    //   LocalVoiceModeOverride  local only, never replicated: how THIS machine decides to hear them.
    //   MaxAllowedVoiceMode     derived, recomputed every world frame from the VoicePermission
    //                           components in scope. Not stored, so a permission that goes away takes
    //                           its cap with it instead of leaving a stale ceiling behind.
    //
    // ActiveVoiceMode is what the avatar's output actually plays, and it folds the three together. The
    // one asymmetry worth stating: a local override can only ever make someone QUIETER when they chose
    // Mute or Whisper, because being able to locally promote a whisper to a shout would hand every
    // listener a way to hear what was said quietly on purpose. -xlinka

    public Audio.VoiceMode VoiceMode
    {
        get => voiceMode.Value;
        set
        {
            if (!IsAuthority && World?.LocalUser != this)
                throw new InvalidOperationException("Only the user or the authority can change a voice mode");
            voiceMode.Value = value;
        }
    }

    public Audio.VoiceMode? LocalVoiceModeOverride { get; set; }

    public Audio.VoiceMode ActiveVoiceMode
    {
        get
        {
            if (LocalVoiceModeOverride == Audio.VoiceMode.Mute || IsMuted.Value)
                return Audio.VoiceMode.Mute;

            var mode = voiceMode.Value;
            if (mode == Audio.VoiceMode.Mute || mode == Audio.VoiceMode.Whisper)
                return mode;

            return LocalVoiceModeOverride ?? mode;
        }
    }

    private Audio.VoiceMode _maxAllowedVoiceMode = Audio.VoiceMode.Broadcast;

    public Audio.VoiceMode MaxAllowedVoiceMode => _maxAllowedVoiceMode;

    internal void SetMaxAllowedVoiceMode(Audio.VoiceMode mode) => _maxAllowedVoiceMode = mode;

    // Metres from this user's head to the local user's ear, or infinity when either end is unknown.
    // Written once a frame by WorldAudioManager off its own LastPrimaryEarPosition rather than computed
    // per reader: half a dozen things want this number and none of them should be walking a user root
    // for it. -xlinka
    public float DistanceToLocalUserHead { get; private set; } = float.PositiveInfinity;

    internal void SetDistanceToLocalUserHead(float distance) => DistanceToLocalUserHead = distance;

    // The key everything local-and-per-user hangs off. An account id follows a person between machines,
    // so it wins; a session UserID is next; a machine id is the last resort a guest always has.
    private string StableAudioKey
    {
        get
        {
            var account = AccountId?.Value;
            if (!string.IsNullOrEmpty(account))
                return account;
            var id = UserID?.Value;
            if (!string.IsNullOrEmpty(id))
                return id;
            return MachineID?.Value ?? string.Empty;
        }
    }

    private string MachineAudioKey => MachineID?.Value ?? string.Empty;

    // Up to double: a quiet talker can be brought up past unity, and the master limiter keeps that from
    // clipping.
    public const float MaxLocalVolume = 2f;

    // How loud this person is FOR ME. Host silencing is absolute and short-circuits it; everything else
    // is a per-category gain on the audio system, written under both the stable key and the machine key
    // so a user who signs in mid-session keeps the level I already set for them.
    public float LocalVolume
    {
        get
        {
            if (IsSilenced.Value)
                return 0f;

            var system = Engine.Current?.AudioSystem;
            if (system == null)
                return 1f;

            if (system.TryGetCategoryVolume(StableAudioKey, out float volume))
                return volume;
            if (system.TryGetCategoryVolume(MachineAudioKey, out volume))
                return volume;
            return 1f;
        }
        set
        {
            var system = Engine.Current?.AudioSystem;
            if (system == null)
                return;
            value = float.IsNaN(value) ? 0f : System.Math.Clamp(value, 0f, MaxLocalVolume);
            system.SetCategoryVolume(StableAudioKey, value);
            system.SetCategoryVolume(MachineAudioKey, value);
        }
    }

    // Someone this machine's account blocked: unseen and unheard here, and only here. Off the social
    // service's snapshot, so it holds whichever world they turn up in and lifts the moment they are unblocked.
    public bool IsBlockedByMe
    {
        get
        {
            var account = AccountId?.Value;
            return !string.IsNullOrEmpty(account) && !IsLocal && Engine.Current?.Social?.IsBlocked(account) == true;
        }
    }

    // On this machine's account's friends list, off the same snapshot.
    public bool IsFriendOfMine
    {
        get
        {
            var account = AccountId?.Value;
            return !string.IsNullOrEmpty(account) && !IsLocal && Engine.Current?.Social?.IsFriend(account) == true;
        }
    }

    public bool IsAudioLocallyBlocked
    {
        get
        {
            var system = Engine.Current?.AudioSystem;
            if (system == null)
                return false;
            return system.IsAudioBlocked(StableAudioKey) || system.IsAudioBlocked(MachineAudioKey);
        }
    }

    public void SetAudioLocallyBlocked(bool blocked)
    {
        var system = Engine.Current?.AudioSystem;
        if (system == null)
            return;
        system.SetAudioBlocked(StableAudioKey, blocked);
        system.SetAudioBlocked(MachineAudioKey, blocked);
    }

    #endregion

    private Components.UserRoot _root = null!;
    public readonly SyncRef<Components.UserRoot> UserRootRef = new();

    // INVITES THROUGH THE SESSION
    //
    // InviteAsks is this user's own machine asking the host to let accounts in: numbered lines, the last few,
    // shaped like ChatLog. InviteBook is only ever filled on the host's own user: each account the host let in
    // by invite and until when, and the asks it turned down, so an asker can see how its ask went. The door
    // reads the book on the host and nothing else; a joiner saying it was invited counts for nothing. Both sit
    // after every other member so the rest keep their places on the wire. -xlinka
    public readonly Sync<string> InviteAsks = new();
    public readonly Sync<string> InviteBook = new();

    // What kind of account this user proved: a person ("User"), or a bot or server login, as the accounts
    // service has it on the record. Host written with the rest of the card, empty on a guest. Last so every
    // member before it keeps its place on the wire. -xlinka
    public readonly Sync<string> AccountKind = new();

    // EDITING SOMEBODY ELSE
    //
    // EditAsks is this user's own machine asking people in the session to let it edit them: numbered lines, the last
    // few, each naming whom it asks. EditGrants is this user's own answers: whom they let change their body, their
    // avatar and what they hold for this session, and whom they turned down until when. Only this user's machine
    // writes either, and every peer's gate reads EditGrants, so what an asker claims counts for nothing. Last so every
    // member before them keeps its place on the wire. -xlinka
    public readonly Sync<string> EditAsks = new();
    public readonly Sync<string> EditGrants = new();

    public bool IsBot => Nexus.Cloud.Cdn.AccountKinds.IsBot(AccountKind.Value);

    // On the host and for yourself it is the root registered here; for everyone else on a guest it is the one
    // the reference names, as long as that root says it is theirs. Without the fallback a guest saw no other
    // user's body at all: no head to measure voices from, no one to face, a mirror of just yourself. -xlinka
    public Components.UserRoot Root
    {
        get => _root ?? (UserRootRef.Target is { IsDestroyed: false } named && ReferenceEquals(named.TargetUser.Target, this)
            ? named
            : null!);
        set
        {
            if (World?.IsAuthority == true || World?.LocalUser == this)
            {
                _root = value;
                UserRootRef.Target = value;
                if (value != null)
                {
                    var scope = World?.LocalUser == this ? "local" : "authority";
                    LumoraLogger.Debug($"User: Registered UserRoot for {scope} user '{UserName.Value}'");
                }
            }
            else
            {
                throw new Exception("Only the user or authority can register their UserRoot");
            }
        }
    }

    public User()
    {
    }

    internal void InitializeFromCollection(World world, RefID refID)
    {
        if (world == null)
            throw new ArgumentNullException(nameof(world));

        var refController = world.ReferenceController;
        if (refController == null)
            throw new InvalidOperationException("ReferenceController required to initialize User.");

        IsInInitPhase = true;
        refController.AllocationBlockBegin(refID);
        try
        {
            base.Initialize(world, parent: null);

            // Identity and namespace fields are the host's to author, full stop. Without this a guest
            // can overwrite its own UserID/MachineID (which is how bans are keyed, so that's a ban-dodge)
            // or rewrite its AllocationID to claim another user's RefID namespace and then author their
            // objects. MarkHostOnly makes Validate reject any guest write to these on the authority; the
            // host still authors them locally just fine. -xlinka
            UserName.MarkHostOnly();
            UserID.MarkHostOnly();
            MachineID.MarkHostOnly();
            AccountId.MarkHostOnly();
            AllocationID.MarkHostOnly();
            ItemAllocationID.MarkHostOnly();
            AllocationIDStart.MarkHostOnly();
            AllocationIDEnd.MarkHostOnly();
            IsSilenced.MarkHostOnly(); // host/platform-imposed silence, a guest can't lift it off themselves. -xlinka
            NameColor.MarkHostOnly();
            IsSupporter.MarkHostOnly();
            SupporterTier.MarkHostOnly();
            SupporterColor.MarkHostOnly();
            SupporterStyle.MarkHostOnly();
            IsStaff.MarkHostOnly();
            AccountKind.MarkHostOnly();
            GroupId.MarkHostOnly();
            GroupTag.MarkHostOnly();
            GroupName.MarkHostOnly();
            GroupIconHash.MarkHostOnly();
            GroupBannerHash.MarkHostOnly();
            GroupRole.MarkHostOnly();
            GroupColor.MarkHostOnly();
            PacketLoss.MarkHostOnly();
            QueuedMessages.MarkHostOnly();
            DeltaMessages.MarkHostOnly();
            StreamMessages.MarkHostOnly();
            ControlMessages.MarkHostOnly();
            IsLagging.MarkHostOnly();
            // what a machine says about itself: nobody else's peer may say it for them
            IsLive.MarkOwnUserOnly();
            Capture.MarkOwnUserOnly();
            ChatTyping.MarkOwnUserOnly();
            ChatLog.MarkOwnUserOnly();
            InviteAsks.MarkOwnUserOnly();
            InviteBook.MarkOwnUserOnly();
            EditAsks.MarkOwnUserOnly();
            EditGrants.MarkOwnUserOnly();
            // What a user reports about themselves, or asks for themselves. The host still writes them where it
            // must (a moderation mute, clamping a voice cap, clearing a respawn ask): host writes are local and
            // never judged by these rules. A guest writing them on someone else's record was allowed under a
            // Builder role, which let anyone mute, unmute or respawn anyone. -xlinka
            IsMuted.MarkOwnUserOnly();
            voiceMode.MarkOwnUserOnly();
            RespawnRequest.MarkOwnUserOnly();
            Ping.MarkOwnUserOnly();
            IsPresent.MarkOwnUserOnly();
            PresentInWorld.MarkOwnUserOnly();
            PresentInHeadset.MarkOwnUserOnly();
            VRActive.MarkOwnUserOnly();
            FPS.MarkOwnUserOnly();
            HeadDevice.MarkOwnUserOnly();
            UserPlatform.MarkOwnUserOnly();
            DownloadSpeed.MarkOwnUserOnly();
            UploadSpeed.MarkOwnUserOnly();
            DownloadedBytes.MarkOwnUserOnly();
            UploadedBytes.MarkOwnUserOnly();
            DownloadSpeedMax.MarkOwnUserOnly();
            UploadSpeedMax.MarkOwnUserOnly();
            EngineVersion.MarkOwnUserOnly();
            RuntimeVersion.MarkOwnUserOnly();
            DashOpen.MarkOwnUserOnly();
            ViewMode.MarkOwnUserOnly();
            PrimaryHand.MarkOwnUserOnly();
            streamConfiguration.MarkOwnUserOnly();
            // a guest may name its own body and nobody else's: the host binds whatever root this names
            UserRootRef.CheckRemoteSets((sender, _, root) => root != null && ReferenceEquals(sender, this)
                && ReferenceEquals(root.TargetUser.Target, this));

            EndInitializationStageForMembers();

            // The host fills in a new user's name, ids and byte before the user has a world, so none of
            // those writes marked anything to send. Peers already in the session learn the user from its
            // key alone and kept blank fields for them all session; only a later joiner saw the values,
            // in its full state. -xlinka
            if (world.IsAuthority)
                SendPresetFields();

            _syncMembers = new List<ISyncMember>(SyncMemberCount);
            for (int i = 0; i < SyncMemberCount; i++)
            {
                _syncMembers.Add(GetSyncMember(i));
            }

            StreamGroupManager = new StreamGroupManager(this);
            userStreams.Initialize(this);
            userStreams.OnElementAdded += OnStreamAdded;
            userStreams.OnElementRemoved += OnStreamRemoved;

            UserRootRef.OnTargetChange += OnUserRootRefChanged;
            RespawnRequest.OnChanged += _ => OnRespawnRequested();
            InviteAsks.OnChanged += _ => OnInviteAsked();
            EditAsks.OnChanged += _ => Networking.Session.SessionEditConsent.AsksChanged(this);
            EditGrants.OnChanged += _ => Networking.Session.SessionEditConsent.GrantsChanged(this);

            // Only create tracking streams on authority - clients receive them via sync
            if (world.IsAuthority)
            {
                EnsureTrackingStreamsInitialized();
            }
            EndInitPhase();

            LumoraLogger.Debug($"User created with {_syncMembers.Count} sync members");
        }
        finally
        {
            refController.AllocationBlockEnd();
        }
    }

    private void SendPresetFields()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            if (GetSyncMember(i) is ConflictingSyncElement field and not ISyncRef)
                field.InvalidateSyncElement();
        }
    }

    internal void Initialize()
    {
        // Only clients need to detect their local user this way
        // Authority sets local user directly via CreateHostUser
        if (!World.IsAuthority && World.LocalUser == null)
        {
            var session = World.Session;
            var syncManager = session?.Sync;
            // Guard against a null/default target matching a user whose RefID also defaulted to 0, so a
            // malformed JoinGrant can't trick a client into adopting the wrong user as local. -xlinka
            if (syncManager != null && !syncManager.LocalUserRefIDToInit.IsNull && ReferenceID == syncManager.LocalUserRefIDToInit)
            {
                World.SetLocalUser(this);
                LumoraLogger.Debug($"User.Initialize: Set local user '{UserName.Value}' (RefID: {ReferenceID})");
            }
        }
    }

    // Authority or local user only.
    public void SetUserName(string name)
    {
        if (IsAuthority || World.LocalUser == this)
        {
            UserName.Value = name;
        }
        else
        {
            LumoraLogger.Warn("Non-authority user cannot change username");
        }
    }

    // Host-authoritative; cannot kick the host.
    public void Kick()
    {
        if (World == null || !World.IsAuthority)
        {
            LumoraLogger.Warn("Kick requires host authority");
            return;
        }
        if (IsHost)
        {
            LumoraLogger.Warn("Cannot kick the host");
            return;
        }
        LumoraLogger.Log($"Kicking user '{UserName.Value}'");
        World.RemoveUser(this);
    }

    // Host-authoritative; cannot ban the host.
    public void Ban()
    {
        if (World == null || !World.IsAuthority)
        {
            LumoraLogger.Warn("Ban requires host authority");
            return;
        }
        if (IsHost)
        {
            LumoraLogger.Warn("Cannot ban the host");
            return;
        }
        LumoraLogger.Log($"Banning user '{UserName.Value}'");
        // held by the proven account (empty for a guest) and the machine; UserID is only this session's slot
        Security.BanManager.TempBan(AccountId.Value, MachineID.Value);
        Security.BanManager.AddBan(UserName.Value, AccountId.Value, MachineID.Value, World.WorldName.Value);
        World.RemoveUser(this);
    }

    public List<ISyncMember> GetDirtySyncMembers()
    {
        var dirty = new List<ISyncMember>();
        foreach (var member in _syncMembers)
        {
            if (member.IsDirty)
            {
                dirty.Add(member);
            }
        }
        return dirty;
    }

    public void ClearDirtyFlags()
    {
        foreach (var member in _syncMembers)
        {
            member.IsDirty = false;
        }
    }

    #region Stream Management

    public IStream GetStream(RefID id)
    {
        return (userStreams.TryGetValue(id, out var stream) ? stream : null) ?? null!;
    }

    public S AddStream<S>() where S : Networking.Streams.Stream, new()
    {
        var stream = new S();
        RefID key = World.ReferenceController.PeekID();
        userStreams.Add(key, stream, isNewlyCreated: true);
        return stream;
    }

    // Find the first stream of type S matching the predicate, or null. -xlinka
    public S? GetStream<S>(Func<S, bool> predicate) where S : Networking.Streams.Stream
    {
        foreach (var stream in Streams)
        {
            if (stream is S typed && predicate(typed))
                return typed;
        }
        return null;
    }

    // Find this user's stream of type S with the given name, or add and name one. The per-user
    // named-stream pattern (e.g. a "Voice" stream); the name replicates so peers agree. -xlinka
    public S GetStreamOrAdd<S>(string name, Action<S>? init = null) where S : Networking.Streams.Stream, new()
    {
        var existing = GetStream<S>(s => s.Name == name);
        if (existing != null)
            return existing;

        var stream = AddStream<S>();
        stream.Name = name;
        init?.Invoke(stream);
        return stream;
    }

    public void RemoveStream(Networking.Streams.Stream stream)
    {
        userStreams.Remove(stream.ReferenceID);
    }

    public void StreamConfigurationChanged()
    {
        if (!streamConfiguration.IsSyncDirty)
        {
            streamConfiguration.Value++;
        }
    }

    public bool WasStreamJustAdded(Networking.Streams.Stream stream)
    {
        return _justAddedStreams.Contains(stream);
    }

    public void UpdateStreams()
    {
        userStreams.Update();
    }

    private void OnStreamAdded(ReplicatedDictionary<RefID, Stream> collection, RefID key, Stream stream, bool isNew)
    {
        using (World.ReferenceController.AllocationScopeAt(key))
        {
            stream.Initialize(this);
        }

        if (IsLocal)
        {
            stream.Group = "Default";
            stream.Active = true;
        }
        else
        {
            _justAddedStreams.Add(stream);
        }
    }

    private void OnStreamRemoved(ReplicatedDictionary<RefID, Stream> collection, RefID key, Stream stream)
    {
        _justAddedStreams.Remove(stream);
        stream.Dispose();
    }

    internal void ClearJustAddedStreams()
    {
        _justAddedStreams.Clear();
    }

    public void GetTrackingStreams(BodyNode node, out Float3ValueStream position, out FloatQValueStream rotation)
    {
        EnsureTrackingStreamsInitialized();

        if (!_trackingStreams.TryGetValue(node, out var pair))
        {
            pair = CreateTrackingStreamPair();
            _trackingStreams[node] = pair;
        }

        position = pair.Position;
        rotation = pair.Rotation;
    }

    internal void ConfigureLocalTrackingStreams()
    {
        foreach (var stream in Streams)
        {
            stream.Active = true;

            if (stream is ImplicitStream implicitStream)
            {
                implicitStream.SetUpdatePeriod(1, 0);
            }

            StreamGroupManager.AssignToGroup(stream, null);
        }

        if (_trackingStreamsInitialized)
        {
            foreach (var pair in _trackingStreams.Values)
            {
                pair.Position.Active = true;
                pair.Rotation.Active = true;

                pair.Position.SetUpdatePeriod(1, 0);
                pair.Rotation.SetUpdatePeriod(1, 0);

                StreamGroupManager.AssignToGroup(pair.Position, null);
                StreamGroupManager.AssignToGroup(pair.Rotation, null);
            }
        }

        LumoraLogger.Debug($"ConfigureLocalTrackingStreams: Configured {StreamCount} streams for local user");
    }

    private void EnsureTrackingStreamsInitialized()
    {
        if (_trackingStreamsInitialized)
            return;

        _trackingStreamsInitialized = true;

        foreach (var node in TrackingNodes)
        {
            if (_trackingStreams.ContainsKey(node))
                continue;

            _trackingStreams[node] = CreateTrackingStreamPair();
        }
    }

    private TrackingStreamPair CreateTrackingStreamPair()
    {
        var position = CreateTrackingStream<Float3ValueStream>();
        var rotation = CreateTrackingStream<FloatQValueStream>();
        return new TrackingStreamPair(position, rotation);
    }

    private T CreateTrackingStream<T>() where T : Stream, new()
    {
        var stream = new T();
        RefID key = World.ReferenceController.PeekID();
        userStreams.Add(key, stream, isNewlyCreated: true);

        stream.InitializeDefaults(active: true, groupName: TrackingGroupName);

        if (IsLocal)
        {
            if (stream is ImplicitStream implicitStream)
            {
                implicitStream.SetUpdatePeriod(1, 0);
            }
        }

        return stream;
    }

    #endregion

    public override void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        UserRootRef.OnTargetChange -= OnUserRootRefChanged;
        userStreams.OnElementAdded -= OnStreamAdded;
        userStreams.OnElementRemoved -= OnStreamRemoved;

        // A user going away takes its streams along. On the authority that is the engine closing the record
        // of someone who left, not an edit, and a Social or Event world's freeze refused it half way. -xlinka
        using (World is { IsAuthority: true } world ? world.DataModelPermissions?.EnterSystemBypass() : null)
        {
            userStreams.Clear();
            StreamGroupManager?.Clear();
        }
        _trackingStreams.Clear();
        _justAddedStreams.Clear();

        base.Dispose();
    }

    private void OnRespawnRequested()
    {
        var world = World;
        if (!RespawnRequest.Value || world == null || !world.IsAuthority || IsDestroyed)
            return;
        world.RunSynchronously(() =>
        {
            if (IsDestroyed || !RespawnRequest.Value)
                return;
            world.RespawnUser(this);
            using (world.DataModelPermissions?.EnterSystemBypass())
                RespawnRequest.Value = false;
        });
    }

    // The host answers asks in its own update, never inside the sync write that brought them.
    private void OnInviteAsked()
    {
        var world = World;
        if (world == null || !world.IsAuthority || IsDestroyed || world.LocalUser == this)
            return;
        world.RunSynchronously(() =>
        {
            if (!IsDestroyed)
                Networking.Session.SessionInvites.TakeAsks(world, this);
        });
    }

    private void OnUserRootRefChanged(SyncRef<Components.UserRoot> syncRef)
    {
        var userRoot = syncRef.Target;
        if (userRoot == null || userRoot.IsDestroyed)
        {
            return;
        }

        if (userRoot.ActiveUser == this)
        {
            return;
        }

        // Only the authority should initialize and bind UserRoot.
        // Clients should not modify synced references during callbacks.
        if (World?.IsAuthority == true)
        {
            // Someone else's body, still in use, is never handed over: that would give this user the run of it.
            if (userRoot.ActiveUser is { IsDestroyed: false } other && other != this)
            {
                LumoraLogger.Warn(
                    $"User: UserRootRef of '{UserName.Value ?? "(null)"}' names the root of '{other.UserName.Value ?? "(null)"}', refused");
                return;
            }

            userRoot.Initialize(this);
        }
    }

    public override string ToString()
    {
        return $"User({UserName.Value ?? UserID.Value}, ID={ReferenceID})";
    }
}

