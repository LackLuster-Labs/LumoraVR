// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Warden;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

// The gate's view of RefID. RefID stays here; the gate is handed only the bit rules it
// needs, so it holds no engine type and the ownership RULE (authority ids are owned by nobody, a user
// owns what was minted in their allocation byte) stays on the gate's side of the wall. -xlinka
internal sealed class RefIDPermissionIdSpace : IPermissionIdSpace
{
    public static readonly RefIDPermissionIdSpace Instance = new();

    private RefIDPermissionIdSpace() { }

    public bool IsNull(ulong id) => new RefID(id).IsNull;

    public bool IsAuthority(ulong id) => new RefID(id).IsAuthorityID;

    public byte OwnerByte(ulong id) => new RefID(id).GetUserByte();

    public bool IsValidOwnerByte(byte ownerByte) => RefIDConstants.IsValidUserByte(ownerByte);

    public bool TryGetElementId(object? key, out ulong id)
    {
        if (key is RefID refId)
        {
            id = refId.RawValue;
            return true;
        }

        id = 0;
        return false;
    }
}

// Carries a violation escalation from the gate out to the session. The gate decides WHO goes and why;
// everything about how a peer is actually removed lives here, because LumoraWarden references no
// networking and never will. -xlinka
internal sealed class SessionPermissionEnforcement : IPermissionEnforcement
{
    private readonly World _world;

    public SessionPermissionEnforcement(World world) => _world = world;

    public void WarnHost(IPermissionActor actor, double score, string summary)
    {
        // The gate already logged it. This exists so a host-facing surface (round 2's session screen) has
        // somewhere to hang a live indicator without the gate knowing about UI.
    }

    public void Kick(IPermissionActor actor, string reason)
    {
        if (actor is not User user || _world == null || !_world.IsAuthority || user.IsHost)
            return;

        var connections = _world.Session?.Connections;
        if (connections != null)
            connections.DisconnectUser(user, reason);
        else
            _world.RemoveUser(user);
    }

    public void TempBan(IPermissionActor actor, string reason)
    {
        if (actor is not User user || _world == null || !_world.IsAuthority || user.IsHost)
            return;

        // TEMP, not durable. A denial score is evidence of a bad session, not of a person who should never
        // come back, and a wrong durable ban is a lot harder to undo than a wrong temp one. The host bans
        // properly through the session screen if it wants that. -xlinka
        Security.BanManager.TempBan(user.AccountId.Value, user.MachineID.Value);
        Kick(actor, reason);
    }
}

// The engine's handle on a world's permission gate. The decision itself is
// PermissionEngine over in LumoraWarden; this only forwards to it and FAILS CLOSED if
// the gate ever throws. There is no state here that could disagree with the gate, and no path that
// answers a request without asking it. -xlinka
public sealed class DataModelPermissionController
{
    // Building the gate is part of building the world: if it cannot be built the world constructor
    // throws and no world exists to write to, which is the fail-closed outcome. Kept private so the
    // only way into the policy from here is through the forwarding members below. -xlinka
    private readonly PermissionEngine _engine;

    // Reused across republishes. The gate takes a whole snapshot and rebuilds its own dictionaries from
    // it, so the same carrier can be refilled instead of allocating a fresh graph per config edit.
    private readonly PermissionPolicy _policy = new();

    private readonly World _world;

    public DataModelPermissionController(World world)
    {
        if (world == null)
        {
            throw new ArgumentNullException(nameof(world));
        }

        _world = world;

        _engine = new PermissionEngine(world, RefIDPermissionIdSpace.Instance)
        {
            Warn = LumoraLogger.Warn,
            Enforcement = new SessionPermissionEnforcement(world)
        };
    }

    public DataModelPermissionRole HostRole => _engine.HostRole;
    public DataModelPermissionRole AdminRole => _engine.AdminRole;
    public DataModelPermissionRole BuilderRole => _engine.BuilderRole;
    public DataModelPermissionRole ModeratorRole => _engine.ModeratorRole;
    public DataModelPermissionRole GuestRole => _engine.GuestRole;
    public DataModelPermissionRole SpectatorRole => _engine.SpectatorRole;
    public IReadOnlyList<DataModelPermissionRole> AssignableRoles => _engine.AssignableRoles;

    // Master switch for permission enforcement. HOST-AUTHORITATIVE and FAIL-CLOSED: only the authority
    // may turn enforcement off, and the refusal is decided inside the gate, not here.
    public bool Enabled
    {
        get => _engine.Enabled;
        set => _engine.Enabled = value;
    }

    public bool LogDeniedMutations
    {
        get => _engine.LogDeniedMutations;
        set => _engine.LogDeniedMutations = value;
    }

    // Social/Event lockdown. When set, the authored world is frozen for EVERYONE (including the host
    // and admins): only a user's OWN runtime objects (avatar, spawned items) may be mutated. World
    // content is created under the authority RefID, so no user "owns" it and all editing of it is
    // denied - there's no role that escapes this and no live toggle. Set by WorldModePermissions.
    public bool SocialLock
    {
        get => _engine.SocialLock;
        set => _engine.SocialLock = value;
    }

    public DataModelPermissionRole GetDefaultRole(DataModelAccessClass accessClass) => _engine.GetDefaultRole(accessClass);

    public void SetDefaultRole(DataModelAccessClass accessClass, DataModelPermissionRole role)
    {
        _engine.SetDefaultRole(accessClass, role);
        RightsChanged();
    }

    public int UserOverrideCount => _engine.UserOverrideCount;

    public void ClearUserOverrides()
    {
        _engine.ClearUserOverrides();
        RightsChanged();
    }

    public IDisposable EnterActor(User? actor) => _engine.EnterActor(actor);

    // For an actor that is not a live User: a script principal, which carries its author's identity and
    // byte but never the author's root or host status.
    public IDisposable EnterActor(IPermissionActor? actor) => _engine.EnterActor(actor);

    public Lumora.Warden.PermissionEngine.BypassScope EnterSystemBypass() => _engine.EnterSystemBypass();

    // For the world's OWN authored logic acting on world content while the world is frozen - an authored
    // button driving an authored light in a Social world. Pass the component doing the asking: the scope
    // arms only if THAT is authority-authored too, so a visitor's own control opens nothing. Permits a
    // mutation of an AUTHORITY-AUTHORED target on the AUTHORITY and nothing else; every other gate still
    // runs. Reach for this instead of EnterSystemBypass, which skips the lot.
    public IDisposable EnterAuthoredContentScope(IWorldElement? source) => _engine.EnterAuthoredContentScope(source);

    // The authority keeping a shared panel up (rebuilding its rows, refreshing what they show) while somebody
    // holds it or has it pinned. Pass the component doing its upkeep: only what sits on its slot or below gets
    // past the person boundary, and only on the authority. Everything else the gate asks still runs.
    public Lumora.Warden.PermissionEngine.UpkeepScope EnterUiUpkeep(IWorldElement? source) => _engine.EnterUpkeepScope(source);

    public void AddRule(IDataModelPermissionRule rule) => _engine.AddRule(rule);

    public bool RemoveRule(IDataModelPermissionRule rule) => _engine.RemoveRule(rule);

    public void ClearRules() => _engine.ClearRules();

    public void SetUserRole(User user, DataModelPermissionRole role)
    {
        _engine.SetUserRole(user, role);
        RightsChanged();
    }

    public void ClearUserRole(User user)
    {
        _engine.ClearUserRole(user);
        RightsChanged();
    }

    public DataModelPermissionRole GetRole(User? user) => _engine.GetRole(user);

    public DataModelAccessClass GetAccessClass(User? user) => _engine.GetAccessClass(user);

    public IReadOnlyList<DataModelPermissionRole> AssignableRolesFor(WorldMode mode) => _engine.AssignableRolesFor(mode);

    // GROUP ROSTER
    // Filled by GroupHostRoster on the host and by nothing else. One whole snapshot per push, so a failed
    // fetch leaves the last good roster standing simply by not calling this. The reads below are what the
    // door and the Permissions tab ask; on a guest they always answer no, because a guest never fetched a
    // roster and is not entitled to one. -xlinka
    public void SetGroupRoster(IReadOnlyDictionary<string, string>? members, IReadOnlyCollection<string>? bans)
        => _engine.SetGroupRoster(members, bans);

    public bool IsGroupMember(string? accountId) => _engine.IsGroupMember(accountId);

    public bool IsGroupBanned(string? accountId) => _engine.IsGroupBanned(accountId);

    public string GroupRoleOf(string? accountId) => _engine.GroupRoleOf(accountId);

    public bool IsGroupModerator(User? user)
        => user != null && PermissionEngine.IsGroupModeratorRank(_engine.GroupRoleOf(((IPermissionActor)user).AccountKey));

    public int GroupRosterCount => _engine.GroupRosterCount;

    // The Social/Event lock floor plus the default role per access class.
    public void ApplyMode(WorldMode mode)
    {
        _engine.ApplyMode(mode);
        RightsChanged();
    }

    // Whatever a role change takes away, it takes from grants made while it allowed them too.
    internal void RightsChanged()
    {
        _world.LinkManager?.ScheduleGrantRecheck();
        _world.ScriptsIfAny?.ScheduleRecheck();
    }

    public DataModelPermissionRole? FindRole(string? name) => _engine.FindRole(name);

    public DataModelAccessClass AccessClassOf(DataModelPermissionRole role) => _engine.AccessClassOf(role);

    public int AssignmentCount => _engine.AssignmentCount;

    // LIVE CONFIG
    // Marshals the host's config component into a policy snapshot and hands it to the gate, which rebuilds
    // its own tables from it. This is the ONLY direction config travels: the gate never reads the data
    // model, so a decision never touches a replicated component. Called on attach and on every change, and
    // nowhere near a frame loop. A null config is a full reset to the compiled defaults. -xlinka
    public void ApplyConfig(WorldPermissionConfig? config)
    {
        _policy.Assignments.Clear();
        _policy.Caps.Clear();
        _policy.DefaultJoinerRole = null;
        _policy.Escalation = new PermissionEscalationSettings();

        if (config == null || config.IsDestroyed)
        {
            PushDefaultRoles(null);
            _engine.ApplyPolicy(_policy);
            RightsChanged();
            return;
        }

        foreach (var entry in config.Assignments)
        {
            if (entry == null || entry.IsDestroyed || string.IsNullOrWhiteSpace(entry.Role.Value))
                continue;

            _policy.Assignments.Add(new PermissionAssignment
            {
                MachineKey = entry.MachineId.Value,
                AccountKey = entry.AccountId.Value,
                RoleName = entry.Role.Value
            });
        }

        foreach (var entry in config.RoleCaps)
        {
            if (entry == null || entry.IsDestroyed || string.IsNullOrWhiteSpace(entry.Role.Value))
                continue;

            _policy.Caps.Add(new PermissionRoleCap
            {
                RoleName = entry.Role.Value,
                Spawn = entry.Spawn.Value,
                SaveCopy = entry.SaveCopy.Value,
                Export = entry.Export.Value,
                ToolUse = entry.ToolUse.Value,
                Touch = entry.Touch.Value,
                Script = entry.Script.Value,
                MinScale = entry.MinScale.Value,
                MaxScale = entry.MaxScale.Value
            });
        }

        _policy.DefaultJoinerRole = config.DefaultJoinerRole.Value;

        PushDefaultRoles(config);

        // A saved world written before these fields existed loads with zeros, and a zero half-life means
        // "forget everything instantly" while a zero kick score means "kick on the first denial". Neither
        // is what an unset value means, so anything not positive falls back to the shipped default. -xlinka
        var escalation = new PermissionEscalationSettings
        {
            Response = config.ViolationResponse.Value
        };
        if (config.DenialHalfLifeSeconds.Value > 0f)
            escalation.HalfLifeSeconds = config.DenialHalfLifeSeconds.Value;
        if (config.WarnScore.Value > 0f)
            escalation.WarnScore = config.WarnScore.Value;
        if (config.KickScore.Value > 0f)
            escalation.KickScore = config.KickScore.Value;
        if (config.TempBanScore.Value > 0f)
            escalation.TempBanScore = config.TempBanScore.Value;
        _policy.Escalation = escalation;

        _engine.ApplyPolicy(_policy);
        RightsChanged();
    }

    // Who gets which role on arrival, per access class, pushed at the gate as the host typed it.
    //
    // Runs AFTER ApplyMode at both call sites in World, which matters: ApplyMode seeds every class from
    // the mode and this lays the host's answers over the top. A row the host left empty (or that names a
    // role this mode does not offer, or names Host, which nobody is ever handed) goes back to the mode's
    // own seed rather than to whatever the previous config happened to leave behind. -xlinka
    private void PushDefaultRoles(WorldPermissionConfig? config)
    {
        var mode = _engine.Mode;
        PushDefaultRole(mode, DataModelAccessClass.Anonymous, config?.AnonymousRole.Value);
        PushDefaultRole(mode, DataModelAccessClass.Visitor, config?.VisitorRole.Value);
        PushDefaultRole(mode, DataModelAccessClass.Contact, config?.ContactRole.Value);
        PushDefaultRole(mode, DataModelAccessClass.Group, config?.GroupRole.Value);
    }

    private void PushDefaultRole(WorldMode mode, DataModelAccessClass accessClass, string? roleName)
    {
        var role = _engine.FindRole(roleName);
        // A role this mode does not offer is discarded rather than honoured. A world saved in Builder and
        // re-hosted as an Event would otherwise carry "everyone signed in is a Builder" into a space that
        // has no Builder in it, which is the one direction a stale config must never be able to push. It
        // also keeps the session screen honest: the lit pill is always one the host can see. -xlinka
        if (role != null && !IsAssignableIn(mode, role))
            role = null;
        if (role == null || ReferenceEquals(role, _engine.HostRole))
            role = _engine.SeedRoleFor(mode, accessClass);
        _engine.SetDefaultRole(accessClass, role);
    }

    private bool IsAssignableIn(WorldMode mode, DataModelPermissionRole role)
    {
        var roles = _engine.AssignableRolesFor(mode);
        for (int i = 0; i < roles.Count; i++)
        {
            if (ReferenceEquals(roles[i], role))
                return true;
        }
        return false;
    }

    // PER-DOMAIN QUERIES, for the tool / spawn / touch / copy call sites and for round 2's UI. The mode
    // ceiling is applied inside the gate, so a caller cannot forget it.
    public bool AllowsDomain(User? user, DataModelPermissionDomain domain) => _engine.AllowsDomain(user, domain);

    // The same question asked of a ROLE rather than a person, so a page can show what a role gets with
    // nobody standing in it. Same resolution order the per-user path takes: the compiled cap, then the
    // host's toggle, then the world mode ceiling last and binding.
    public bool AllowsDomainFor(DataModelPermissionRole role, DataModelPermissionDomain domain)
        => role != null && _engine.AllowsDomainFor(role, domain);

    // What the world mode alone would hand this access class, with no host override on top. The "By world
    // mode" pill on the session screen needs it to say what clearing a row actually lands on.
    public DataModelPermissionRole SeedRoleFor(WorldMode mode, DataModelAccessClass accessClass)
        => _engine.SeedRoleFor(mode, accessClass);

    public float ClampScale(User? user, float requested) => _engine.ClampScale(user, requested);

    public bool TryGetScaleBounds(User? user, out float min, out float max)
        => _engine.TryGetScaleBounds(user, out min, out max);

    // Records a refusal that never reached Authorize - a host-only member forged on the wire, a link claim
    // the arbitration ledger rejected. Those are the highest-signal refusals there are, so the ledger has
    // to see them or the escalation is blind to exactly the behaviour it exists for.
    public void ReportDenial(User? actor, DataModelDenialKind kind, string? reason = null)
    {
        if (actor == null)
            return;

        try
        {
            _engine.ReportDenial(actor, kind, reason);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never take down a validation path.
            LumoraLogger.Debug($"Permission denial ledger threw, ignoring: {ex.Message}");
        }
    }

    public double DenialScore(User? actor) => _engine.DenialScoreOf(actor);

    // Seconds, monotonic. Only the denial ledger reads it; settable so a test can drive decay without
    // sleeping through a real minute. Null restores the real clock.
    public Func<double>? Clock
    {
        get => _engine.Clock;
        set => _engine.Clock = value;
    }

    // Where a violation escalation lands. Wired to the session at construction; swappable so a test can
    // watch what the gate decided without a peer actually being kicked.
    public IPermissionEnforcement? Enforcement
    {
        get => _engine.Enforcement;
        set => _engine.Enforcement = value;
    }

    // Ask the gate. A gate that throws is a gate that did not decide, so it denies: an exception here
    // must never read as permission. -xlinka
    public bool Authorize(in DataModelPermissionRequest request, out string? reason)
    {
        try
        {
            return _engine.Authorize(in request, out reason);
        }
        catch (Exception ex)
        {
            reason = $"permission check failed: {ex.Message}";
            LumoraLogger.Error($"Datamodel permission check threw, denying: {ex}");
            return false;
        }
    }

    // Authorize asked quietly, as things stand or with no scope open. Fails closed the same way.
    public bool Allows(in DataModelPermissionRequest request, bool unscoped = false)
    {
        try
        {
            return unscoped ? _engine.AllowsUnscoped(in request) : _engine.Allows(in request);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Datamodel permission check threw, denying: {ex}");
            return false;
        }
    }

    // The person boundary for putting a slot under parent: a new child, or a reparent. sender is set for a move that
    // came over the wire and is asked on the host; null asks for whoever this thread acts as. Fails closed.
    public bool AllowsPlacingUnder(Slot? placed, Slot? parent, User? sender = null)
    {
        try
        {
            return _engine.AllowsPlacingUnder(sender, placed, parent, isNetwork: sender != null);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Datamodel placement check threw, denying: {ex}");
            return false;
        }
    }

    public void Assert(in DataModelPermissionRequest request)
    {
        if (!Authorize(request, out var reason))
        {
            throw new UnauthorizedAccessException(reason ?? "datamodel mutation denied");
        }
    }
}
