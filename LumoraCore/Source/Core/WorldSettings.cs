// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Cloud;

namespace Lumora.Core;

// Because it rides the normal component replication, settings replicate to clients, persist with the world
// (in the slot tree), and update live - reached via Configuration. The authority owns it; a client receives
// it through state sync. Edit/access policy (e.g. Mode) is still enforced host-authoritatively by the
// permission gate, not by trusting these synced values.
//
// NOT BROWSABLE and single-instance: the browser must never be able to bolt a second one onto anything,
// and every member carries the config lock, so this component is authored by the Session dashboard and
// by the engine's own create/load paths, by nobody else and from nowhere else. -xlinka
[ComponentCategory(ComponentLibrary.HiddenCategory)]
[SingleInstancePerSlot]
public sealed class WorldSettings : Component
{
    [Group("Access")]
    public readonly Sync<int> MaxUsers = new();
    public readonly Sync<bool> AllowJoin = new();
    public readonly Sync<bool> IsPublic = new();
    public readonly Sync<World.WorldAccessLevel> AccessLevel = new();

    // The group this session is hosted for, or empty. The host writes it; a guest only reads it, which is
    // why it is host-only - a guest that could set it would hand itself the moderator marker on the
    // nametag of anyone in the same group. Nothing in this package SETS it: hosting a world for a group is
    // its own piece of work, and this is the field it will write. What already reads it is the nametag,
    // which only shows a group's moderators as moderators inside that group's own world. -xlinka
    public readonly Sync<string> HostGroupId = new();

    // Baked at host; drives the permission preset.
    [Group("Mode")]
    public readonly Sync<WorldMode> Mode = new();

    // Attached by the engine on a first read rather than brought by a world, so it still holds the defaults
    // and the mode a host picks replaces them. -xlinka
    internal bool SeededByEngine;

    public readonly Sync<bool> MobileFriendly = new();
    public readonly Sync<bool> EditMode = new();
    [Group("Session")]
    public readonly Sync<bool> HideFromSessionLists = new();
    public readonly Sync<bool> AutoKickAFK = new();
    public readonly Sync<int> MaxAFKMinutes = new();
    [Group("Assets")]
    public readonly Sync<bool> CleanupUnusedAssets = new();
    public readonly Sync<float> AssetCleanupInterval = new();
    [Group("Info")]
    // The name the host gave the session. The host seeds it at start and renames through it; every peer's
    // world takes it from here, so guests see a rename. Empty keeps the name the world was opened under.
    public readonly Sync<string> SessionName = new();
    // The session's directory id, seeded by the host at start, so a guest that came in on an address without
    // one still knows which session it is in and its friends can be sent there.
    public readonly Sync<string> SessionId = new();
    public readonly Sync<string> Description = new();
    [Group("Persistence")]
    public readonly Sync<bool> EnablePersistence = new();
    public readonly Sync<float> AutoSaveInterval = new();
    public readonly Sync<int> MaxWorldSizeMB = new();

    [Group("Discovery")]
    public readonly SyncFieldList<string> Tags = new();

    // Sync send/process rate in Hz for THIS session. Host-only: a guest turning it down would not make
    // the host send less, it would only starve that guest's own loop, so the host owns it and everyone
    // runs at the same rate. MarkHostOnly makes the authority refuse a guest's delta for it outright,
    // which is the actual enforcement - the Session screen only hides the control. -xlinka
    [Group("Network")]
    public readonly Sync<int> NetworkTickRate = new();

    // The saved values come off disk through the shared Worker.Load, which writes the members directly -
    // so the load runs inside the edit scope rather than being refused by the lock it installs. -xlinka
    public override void Load(Persistence.DataTreeNode node, Persistence.LoadControl control)
    {
        using var configEdit = WorldConfigEdit.Enter();
        base.Load(node, control);
    }

    public const int DefaultTickRate = 70;
    public const int MinTickRate = 10;
    public const int MaxTickRate = 120;

    public override void OnInit()
    {
        base.OnInit();
        using var configEdit = WorldConfigEdit.Enter();
        MaxUsers.Value = 32;
        AllowJoin.Value = true;
        IsPublic.Value = false;
        AccessLevel.Value = World.WorldAccessLevel.Private;
        HostGroupId.Value = string.Empty;
        Mode.Value = WorldMode.Builder;
        MobileFriendly.Value = false;
        EditMode.Value = false;
        HideFromSessionLists.Value = false;
        AutoKickAFK.Value = false;
        MaxAFKMinutes.Value = 30;
        CleanupUnusedAssets.Value = true;
        AssetCleanupInterval.Value = 300f;
        SessionName.Value = string.Empty;
        SessionId.Value = string.Empty;
        Description.Value = string.Empty;
        EnablePersistence.Value = false;
        AutoSaveInterval.Value = 0f;
        MaxWorldSizeMB.Value = 512;
        NetworkTickRate.Value = DefaultTickRate;
    }

    public override void OnAwake()
    {
        base.OnAwake();

        // The blanket, same shape as WorldPermissionConfig's. It used to be two named fields, which meant
        // every OTHER member here - the access level, the user cap, persistence, the AFK kick - took a
        // guest's delta and took an inspector write from anyone standing in the world. Running the sweep
        // over the whole component is what makes a field added here next year locked by construction
        // instead of by somebody remembering. -xlinka
        for (int i = 0; i < SyncMemberCount; i++)
        {
            if (GetSyncMember(i) is ConflictingSyncElement element)
            {
                element.MarkHostOnly();
                element.MarkNonDrivable();
                element.MarkConfigLocked();
            }
        }

        // The home stays Private whoever writes the level: the Session screen, a script or an engine path. -xlinka
        AccessLevel.LocalFilter = (level, _) => KeepHomePrivate(level);
    }

    private World.WorldAccessLevel KeepHomePrivate(World.WorldAccessLevel level)
    {
        if (level == World.WorldAccessLevel.Private || !Management.WorldManager.IsLocalHome(World))
            return level;
        Logging.Logger.Warn($"WorldSettings: the local world stays private, access {level} refused");
        return World.WorldAccessLevel.Private;
    }

    public override void OnStart()
    {
        base.OnStart();

        // A config component that is not on the world root came in on a copied item, a pasted graph or a
        // crafted save. It is not a second opinion about the world's settings, it is a lie told from
        // inside an object, so it goes. -xlinka
        if (World != null && World.IsAuthority && WorldConfigLock.IsMisplaced(this))
        {
            Logging.Logger.Warn(
                $"WorldSettings on '{Slot?.Name?.Value ?? "?"}' is not the world root - removing it.");
            using (WorldConfigEdit.Enter())
                Destroy();
            return;
        }

        using var configEdit = WorldConfigEdit.Enter();

        // A world saved before the tick rate moved here loads with a zero. Normalize it on the
        // authority so the Session screen shows a real number instead of an out-of-range 0.
        if (World != null && World.IsAuthority && NetworkTickRate.Value <= 0)
            NetworkTickRate.Value = DefaultTickRate;

        // A load writes members past the filter, so a home saved on another level comes back Private here. -xlinka
        if (World != null && World.IsAuthority && AccessLevel.Value != World.WorldAccessLevel.Private
            && Management.WorldManager.IsLocalHome(World))
            AccessLevel.Value = World.WorldAccessLevel.Private;

        // Push the session rate into the sync manager on the MAIN thread. The sync loop reads its
        // cached copy off-thread every idle wait; reaching into the data model from there to resolve
        // a component would race the main thread's attaches. -xlinka
        PublishTickRate();
        NetworkTickRate.OnChanged += _ => PublishTickRate();
        // Keep the permission preset in lockstep with the mode, ON EVERY PEER. The host is still the only
        // gate that decides anything - it refuses a guest's delta and corrects it - but a guest whose local
        // gate never ran the preset keeps the constructor's Builder defaults, so its own client cheerfully
        // authorises edits the host is about to throw away. That is the first wall a guest hits in a social
        // world and it is completely invisible from the client side. The mode value is host-authored and
        // replicated, so applying it locally only ever makes a guest agree with the host sooner. -xlinka
        if (World != null)
        {
            WorldModePermissions.Apply(World, Mode.Value);
            StampModeTag();
        }
        Mode.OnChanged += _ =>
        {
            if (World != null)
            {
                WorldModePermissions.Apply(World, Mode.Value);
                StampModeTag();
            }
        };

        // Access level is host-authoritative LIVE state: when the host changes it at runtime, re-advertise
        // the session and start/stop the LAN beacon to match. (The beacon was otherwise only chosen once at
        // create time, which is why you used to have to "open as LAN" instead of switching to it.) Gated to
        // the host AND to a Running world so the create-time seed in StartSession doesn't fire this before the
        // session even exists (the initial beacon comes from the session metadata as before). A client just
        // receives the synced value and never touches the beacon. -xlinka
        // IsPublic follows too: it is seeded once at creation and Discord presence reads it.
        AccessLevel.OnChanged += _ =>
        {
            if (World == null || !World.IsAuthority || World.State != World.WorldState.Running)
                return;
            var visibility = ToVisibility(AccessLevel.Value, HostGroupId.Value.Length > 0);
            using (WorldConfigEdit.Enter())
                IsPublic.Value = visibility == SessionVisibility.Public;
            World.Session?.SetVisibility(visibility);
        };

        ApplySessionName();
        SessionName.OnChanged += _ => ApplySessionName();
        ApplySessionId();
        SessionId.OnChanged += _ => ApplySessionId();
    }

    // The listing's mode tag follows the mode itself. Hosting stamps it before a saved world's own settings are in,
    // so a Social or Event save opened again was listed as a Builder world. -xlinka
    private void StampModeTag()
    {
        var world = World;
        var session = world?.Session;
        if (world is not { IsAuthority: true } || session?.Metadata?.Tags is not { } tags
            || tags.Contains(WorldModePermissions.ModeTag(Mode.Value)))
            return;
        session.UpdateMetadata(metadata => WorldModePermissions.StampModeTag(metadata.Tags, Mode.Value));
    }

    // A guest's world was opened as "Unknown"; it takes the host's id once that arrives.
    private void ApplySessionId()
    {
        var world = World;
        string id = SessionId.Value ?? string.Empty;
        if (world != null && !world.IsAuthority && SessionIdentifier.IsValid(id) && world.SessionID.Value != id)
            world.SessionID.Value = id;
    }

    // On the host the listing follows too: the LAN beacon at once, the directory on its next heartbeat. Bans
    // are kept per world name, so a rename carries this world's bans over to the new one. -xlinka
    private void ApplySessionName()
    {
        var world = World;
        string name = SessionName.Value?.Trim() ?? string.Empty;
        if (world == null || name.Length == 0)
            return;
        string old = world.WorldName.Value ?? string.Empty;
        if (!string.Equals(old, name, System.StringComparison.Ordinal))
        {
            if (world.IsAuthority)
                Security.BanManager.CopyWorldBans(old, name);
            world.WorldName.Value = name;
        }
        var session = world.Session;
        if (world.IsAuthority && session?.Metadata != null
            && !string.Equals(session.Metadata.Name, name, System.StringComparison.Ordinal))
            session.UpdateMetadata(metadata => metadata.Name = name);
    }

    private void PublishTickRate()
    {
        int rate = NetworkTickRate.Value;
        if (rate <= 0)
            rate = DefaultTickRate;
        World?.Session?.Sync?.SetSyncRate(System.Math.Clamp(rate, MinTickRate, MaxTickRate));
    }

    // Map the user-facing access level to the network session visibility that drives the LAN beacon / public
    // registration. Contacts tiers advertise to contacts, the open tiers advertise publicly.
    //
    // A world actually hosted FOR a group advertises publicly whichever group tier it is on. Members have
    // to be able to FIND the thing before the door can refuse everybody else, and a world nobody can find
    // is not a group world. The flag matters: a world set to a group tier with no HostGroupId has no door
    // behind it, so it keeps the old silent answer rather than being quietly published to the directory.
    // -xlinka
    internal static SessionVisibility ToVisibility(World.WorldAccessLevel level, bool hostedForGroup = false)
    {
        bool groupTier = level == World.WorldAccessLevel.GroupMembers
            || level == World.WorldAccessLevel.GroupPlus;
        if (groupTier && hostedForGroup)
            return SessionVisibility.Public;

        return level switch
        {
            World.WorldAccessLevel.Private => SessionVisibility.Private,
            World.WorldAccessLevel.LAN => SessionVisibility.LAN,
            World.WorldAccessLevel.Contacts or World.WorldAccessLevel.ContactsPlus
                or World.WorldAccessLevel.GroupMembers or World.WorldAccessLevel.GroupPlus
                => SessionVisibility.Contacts,
            _ => SessionVisibility.Public, // RegisteredUsers / Anyone / GroupPublic
        };
    }
}
