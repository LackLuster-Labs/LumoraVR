// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumora.Warden;

// The hard permission gate. One per world. It sits below the engine on purpose: the engine takes a
// dependency on this policy and cannot compile without it, so there is no path that "forgets" to
// consult the gate. Every decision here is host-authoritative - it reads the world's own authority
// flag and host-assigned allocation bytes, never a value a client sent. -xlinka
public sealed class PermissionEngine
{
    private sealed class Scope : IDisposable
    {
        private readonly IPermissionActor? _previousActor;
        private readonly int _previousBypassDepth;
        private bool _disposed;

        public Scope(IPermissionActor? actor, bool systemBypass)
        {
            _previousActor = s_currentActor;
            _previousBypassDepth = s_systemBypassDepth;
            s_currentActor = actor;
            if (systemBypass)
            {
                s_systemBypassDepth = _previousBypassDepth + 1;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            s_currentActor = _previousActor;
            s_systemBypassDepth = _previousBypassDepth;
            _disposed = true;
        }
    }

    private sealed class AuthoredContentScope : IDisposable
    {
        private readonly int _previousDepth;
        private readonly bool _armed;
        private bool _disposed;

        public AuthoredContentScope(bool armed)
        {
            _armed = armed;
            // An unarmed scope does not touch the counter: the inert singleton is shared by every thread.
            _previousDepth = armed ? s_authoredContentDepth : 0;
            if (armed)
                s_authoredContentDepth = _previousDepth + 1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (_armed)
                s_authoredContentDepth = _previousDepth;
            _disposed = true;
        }
    }

    private static readonly AuthoredContentScope s_inertScope = new(armed: false);

    // The compiled capability caps for one role, plus whatever the host's config resolved on top. Built
    // once per policy change and read by reference afterwards, so the per-domain question costs a
    // dictionary probe and a field read.
    private readonly struct DomainCaps
    {
        public readonly bool Spawn;
        public readonly bool SaveCopy;
        public readonly bool Export;
        public readonly bool ToolUse;
        public readonly bool Touch;
        public readonly bool Script;
        public readonly float MinScale;
        public readonly float MaxScale;

        public DomainCaps(bool spawn, bool saveCopy, bool export, bool toolUse, bool touch, bool script, float minScale = 0f, float maxScale = 0f)
        {
            Spawn = spawn;
            SaveCopy = saveCopy;
            Export = export;
            ToolUse = toolUse;
            Touch = touch;
            Script = script;
            MinScale = minScale;
            MaxScale = maxScale;
        }

        public bool Allows(PermissionDomain domain) => domain switch
        {
            PermissionDomain.Spawn => Spawn,
            PermissionDomain.SaveCopy => SaveCopy,
            PermissionDomain.Export => Export,
            PermissionDomain.ToolUse => ToolUse,
            PermissionDomain.Touch => Touch,
            PermissionDomain.Script => Script,
            _ => false
        };

        public DomainCaps With(PermissionRoleCap cap) => new(
            Resolve(Spawn, cap.Spawn),
            Resolve(SaveCopy, cap.SaveCopy),
            Resolve(Export, cap.Export),
            Resolve(ToolUse, cap.ToolUse),
            Resolve(Touch, cap.Touch),
            Resolve(Script, cap.Script),
            cap.MinScale > 0f ? cap.MinScale : MinScale,
            cap.MaxScale > 0f ? cap.MaxScale : MaxScale);

        private static bool Resolve(bool compiled, PermissionToggle toggle) => toggle switch
        {
            PermissionToggle.Grant => true,
            PermissionToggle.Deny => false,
            _ => compiled
        };
    }

    // Per thread, read on every gated write and set around every script node call: plain thread statics, a
    // field read where a ThreadLocal was a lookup. -xlinka
    [ThreadStatic] private static IPermissionActor? s_currentActor;
    [ThreadStatic] private static int s_systemBypassDepth;
    [ThreadStatic] private static int s_authoredContentDepth;
    [ThreadStatic] private static int s_quietDepth;
    [ThreadStatic] private static IPermissionTarget? s_upkeepSource;

    private readonly IPermissionWorldFacts _world;
    private readonly IPermissionIdSpace _ids;
    private readonly List<IPermissionRule> _rules = new();
    private readonly Dictionary<ulong, PermissionRole> _userRoles = new();

    // Distinct denials already logged, so a per-frame denial doesn't spam thousands of identical lines. -xlinka
    private readonly HashSet<string> _loggedDenials = new();

    // THE HOST'S CONFIG, RESOLVED
    // Nothing below is read from the data model at request time. ApplyPolicy takes a whole snapshot and
    // rebuilds these; between snapshots every lookup is a dictionary probe on state the engine owns. That
    // is what keeps a config that lives in a replicated component off the hot path entirely. -xlinka
    private readonly Dictionary<PermissionRole, DomainCaps> _compiledCaps = new();
    private readonly Dictionary<PermissionRole, DomainCaps> _resolvedCaps = new();
    private readonly Dictionary<string, PermissionRole> _rolesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PermissionRole> _assignedByMachine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PermissionRole> _assignedByAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PermissionToggle[]> _toggles = new(StringComparer.OrdinalIgnoreCase);
    private PermissionRole? _defaultJoinerRole;
    private WorldMode _mode = WorldMode.Builder;

    // Resolved assignment per live actor, so the common path does not hash identity strings on every
    // gated write. Dropped wholesale whenever the policy changes; never filled for an actor whose
    // identity the host has not authored yet, because that answer would be wrong the moment it arrives.
    private readonly Dictionary<ulong, PermissionRole?> _assignmentCache = new();

    // THE GROUP ROSTER, AS THE HOST FETCHED IT
    // Account id -> the group service's own rung name, plus the group's ban list. Only the authority
    // ever fills these (the roster component does the fetching) and nothing that arrives over the wire
    // can reach them, so a client claiming to be a group moderator claims it to nobody. Empty until the
    // first fetch lands, which is the fail-closed answer: no members-only door opens and nobody is a
    // group moderator while we have not heard back. -xlinka
    private readonly Dictionary<string, string> _groupRoster = new(StringComparer.Ordinal);
    private readonly HashSet<string> _groupBans = new(StringComparer.Ordinal);

    private readonly PermissionLedger _ledger = new();

    // Where denial and refusal messages go. The engine carries no logger of its own.
    public Action<string>? Warn { get; set; }

    // Where a peer that keeps tripping the gate gets dealt with. Warden knows nothing about connections;
    // the adapter wires this to the session.
    public IPermissionEnforcement? Enforcement { get; set; }

    // Seconds, monotonic. Only the denial ledger reads it. Settable so a test can drive decay without
    // sleeping.
    public Func<double>? Clock { get; set; }

    public PermissionLedger Ledger => _ledger;

    // The mode the world was hosted in. Set by ApplyMode; the per-domain ceilings read it.
    public WorldMode Mode => _mode;

    // Owner of the world - full power, not assignable (you can't demote the host).
    public PermissionRole HostRole { get; }
    // Assignable roles, in descending power. Each has preset capabilities over OTHER users' objects
    // (own objects are always fully editable). The host assigns these per access-class or per-user.
    public PermissionRole AdminRole { get; }
    public PermissionRole BuilderRole { get; }
    public PermissionRole ModeratorRole { get; }
    public PermissionRole GuestRole { get; }
    public PermissionRole SpectatorRole { get; }
    public IReadOnlyList<PermissionRole> AssignableRoles { get; }

    private readonly Dictionary<PermissionAccessClass, PermissionRole> _defaultRoles = new();

    private bool _enabled = true;

    // Master switch for permission enforcement. Disabling it makes every Authorize() pass, so it is
    // HOST-AUTHORITATIVE and FAIL-CLOSED: only the authority may turn enforcement off, and only while
    // the world context is known. A guest (or any non-authority code path) that tries to clear it is
    // ignored - enforcement can never be silently killed from a non-host path. Re-enabling is always
    // allowed (it only tightens). -xlinka
    public bool Enabled
    {
        get => _enabled;
        set
        {
            // Re-enabling always allowed - it can only tighten enforcement.
            if (value)
            {
                _enabled = true;
                return;
            }

            // Disabling requires a known, authoritative context. Fail closed otherwise.
            if (_world == null || _world.IsDisposed || !_world.IsAuthority)
            {
                Warn?.Invoke("Refused to disable datamodel permission enforcement: not host-authoritative.");
                return;
            }

            _enabled = false;
        }
    }

    public bool LogDeniedMutations { get; set; } = true;

    // Social/Event lockdown. When set, the authored world is frozen for EVERYONE (including the host
    // and admins): only a user's OWN runtime objects (avatar, spawned items) may be mutated. World
    // content is created under the authority id, so no user "owns" it and all editing of it is
    // denied - there's no role that escapes this and no live toggle. Its grabbable things may still be
    // picked up and its hinges turned, by the ordinary grab rules. Set from the world mode by ApplyMode.
    public bool SocialLock { get; set; }

    public PermissionEngine(IPermissionWorldFacts world, IPermissionIdSpace ids)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));

        var all = PermissionAction.All;
        var view = PermissionAction.Read | PermissionAction.CollectionEnumerate;
        // Builders edit shared content completely, including deleting components and resizing collections.
        // The person boundary, world freeze and configuration locks still decide which targets they may edit.
        // -xlinka
        var build = view | PermissionAction.Mutation;
        var moderate = view | PermissionAction.Destroy | PermissionAction.CollectionRemove
            | PermissionAction.CollectionClear;

        HostRole = new PermissionRole("Host", all, all);
        AdminRole = new PermissionRole("Admin", all, all);
        BuilderRole = new PermissionRole("Builder", all, build);
        ModeratorRole = new PermissionRole("Moderator", all, moderate);
        // "User": full control of your OWN objects, view-only on others'. Shown as the normal-member
        // role (the social/event role set is Moderator / User / Spectator).
        GuestRole = new PermissionRole("User", all, view);

        // Spectator is view-only on OTHER people's things, not on its own. It used to be view/view, which
        // read as "cannot write anything at all, including the objects it minted itself" - and since Event
        // worlds default every visitor to Spectator, that meant nobody in an event could stand up their own
        // per-peer rig: no hand tool, no laser, no touch relay, so no interacting with the authored world
        // that an event exists to show you. What makes a spectator a spectator is the FOREIGN mask (they
        // cannot touch the world) and the Spawn domain ceiling (an event forbids bringing items in, and
        // GrabSpawnerBase reads that ceiling directly), not being unable to author their own body. -xlinka
        SpectatorRole = new PermissionRole("Spectator", all, view);
        AssignableRoles = new[] { AdminRole, BuilderRole, ModeratorRole, GuestRole, SpectatorRole };

        _defaultRoles[PermissionAccessClass.Anonymous] = SpectatorRole;
        _defaultRoles[PermissionAccessClass.Visitor] = GuestRole;
        _defaultRoles[PermissionAccessClass.Contact] = BuilderRole;
        _defaultRoles[PermissionAccessClass.Host] = AdminRole;
        _defaultRoles[PermissionAccessClass.Group] = BuilderRole;

        foreach (var role in new[] { HostRole, AdminRole, BuilderRole, ModeratorRole, GuestRole, SpectatorRole })
            _rolesByName[role.Name] = role;

        // COMPILED DOMAIN CAPS. These are the floor a missing or nonsense config falls back to, so they
        // are written tight: a role gets a domain here only if it would be strange for it NOT to have it.
        // Export is the one that stays off by default even for people who can build, because taking
        // content off the machine is a different ask from editing it in place. -xlinka
        // Script runs for every role but Spectator, in every mode. What a person's script may touch is what they may:
        // the floor and the role keep it to their own things, and it moves no one but its author. -xlinka
        _compiledCaps[HostRole] = new DomainCaps(true, true, true, true, true, true);
        _compiledCaps[AdminRole] = new DomainCaps(true, true, true, true, true, true);
        _compiledCaps[BuilderRole] = new DomainCaps(true, true, true, true, true, true);
        _compiledCaps[ModeratorRole] = new DomainCaps(true, true, false, true, true, true);
        _compiledCaps[GuestRole] = new DomainCaps(true, true, false, false, true, true);
        _compiledCaps[SpectatorRole] = new DomainCaps(false, false, false, false, true, false);

        RebuildResolvedCaps();
    }

    // CONFIGURATION
    // One snapshot in, everything rebuilt. Called by the adapter whenever the host's config component
    // changes; there is no path that reads the data model from inside a decision. A null policy is the
    // same as an empty one: back to the compiled defaults, never wider. -xlinka
    public void ApplyPolicy(PermissionPolicy? policy)
    {
        _assignedByMachine.Clear();
        _assignedByAccount.Clear();
        _toggles.Clear();
        _assignmentCache.Clear();
        _defaultJoinerRole = null;

        if (policy != null)
        {
            foreach (var assignment in policy.Assignments)
            {
                if (assignment == null || !TryResolveAssignableRole(assignment.RoleName, out var role))
                    continue;

                if (!string.IsNullOrEmpty(assignment.AccountKey))
                    _assignedByAccount[assignment.AccountKey!] = role;
                if (!string.IsNullOrEmpty(assignment.MachineKey))
                    _assignedByMachine[assignment.MachineKey!] = role;
            }

            foreach (var cap in policy.Caps)
            {
                if (cap == null || string.IsNullOrEmpty(cap.RoleName) || !_rolesByName.ContainsKey(cap.RoleName!))
                    continue;
                _toggles[cap.RoleName!] = new[] { cap.Spawn, cap.SaveCopy, cap.Export, cap.ToolUse, cap.Touch, cap.Script };
            }

            // A default that names an unknown or non-assignable role is discarded rather than guessed at:
            // the per-access-class default that the mode baked in is the safe answer.
            if (TryResolveAssignableRole(policy.DefaultJoinerRole, out var joinerRole))
                _defaultJoinerRole = joinerRole;

            _ledger.Settings = policy.Escalation ?? new PermissionEscalationSettings();
        }
        else
        {
            _ledger.Settings = new PermissionEscalationSettings();
        }

        RebuildResolvedCaps(policy);
    }

    private bool TryResolveAssignableRole(string? name, out PermissionRole role)
    {
        role = GuestRole;
        if (string.IsNullOrWhiteSpace(name) || !_rolesByName.TryGetValue(name!, out var found))
            return false;

        // Host is not assignable. You cannot hand someone the world.
        if (ReferenceEquals(found, HostRole))
            return false;

        role = found;
        return true;
    }

    private void RebuildResolvedCaps(PermissionPolicy? policy = null)
    {
        _resolvedCaps.Clear();
        foreach (var pair in _compiledCaps)
            _resolvedCaps[pair.Key] = pair.Value;

        if (policy == null)
            return;

        foreach (var cap in policy.Caps)
        {
            if (cap == null || string.IsNullOrEmpty(cap.RoleName)
                || !_rolesByName.TryGetValue(cap.RoleName!, out var role)
                || !_resolvedCaps.TryGetValue(role, out var current))
                continue;

            _resolvedCaps[role] = current.With(cap);
        }
    }

    private DomainCaps CapsFor(PermissionRole role)
        => _resolvedCaps.TryGetValue(role, out var caps) ? caps : default;


    private PermissionToggle ToggleFor(PermissionRole role, PermissionDomain domain)
        => _toggles.TryGetValue(role.Name, out var set) && (int)domain < set.Length
            ? set[(int)domain]
            : PermissionToggle.Inherit;

    // The mode whose ceilings apply. SocialLock is settable on its own (the world-mode preset is not the
    // only thing that can freeze a world), so a hand-set lock still pulls the domain ceilings down with
    // it rather than leaving tools live in a frozen world. -xlinka
    private WorldMode EffectiveMode => SocialLock && _mode == WorldMode.Builder ? WorldMode.Social : _mode;

    // Whether a user may use a whole capability domain. THE one place the mode floor lands for domains:
    // the role's compiled cap and the host's toggle resolve first, and the ceiling is applied after, so a
    // toggle can only ever restrict below it. Binds every role including the host, by design. -xlinka
    public bool AllowsDomain(IPermissionActor? actor, PermissionDomain domain)
        => AllowsDomainFor(GetRole(actor), domain);

    public bool AllowsDomainFor(PermissionRole role, PermissionDomain domain)
    {
        if (!Enabled)
            return true;

        bool allowed = CapsFor(role).Allows(domain);
        return allowed && WorldModePolicy.ModeAllows(EffectiveMode, domain);
    }

    // Bounds a requested uniform scale to the actor's role cap. Bounds at or below zero are inactive,
    // which is what an unset config reads as.
    public float ClampScale(IPermissionActor? actor, float requested)
    {
        if (!Enabled)
            return requested;

        var caps = CapsFor(GetRole(actor));
        if (caps.MinScale > 0f && requested < caps.MinScale)
            return caps.MinScale;
        if (caps.MaxScale > 0f && requested > caps.MaxScale)
            return caps.MaxScale;
        return requested;
    }

    public bool TryGetScaleBounds(IPermissionActor? actor, out float min, out float max)
    {
        var caps = CapsFor(GetRole(actor));
        min = caps.MinScale;
        max = caps.MaxScale;
        return min > 0f || max > 0f;
    }

    // Role a freshly-joined user of the given access class gets (unless overridden).
    public PermissionRole GetDefaultRole(PermissionAccessClass accessClass)
        => _defaultRoles.TryGetValue(accessClass, out var role) ? role : GuestRole;

    public void SetDefaultRole(PermissionAccessClass accessClass, PermissionRole role)
    {
        if (role != null)
            _defaultRoles[accessClass] = role;
    }

    // GROUP ROSTER
    //
    // Replaces the whole roster in one call, same shape as ApplyPolicy: the caller hands over a complete
    // snapshot and this rebuilds from it, so there is no partial state and no path that merges a half
    // answer into a good one. A failed fetch must therefore NOT call this - the previous roster standing
    // is the point.
    public void SetGroupRoster(IReadOnlyDictionary<string, string>? members, IReadOnlyCollection<string>? bans)
    {
        _groupRoster.Clear();
        _groupBans.Clear();

        if (members != null)
        {
            foreach (var pair in members)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                    _groupRoster[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        if (bans != null)
        {
            foreach (var account in bans)
            {
                if (!string.IsNullOrEmpty(account))
                    _groupBans.Add(account);
            }
        }
    }

    public bool IsGroupMember(string? accountKey)
        => !string.IsNullOrEmpty(accountKey) && _groupRoster.ContainsKey(accountKey!);

    public bool IsGroupBanned(string? accountKey)
        => !string.IsNullOrEmpty(accountKey) && _groupBans.Contains(accountKey!);

    // The rung the roster gives this account, or empty. These are the group service's words (Owner,
    // Admin, Moderator, Builder, Member), never a role of this engine's, and turning one into a world
    // role is done in exactly one place below.
    public string GroupRoleOf(string? accountKey)
        => !string.IsNullOrEmpty(accountKey) && _groupRoster.TryGetValue(accountKey!, out var role)
            ? role
            : string.Empty;

    public int GroupRosterCount => _groupRoster.Count;

    public int GroupBanCount => _groupBans.Count;

    // The top three rungs of the group service's five rung ladder. Compared by name because the words
    // are the service's; an unknown rung is not a moderator, which is the safe way for this to be wrong.
    public static bool IsGroupModeratorRank(string? groupRole)
        => string.Equals(groupRole, "Moderator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(groupRole, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(groupRole, "Owner", StringComparison.OrdinalIgnoreCase);

    // Number of users with an explicit per-user role override.
    public int UserOverrideCount => _userRoles.Count;

    // Persisted assignments the current policy carries, counted by identity key.
    public int AssignmentCount => _assignedByAccount.Count + _assignedByMachine.Count;

    public void ClearUserOverrides()
    {
        _userRoles.Clear();
        _assignmentCache.Clear();
    }

    public IDisposable EnterActor(IPermissionActor? actor) => new Scope(actor, systemBypass: false);

    // What EnterActor's scope saves and puts back, for a caller that sets the actor thousands of times a frame
    // and keeps the saved state itself: a script's node calls. Every SetActor is undone by Restore with the
    // mark it returned, innermost first. -xlinka
    public readonly struct ActorMark
    {
        internal readonly IPermissionActor? Actor;
        internal readonly int Bypass;

        internal ActorMark(IPermissionActor? actor, int bypass)
        {
            Actor = actor;
            Bypass = bypass;
        }
    }

    public static ActorMark SetActor(IPermissionActor? actor)
    {
        var mark = new ActorMark(s_currentActor, s_systemBypassDepth);
        s_currentActor = actor;
        return mark;
    }

    public static void Restore(in ActorMark mark)
    {
        s_currentActor = mark.Actor;
        s_systemBypassDepth = mark.Bypass;
    }

    // Whether the gate already takes this thread's changes for that actor, with no scope open that lifts it.
    public static bool IsActingAs(IPermissionActor? actor)
        => ReferenceEquals(s_currentActor, actor) && s_systemBypassDepth == 0 && s_authoredContentDepth == 0 && s_upkeepSource == null;

    // Who this thread is acting for inside an actor scope, or null outside one. A fact for a caller that judges by
    // the actor's account itself (an item seal), never a decision.
    public static IPermissionActor? CurrentActor => s_currentActor;

    // A struct, not an object: hot paths open one of these every frame (each dynamic bone chain, every IK
    // solve), and the class it replaced was thirty-two bytes of garbage a time across a room of avatars. It
    // puts back the actor and depth it found; a default one does nothing. -xlinka
    public readonly struct BypassScope : IDisposable
    {
        private readonly IPermissionActor? _previousActor;
        private readonly int _previousDepth;
        private readonly bool _open;

        internal BypassScope(IPermissionActor? previousActor, int previousDepth)
        {
            _previousActor = previousActor;
            _previousDepth = previousDepth;
            _open = true;
        }

        public void Dispose()
        {
            if (!_open)
                return;
            s_currentActor = _previousActor;
            s_systemBypassDepth = _previousDepth;
        }
    }

    public BypassScope EnterSystemBypass()
    {
        var scope = new BypassScope(s_currentActor, s_systemBypassDepth);
        s_systemBypassDepth++;
        return scope;
    }

    // A much narrower door than EnterSystemBypass, for the world's OWN authored logic running on the
    // authority: a button in the world flipping a light it was built alongside. Under the Social/Event
    // freeze that write is denied like any other edit of world content, which is right for a person and
    // wrong for the world's own wiring - the freeze exists to stop VISITORS rearranging the place, not to
    // stop the place from working.
    //
    // BOTH ENDS ARE PINNED TO AUTHORED CONTENT, and that is the whole design:
    //   `source` - the component asking. It must itself be authority-authored, so a visitor who spawns a
    //              control of their own and pokes it opens nothing. Without this the scope would be a
    //              door anybody could carry into a frozen world in their pocket.
    //   target   - what gets written. Must be authority-authored too, so the scope can never reach a
    //              user's belongings.
    // Plus: authority only, mutations and reads only, and only for the dynamic extent of the using block.
    //
    // Inside it the ACTOR's role is deliberately not the gate - the touching visitor is not the one
    // making the edit, the world's own wiring is, and it runs on the host either way. The full bypass
    // skips every one of the checks above, which is why authored controls should reach for this instead.
    // -xlinka
    public IDisposable EnterAuthoredContentScope(IPermissionTarget? source)
    {
        if (!_world.IsAuthority || _world.IsDisposed || !IsAuthorityAuthored(source))
            return s_inertScope;

        return new AuthoredContentScope(armed: true);
    }

    // Shared UI (an inspector, a node browser, their rows and readouts) is built and kept up by the
    // authority, and it goes on working when somebody picks the panel up or pins it to themselves: the
    // authority rebuilding the rows of a panel in somebody's hand is the panel keeping itself, not anybody
    // changing what that person holds. This is the door for exactly that.
    //
    // Narrow on purpose, and every limit is load-bearing:
    //   authority only - a peer's upkeep would arrive at the host as that peer writing into someone else's
    //                    things, which the host cannot tell from the real thing, so peers never keep shared UI;
    //   source         - the component doing its own upkeep. Only what sits on its slot or below passes, so an
    //                    inspector looking at somebody's avatar refreshes its rows and never the avatar;
    //   the person boundary only - the role, the freeze and everything after them still judge the write;
    //   never a person's root itself, and only for the dynamic extent of the using block.
    // Engine code opens it around the upkeep itself; nothing a person does reaches it. -xlinka
    public UpkeepScope EnterUpkeepScope(IPermissionTarget? source)
    {
        if (source == null || !_world.IsAuthority || _world.IsDisposed)
            return default;
        var scope = new UpkeepScope(s_upkeepSource);
        s_upkeepSource = source;
        return scope;
    }

    public readonly struct UpkeepScope : IDisposable
    {
        private readonly IPermissionTarget? _previous;
        private readonly bool _open;

        internal UpkeepScope(IPermissionTarget? previous)
        {
            _previous = previous;
            _open = true;
        }

        public void Dispose()
        {
            if (_open)
                s_upkeepSource = _previous;
        }
    }

    // Whether the authority is keeping shared UI that this element is part of, inside an upkeep scope.
    private bool InUpkeepOf(IPermissionTarget? reached)
        => s_upkeepSource is { } source && reached != null && _world.IsAuthority && reached.IsInside(source);

    // Whether the calling thread is inside a scope that opens the gate: a system bypass, an authored
    // content scope or shared UI upkeep. A script runtime refuses to start a call inside one, or the script
    // would run with the scope's rights instead of its author's.
    public static bool IsGateOpenOnThisThread => s_systemBypassDepth > 0 || s_authoredContentDepth > 0 || s_upkeepSource != null;

    // Whether anything on the ownership chain reads as a user's: under their root, which covers what
    // they wear and what they are holding. The authority mints user roots, so authority-authored alone
    // does not make something the world's.
    private static bool BelongsToAUser(IPermissionTarget? target)
    {
        for (int depth = 0; target != null && depth < 8; depth++)
        {
            if (target.StructuralOwner != null)
                return true;
            target = target.OwnershipParent;
        }
        return false;
    }

    // Whether this element was minted by the authority, walking the ownership chain the way OwnsIdStrong
    // does. A user's object never satisfies it, no matter where it is currently parented.
    private bool IsAuthorityAuthored(IPermissionTarget? target)
    {
        for (int depth = 0; target != null && depth < 8; depth++)
        {
            if (_ids.IsAuthority(target.Id))
                return true;
            if (!_ids.IsNull(target.Id) && _ids.IsValidOwnerByte(_ids.OwnerByte(target.Id)))
                return false;
            target = target.OwnershipParent;
        }
        return false;
    }

    // What a world script may do inside an object someone wears or holds: change it and add to it, never take
    // anything out of it.
    private const PermissionAction OwnObjectActions = PermissionAction.Read | PermissionAction.CollectionEnumerate
        | PermissionAction.Write | PermissionAction.ReferenceWrite | PermissionAction.Create
        | PermissionAction.CollectionAdd | PermissionAction.CollectionInsert | PermissionAction.CollectionSet;

    // A world script inside an object someone wears or holds keeps working that object, as it did before it was
    // picked up or put on: the world-made parts of the nearest object or avatar root that holds both the script
    // and what it changes. Never that root's own place, transform or parent, never the wearer's body machinery
    // or anything a person added (none of it the authority's), and nothing outside that root, the user root
    // above it included. Destroying, copying and exporting stay refused. -xlinka
    private bool InOwnWornObject(IPermissionActor actor, IPermissionTarget? reached, PermissionAction action)
    {
        if (reached == null || (action & ~OwnObjectActions) != 0 || actor.ScriptHome is not { } home || !IsAuthorityAuthored(home))
            return false;
        var root = home.SharedObjectRoot(reached);
        if (root == null || !IsAuthorityAuthored(root) || root.StructuralOwner is not { } wearer
            || ReferenceEquals(root, wearer.RootElement))
            return false;
        if (ReferenceEquals(reached, root) || ReferenceEquals(reached.OwnershipParent, root))
            return false;
        return !reached.IsWearerMachinery;
    }

    public void AddRule(IPermissionRule rule)
    {
        if (rule == null)
        {
            throw new ArgumentNullException(nameof(rule));
        }

        _rules.Add(rule);
    }

    public bool RemoveRule(IPermissionRule rule) => _rules.Remove(rule);

    public void ClearRules() => _rules.Clear();

    public void SetUserRole(IPermissionActor user, PermissionRole role)
    {
        if (user == null)
        {
            throw new ArgumentNullException(nameof(user));
        }
        if (role == null)
        {
            throw new ArgumentNullException(nameof(role));
        }

        _userRoles[user.Id] = role;
        _assignmentCache.Remove(user.Id);
    }

    public void ClearUserRole(IPermissionActor user)
    {
        if (user != null)
        {
            _userRoles.Remove(user.Id);
            _assignmentCache.Remove(user.Id);
        }
    }

    public PermissionRole GetRole(IPermissionActor? user)
    {
        if (user == null)
        {
            return GetDefaultRole(PermissionAccessClass.Anonymous);
        }

        if (IsHostUser(user))
        {
            return HostRole;
        }

        // An explicit assignment - this session's host override, or the persisted one that matched this
        // user's identity - wins. Then the host's configured joiner default, then the per-access-class
        // default the mode baked in.
        if (TryGetAssignedRole(user, out var assigned))
            return assigned;

        if (TryGetGroupModeratorRole(user, out var groupModerator))
            return groupModerator;

        return _defaultJoinerRole ?? GetDefaultRole(GetAccessClass(user));
    }

    // A group's own moderators run that group's events. EVENT ONLY: being a moderator of a group is not
    // being a moderator of somebody's build session, so in Builder and Social worlds a group member gets
    // the Group class default and nothing more. The role handed out is this world's Moderator, which the
    // Event role set already offers, so the mode floor is never crossed - there is no path from here to
    // Builder or Admin. It sits under the assigned-role check on purpose: a host who pinned a role to
    // this person meant it. -xlinka
    private bool TryGetGroupModeratorRole(IPermissionActor user, out PermissionRole role)
    {
        role = ModeratorRole;
        if (_mode != WorldMode.Event || _groupRoster.Count == 0)
            return false;
        return IsGroupModeratorRank(GroupRoleOf(user.AccountKey));
    }

    // The role EXPLICITLY pinned to this user, if any. Deliberately does not fall back to any default,
    // because GetAccessClass is built on top of it and a default that consulted the class would loop.
    private bool TryGetAssignedRole(IPermissionActor user, out PermissionRole role)
    {
        // The session override is the host reaching in live; it outranks the persisted assignment on
        // purpose, so "make them a moderator right now" is not undone by what the file says.
        if (_userRoles.TryGetValue(user.Id, out role!))
            return true;

        if (_assignmentCache.TryGetValue(user.Id, out var cached))
        {
            role = cached!;
            return cached != null;
        }

        var account = user.AccountKey;
        var machine = user.MachineKey;

        PermissionRole? found = null;
        if (!string.IsNullOrEmpty(account) && _assignedByAccount.TryGetValue(account!, out var byAccount))
            found = byAccount;
        else if (!string.IsNullOrEmpty(machine) && _assignedByMachine.TryGetValue(machine!, out var byMachine))
            found = byMachine;

        // Only cache once the host has actually authored an identity for this user. Caching "no
        // assignment" for a user whose MachineID has not synced yet would pin the wrong answer for the
        // rest of the session. -xlinka
        if (!string.IsNullOrEmpty(machine))
            _assignmentCache[user.Id] = found;

        role = found!;
        return found != null;
    }

    // Classify a user for default-role purposes. The host is detected from the world's authority flag; an
    // ASSIGNED user is classified by the tier their role belongs to, which is what finally makes the
    // Contact/Anonymous defaults reachable instead of dead entries in a table. An unassigned user stays a
    // Visitor, the conservative answer, exactly as before.
    //
    // Group sits between the two: after the host check and the explicit assignment, before the Visitor
    // fallback. The only thing that puts anyone in it is the roster the HOST fetched, keyed on the account
    // id the host authored during the handshake. A client sends nothing that can reach either. Until the
    // first fetch lands the roster is empty and everyone is a Visitor, which is the answer that refuses
    // rather than the one that lets people in. -xlinka
    public PermissionAccessClass GetAccessClass(IPermissionActor? user)
    {
        if (user == null)
        {
            return PermissionAccessClass.Anonymous;
        }

        if (IsHostUser(user))
        {
            return PermissionAccessClass.Host;
        }

        if (TryGetAssignedRole(user, out var role))
        {
            return AccessClassOf(role);
        }

        if (IsGroupMember(user.AccountKey))
        {
            return PermissionAccessClass.Group;
        }

        return PermissionAccessClass.Visitor;
    }

    // Which tier a role speaks for. Assigning someone Builder is a statement that they are trusted like a
    // contact; assigning Spectator is a statement that they are not trusted at all.
    public PermissionAccessClass AccessClassOf(PermissionRole role)
    {
        if (ReferenceEquals(role, HostRole) || ReferenceEquals(role, AdminRole))
            return PermissionAccessClass.Host;
        if (ReferenceEquals(role, BuilderRole) || ReferenceEquals(role, ModeratorRole))
            return PermissionAccessClass.Contact;
        if (ReferenceEquals(role, SpectatorRole))
            return PermissionAccessClass.Anonymous;
        return PermissionAccessClass.Visitor;
    }

    public PermissionRole? FindRole(string? name)
        => !string.IsNullOrWhiteSpace(name) && _rolesByName.TryGetValue(name!, out var role) ? role : null;

    // Roles a host may assign to users in the given mode (the per-mode role set).
    public IReadOnlyList<PermissionRole> AssignableRolesFor(WorldMode mode)
    {
        // Social + Event are view/interact spaces: only moderation, normal user, and spectator make
        // sense - no Builder/Admin (there is nothing to build).
        if (mode == WorldMode.Social || mode == WorldMode.Event)
            return new[] { ModeratorRole, GuestRole, SpectatorRole };

        return AssignableRoles;
    }

    // The seed the world mode hands one access class, on its own, with no host config on top.
    //
    // Split out of ApplyMode because the host's config now overrides these PER CLASS: a row the host
    // cleared has to go back to exactly the role the mode would have given it, and the only way that
    // stays true is for both paths to read one table. Answer a new class here or it silently inherits
    // Visitor's answer. -xlinka
    public PermissionRole SeedRoleFor(WorldMode mode, PermissionAccessClass accessClass)
    {
        if (accessClass == PermissionAccessClass.Host)
            return AdminRole;
        if (accessClass == PermissionAccessClass.Anonymous)
            return SpectatorRole;

        return mode switch
        {
            // Frozen world; users may still bring/handle their own items (Guest = "User": own objects
            // fully editable, the world view-only). The SocialLock floor denies any edit of the authored
            // world for everyone, host included.
            WorldMode.Social => GuestRole,
            // Strictest: view + interact only, no spawning even of your own items.
            WorldMode.Event => SpectatorRole,
            _ => BuilderRole
        };
    }

    // Load a mode's preset: the lock floor plus the default role per access class. Baked at host time
    // from the world mode. The floor value comes from WorldModePolicy so it is defined in exactly one
    // place.
    public void ApplyMode(WorldMode mode)
    {
        _mode = mode;
        SocialLock = WorldModePolicy.SocialLockFloor(mode);

        SetDefaultRole(PermissionAccessClass.Anonymous, SeedRoleFor(mode, PermissionAccessClass.Anonymous));
        SetDefaultRole(PermissionAccessClass.Visitor, SeedRoleFor(mode, PermissionAccessClass.Visitor));
        SetDefaultRole(PermissionAccessClass.Contact, SeedRoleFor(mode, PermissionAccessClass.Contact));
        SetDefaultRole(PermissionAccessClass.Group, SeedRoleFor(mode, PermissionAccessClass.Group));
    }

    public bool Authorize(in PermissionRequest request, out string? reason)
    {
        reason = null;
        if (!Enabled)
        {
            return true;
        }

        var world = request.World ?? _world;
        if (world.IsDisposed || !world.IsRunning)
        {
            return true;
        }

        if (s_systemBypassDepth > 0 || IsSystemMutation(request))
        {
            return true;
        }

        if (request.IsNetwork && !world.IsAuthority)
        {
            return true;
        }

        // Pure reads/enumeration never mutate the data model, so they need neither an actor nor a role -
        // ANYTHING may read, including engine-side render hooks that read synced lists (e.g. a renderer
        // reading its Materials list) on a thread with no actor context. Without this a guest's render
        // hook throws "no actor for datamodel mutation" every frame. Scoped tightly to Read/Enumerate so
        // Replicate/Serialize (network send) and real mutations still go through ownership. -xlinka
        const PermissionAction readOnlyActions =
            PermissionAction.Read | PermissionAction.CollectionEnumerate;
        if ((request.Action & ~readOnlyActions) == 0)
        {
            return true;
        }

        var actor = request.IsNetwork
            ? request.Actor
            : request.Actor ?? s_currentActor ?? world.LocalActor;
        if (actor == null)
        {
            reason = "no actor for datamodel mutation";
            return Deny(request, reason, PermissionDenialKind.Unclassified);
        }

        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(request, actor, out var ruleReason);
            if (result == PermissionResult.Allow)
            {
                return true;
            }
            if (result == PermissionResult.Deny)
            {
                reason = ruleReason ?? "denied by datamodel permission rule";
                return Deny(request, reason, PermissionDenialKind.Unclassified);
            }
        }

        // World scripts act for the world, so ownership and roles have nothing to say about them. The one
        // question is whether the target is the world's own content and no user is holding or wearing it,
        // or else a part of the very object the script sits in (InOwnWornObject).
        // Above the freeze on purpose: the world's authored logic has to keep working in a frozen world,
        // the same reasoning as EnterAuthoredContentScope. Copy and export are never theirs to ask for.
        // The slot registry is world content itself, so there the key is what counts: a world script adds
        // and removes world slots only, never a user's. -xlinka
        if (actor.IsWorldContent)
        {
            const PermissionAction worldScriptActions =
                PermissionAction.Mutation | PermissionAction.Read | PermissionAction.CollectionEnumerate;
            var reached = request.Target ?? request.Parent;
            bool worldKey = !ReferenceEquals(request.Target, world.SlotRegistry)
                || (request.Action & PermissionAction.Mutation) == 0
                || (_ids.TryGetElementId(request.Key, out var registryKey) && _ids.IsAuthority(registryKey));
            if (world.IsAuthority
                && (request.Action & ~worldScriptActions) == 0
                && IsAuthorityAuthored(reached)
                && worldKey
                && (!BelongsToAUser(reached) || InOwnWornObject(actor, reached, request.Action)))
            {
                return true;
            }
            reason = "a world script may only change world content";
            return Deny(request, reason, PermissionDenialKind.ForeignWrite);
        }

        // THE PERSON BOUNDARY. What hangs under somebody's user root is theirs: their body, the avatar they wear, what
        // they hold. Nobody else changes any of it, whatever their role, the host included, unless that person said
        // yes to them for this session; the person's own record says so and nothing the asker sends counts. With the
        // yes, the asker is judged below as the person would be, so a held world prop stays the role's question and
        // copying stays its own; and never on the person's root itself (where they stand, what it hangs from, its own
        // components, removing it). Authored grab interactions pass for their protocol fields only, including
        // grabbable accessories and bone chains; their grab restrictions still apply. Moderation writes the
        // user record, which is not under the root, and the engine's own work on bodies
        // (building, respawning, tearing down, the solvers) runs past the gate. Every mode. -xlinka
        var owner = actor;
        bool asPerson = false;
        if ((request.Action & PermissionAction.Mutation) != 0
            && PersonReached(in request, world, out bool rootLevel, out var reachedTarget) is { } person && person.Id != actor.Id)
        {
            bool consented = person.AllowsEditsBy(actor);
            if (consented && !rootLevel)
            {
                owner = person;
                // Their own things answer as they would, their role included; something they only hold stays the
                // actor's role's question, or a yes would hand out whatever the person happens to carry.
                asPerson = reachedTarget == null || !reachedTarget.IsCarriedBy(person) || OwnsIdStrong(person, reachedTarget);
            }
            else if (!rootLevel && KeepsSharedUi(in request, reachedTarget))
            {
                // Pointing at a panel they hold, or the authority keeping it up: past the boundary only, the role
                // and the freeze below still decide.
            }
            else if (rootLevel || !IsAllowedGrabWrite(in request, actor, world, out _))
            {
                reason = consented
                    ? "a person's own root is never anyone else's to change, even with their yes"
                    : "another person's body and what they hold are theirs to change; ask them first";
                // Whatever moved into or out of their hands while the write was on the wire was not aimed at them.
                return Deny(request, reason, request.IsNetwork && reachedTarget?.ChangedHandsRecently == true
                    ? PermissionDenialKind.StaleView
                    : (request.Action & RemovalActions) != 0
                        ? PermissionDenialKind.Ownership
                        : PermissionDenialKind.ForeignWrite);
            }
        }

        var role = GetRole(actor);
        var personRole = asPerson ? GetRole(owner) : role;
        var question = RoleQuestion(request.Action);
        bool ownsTarget = OwnsTarget(owner, request.Target) ||
                          OwnsTarget(owner, request.Parent);

        // Own-byte slot-REGISTRY adds: the flat world slot registry lives in the authority byte with a null
        // parent, so neither Target (the registry) nor Parent reads as owned - that wrongly denies a guest
        // REGISTERING its own slot (the per-peer hand tool / laser rig, or spawning its own content). The new
        // slot's KEY is its id, minted in the creator's own byte, so an add keyed in the actor's byte INTO
        // THE SLOT REGISTRY is an own-object create and must authorize (locally so the build doesn't throw, and
        // on the host so it accepts the guest's replicated add).
        // SCOPED TO THE REGISTRY ON PURPOSE: a per-slot collection (e.g. a slot's component list) has
        // Parent = its owning slot, so OwnsTarget(Parent) above already answers correctly - attaching a
        // component to a HOST slot is denied, and to the guest's OWN slot is allowed (the slot is in the guest's
        // byte). We must NOT relax those here, or a guest could bolt live components onto host geometry. The
        // registry is the only flat (null-parent) collection a guest legitimately writes; user streams go via
        // Parent=own-user and users are host-managed. -xlinka
        //
        // Removals too: a registry removal keyed in the actor's byte is the actor destroying a slot it
        // minted. Without this a guest could register its own things and never delete them again, every
        // destroy of its own slot refused and the local destroy throwing half way. The key is the slot's id,
        // which a grab never changes, so this is the strong kind of ownership as well. -xlinka
        bool ownsRegistryKey = ReferenceEquals(request.Target, world.SlotRegistry) &&
            _ids.TryGetElementId(request.Key, out var keyId) &&
            OwnsId(owner, keyId);
        if (!ownsTarget && ownsRegistryKey &&
            (request.Action & (PermissionAction.CollectionAdd | PermissionAction.CollectionInsert | PermissionAction.CollectionRemove)) != 0)
        {
            ownsTarget = true;
        }

        // Taking one's own entry back out of a list: a user counted into a zone's list of who is inside, by its
        // own machine, has to be able to count itself out again. The entry is the user's own the same strong
        // way a registry key is, minted in its byte when it was added, and the list keys a removal with the
        // removed entry's id. Removal alone: putting entries in stays the list owner's and the role's call,
        // and nobody takes out an entry someone else added. -xlinka
        bool ownsRemovedEntry = !ownsTarget && request.Surface == PermissionSurface.List &&
            (request.Action & PermissionAction.CollectionRemove) != 0 &&
            (request.Action & (PermissionAction.CollectionAdd | PermissionAction.CollectionInsert | PermissionAction.CollectionSet |
                PermissionAction.CollectionClear | PermissionAction.CollectionResize | PermissionAction.Create)) == 0 &&
            _ids.TryGetElementId(request.Key, out var entryId) &&
            OwnsId(owner, entryId);
        if (ownsRemovedEntry)
        {
            ownsTarget = true;
        }

        // SAVE-COPY / EXPORT. Neither one changes the world, so they are settled here, before the freeze
        // (which only speaks about mutations) and before the role check (whose foreign masks are only one
        // of the three things a copy has to satisfy). A request that carries nothing else is finished
        // either way; a request that carries copy bits alongside real mutations falls through so the rest
        // is still judged. -xlinka
        if ((request.Action & PermissionAction.Copy) != 0)
        {
            if (!AuthorizeCopy(in request, role, actor, out var copyReason))
            {
                reason = copyReason;
                return Deny(request, reason!, PermissionDenialKind.ForeignWrite);
            }

            if ((request.Action & ~PermissionAction.Copy) == 0)
            {
                return true;
            }
        }

        // The world's own authored logic, running on the authority against authority-authored content.
        // Narrow on purpose - see EnterAuthoredContentScope. Sits here, above the freeze, because the
        // freeze is the only gate it is meant to lift: everything before this point (system bypass, actor
        // resolution, registered rules) has already run, and nothing after it is skipped for a target this
        // scope does not cover.
        if (s_authoredContentDepth > 0
            && world.IsAuthority
            && (request.Action & ~(PermissionAction.Mutation | PermissionAction.Read | PermissionAction.CollectionEnumerate)) == 0
            && IsAuthorityAuthored(request.Target ?? request.Parent))
        {
            return true;
        }

        // Owned only by holding it: a held foreign object rides under the holder's root, and so does
        // everything it carries along, and OwnsTarget reads all of that as ownership. Worked out once for
        // the freeze and for the role question below. -xlinka
        bool carriedOnly = ownsTarget && (request.Action & PermissionAction.Mutation) != 0
            && IsCarriedOnly(in request, owner, ownsRegistryKey || ownsRemovedEntry);

        // Social/Event floor: the authored world is frozen for EVERYONE incl. the host. Only a user's
        // own runtime objects may be mutated; world content (authority-owned) is foreign to all and
        // denied regardless of role, held or not. This is the unbypassable lock - no role escapes it, no
        // live toggle. Authored grab interactions use the same rules as an open world, including a wearable
        // that permits other users to grab it. Every other edit of world content stays frozen. -xlinka
        if (SocialLock && (!ownsTarget || carriedOnly) && (request.Action & PermissionAction.Mutation) != 0)
        {
            if (IsAllowedGrabWrite(in request, actor, world, out _))
            {
                return true;
            }
            // Taking away what someone brought goes on to the role, which only a moderator's and up allows.
            if (!IsItemRemoval(in request, actor, world))
            {
                reason = "editing is disabled in this world (social)";
                return Deny(request, reason, PermissionDenialKind.LockedWorld);
            }
        }

        // Grab interactions: a grabbable object opts into being picked up + moved by ANY user - that's an
        // interaction, not an ownership edit. Allow the grab-state refs (who holds it / its restore-parent)
        // and the reparent+pose, but ONLY for the user who is actually holding the thing. The host still
        // owns the object and arbitrates the authoritative holder. Ordinary edits to the object stay owner-gated
        // by the role check below. -xlinka
        if (IsAllowedGrabWrite(in request, actor, world, out bool grabTraffic))
        {
            return true;
        }

        // A held foreign object rides under the holder's root, and OwnsTarget reads that as ownership - so
        // without this, picking something up would hand you full edit rights over it for as long as you
        // held it (retune its throw limits, swap its material, rewrite its components). Everything a grab
        // legitimately writes was already allowed above, so anything still arriving here is an ordinary
        // edit of someone else's property and must answer the role's FOREIGN question. That goes for what
        // the held object carries along too, its child slots included. Strips only the grab-flip: real
        // per-byte ownership still reads as owned, so your own equipment is untouched, and a role that may
        // edit foreign objects anyway (host, admin, builder) still can. -xlinka
        if (carriedOnly)
        {
            ownsTarget = false;
            if (!role.Allows(question, ownsTarget))
            {
                reason = "editing a held object requires ownership, not just holding it";
                return Deny(request, reason, PermissionDenialKind.ForeignWrite);
            }
        }

        // Destroying / removing / clearing an object you only "own" because you are holding it is forbidden. A
        // grab parents the object under the grabber and flips the structural owner, which OwnsTarget reads as
        // ownership - that would let a client destroy host content on the next batch.
        // Destructive ops therefore require REAL per-byte ownership, not the structural signal. This only ever
        // removes authority that came from the grab-flip: if the actor really owns it (own-byte equipment)
        // strong ownership holds and this is a no-op; if they don't own it at all the role check below already
        // denies. The grab interaction (move / reparent / pose / grab-state) was allowed above and never
        // reaches here. Holding conveys no destroy authority. -xlinka
        if ((request.Action & DestructiveActions) != 0 && ownsTarget)
        {
            bool ownsTargetStrong = OwnsIdStrong(owner, request.Target) ||
                                    OwnsIdStrong(owner, request.Parent) ||
                                    ownsRegistryKey || ownsRemovedEntry;
            if (!ownsTargetStrong)
            {
                // Strip the grab-flip signal and ask the role the real question, as if the actor were not
                // holding it. Denying outright here took away authority the role grants: the host deleting a
                // component off something in its own hand was refused, then allowed the moment it let go. A
                // guest holding host content still fails, because its role cannot destroy foreign objects.
                // Acting on a person's yes, the question is the person's: what they may take out of their own
                // body. Never more than that, and nothing they only hold gets this far. -xlinka
                ownsTarget = false;
                if (!personRole.Allows(question, ownsTarget))
                {
                    reason = "destroying a held object requires ownership, not just holding it";
                    return Deny(request, reason, PermissionDenialKind.Ownership);
                }
                if (asPerson)
                    return true;
            }
        }

        if (role.Allows(question, ownsTarget) || (asPerson && personRole.Allows(question, ownsTarget)))
        {
            return true;
        }

        reason = $"role '{role.Name}' cannot perform {request.Action} on {request.Surface}";
        return Deny(request, reason, grabTraffic
            ? PermissionDenialKind.GrabContention
            : ClassifyDenial(in request, world));
    }

    // What a refused write was reaching for, for the escalation ledger's weighting. Destroying or
    // unregistering something you do not own is a decision; a plain field write on someone else's object
    // is what a stale client does by accident. -xlinka
    private static PermissionDenialKind ClassifyDenial(in PermissionRequest request, IPermissionWorldFacts world)
    {
        if (ReferenceEquals(request.Target, world.SlotRegistry) || ReferenceEquals(request.Parent, world.SlotRegistry))
            return PermissionDenialKind.Ownership;

        if ((request.Action & DestructiveActions) != 0)
            return PermissionDenialKind.Ownership;

        return PermissionDenialKind.ForeignWrite;
    }

    public void Assert(in PermissionRequest request)
    {
        if (!Authorize(request, out var reason))
        {
            throw new UnauthorizedAccessException(reason ?? "datamodel mutation denied");
        }
    }

    // Authorize's answer, asked without acting on it: nothing logged, nothing counted. For an offer drawn
    // before anyone presses it.
    public bool Allows(in PermissionRequest request)
    {
        s_quietDepth++;
        try
        {
            return Authorize(in request, out _);
        }
        finally
        {
            s_quietDepth--;
        }
    }

    // The same, as the request's actor stands with no bypass or authored scope open on this thread. For a
    // caller already running as the system that has to know whether the person it is acting for could do
    // the thing themselves. It can only ever say less than Authorize does. -xlinka
    public bool AllowsUnscoped(in PermissionRequest request)
    {
        int bypass = s_systemBypassDepth;
        int authored = s_authoredContentDepth;
        var upkeep = s_upkeepSource;
        s_systemBypassDepth = 0;
        s_authoredContentDepth = 0;
        s_upkeepSource = null;
        try
        {
            return Allows(in request);
        }
        finally
        {
            s_systemBypassDepth = bypass;
            s_authoredContentDepth = authored;
            s_upkeepSource = upkeep;
        }
    }

    private bool Deny(in PermissionRequest request, string reason, PermissionDenialKind kind)
    {
        var actor = request.IsNetwork
            ? request.Actor
            : request.Actor ?? s_currentActor ?? request.World?.LocalActor;

        if (s_quietDepth > 0)
            return false;

        if (LogDeniedMutations)
        {
            var actorName = actor?.DisplayName ?? "none";
            var target = request.Target?.HierarchyPath ?? request.Parent?.HierarchyPath ?? "(unknown)";

            // Log each DISTINCT denial once, not every frame. A driven/own-body write that keeps getting denied
            // would otherwise spam thousands of identical lines and bury everything else in the console. -xlinka
            var key = $"{actorName}|{request.Action}|{request.Surface}|{target}|{reason}";
            bool firstTime;
            lock (_loggedDenials)
            {
                firstTime = _loggedDenials.Add(key);
                if (_loggedDenials.Count > 512)
                    _loggedDenials.Clear();
            }

            if (firstTime)
                Warn?.Invoke($"Datamodel permission denied: actor={actorName}, action={request.Action}, surface={request.Surface}, target={target}, reason={reason}");
        }

        // Only a refusal of something that arrived OVER THE WIRE counts against a peer. A local denial is
        // this machine refusing its own optimistic write, which every client does constantly and honestly.
        if (request.IsNetwork)
            ReportDenial(actor, kind, reason);

        return false;
    }

    // VIOLATION ESCALATION
    // Records one refused inbound write against the sender and acts if their score crosses the host's
    // threshold. Public because refusals that never reach Authorize - a host-only member forged on the
    // wire, a link claim the arbitration ledger rejected - are exactly the ones worth the most weight, and
    // the adapter reports them here. -xlinka
    public void ReportDenial(IPermissionActor? actor, PermissionDenialKind kind, string? reason = null)
    {
        if (actor == null || !_world.IsAuthority || _world.IsDisposed)
            return;

        // Never escalate against the host: it cannot kick itself, and its own denials are the floor doing
        // its job (a locked world refuses the host too).
        if (IsHostUser(actor))
            return;

        var identity = IdentityOf(actor);
        if (identity == null)
            return;

        var verdict = _ledger.Record(identity, kind, Now, out double score, out bool warn);

        if (warn)
        {
            Warn?.Invoke($"Permission violations from '{actor.DisplayName ?? identity}': score {score:0.0} "
                + $"({_ledger.CountFor(identity)} refusals, latest {kind}{(reason != null ? ": " + reason : string.Empty)})");
            Enforcement?.WarnHost(actor, score, $"{_ledger.CountFor(identity)} refused writes, latest {kind}");
        }

        switch (verdict)
        {
            case PermissionViolationResponse.Kick:
                Warn?.Invoke($"Kicking '{actor.DisplayName ?? identity}': permission violation score {score:0.0} (latest {kind}).");
                Enforcement?.Kick(actor, $"permission violation score {score:0.0}, latest {kind}");
                break;

            case PermissionViolationResponse.TempBan:
                Warn?.Invoke($"Temp-banning '{actor.DisplayName ?? identity}': permission violation score {score:0.0} (latest {kind}).");
                Enforcement?.TempBan(actor, $"permission violation score {score:0.0}, latest {kind}");
                break;
        }
    }

    // The key a peer's denial score hangs off. Account first so it survives a machine change, machine as
    // the fallback that always exists. A peer with neither is not tracked - the host authors both, so that
    // only happens before the handshake finished, and there is nothing to escalate against yet.
    public static string? IdentityOf(IPermissionActor actor)
    {
        var account = actor.AccountKey;
        if (!string.IsNullOrEmpty(account))
            return "a:" + account;

        var machine = actor.MachineKey;
        return string.IsNullOrEmpty(machine) ? null : "m:" + machine;
    }

    // Seconds on whatever clock the ledger is keeping. Exposed because anything asking the ledger a
    // question has to ask it on the SAME clock the entries were written on, or a decay computed against
    // wall time reads every score as zero. -xlinka
    public double Now => Clock?.Invoke() ?? (Environment.TickCount64 / 1000.0);

    public double DenialScoreOf(IPermissionActor? actor)
    {
        if (actor == null)
            return 0.0;
        var identity = IdentityOf(actor);
        return identity == null ? 0.0 : _ledger.ScoreFor(identity, Now);
    }

    // SAVE-COPY / EXPORT
    // Three separate questions, all of which have to say yes for someone else's object: the role may copy
    // at all (its cap plus the mode ceiling), the role may copy things it does not own (the compiled
    // foreign mask, which an explicit Grant is the only thing that widens), and the object is not marked
    // against it. Your own object skips all three. -xlinka
    private bool AuthorizeCopy(in PermissionRequest request, PermissionRole role, IPermissionActor actor, out string? reason)
    {
        reason = null;
        var action = request.Action & PermissionAction.Copy;

        // Ownership on the copy path is the allocation byte ONLY. The structural signal flips the moment
        // anything is parented under the actor's root (a grab, an equip), and "I am holding it" must
        // never mean "it is mine to copy". A mutation needs the structural signal so you can pose what
        // you carry; a copy needs nothing of the sort. -xlinka
        if (OwnsIdStrong(actor, request.Target) || OwnsIdStrong(actor, request.Parent))
            return true;

        // The authority is already holding every byte of this world in its own memory. A marker that
        // "stopped" the machine serving the world would be theatre, and we do not ship controls that only
        // look like controls. -xlinka
        if (IsHostUser(actor))
            return true;

        var protection = (request.Target ?? request.Parent)?.CopyProtection;
        if (protection != null)
        {
            if ((action & PermissionAction.SaveCopy) != 0 && protection.BlocksSaveCopy)
            {
                reason = "this object is marked as not copyable";
                return false;
            }
            if ((action & PermissionAction.Export) != 0 && protection.BlocksExport)
            {
                reason = "this object is marked as not exportable";
                return false;
            }
        }

        if ((action & PermissionAction.SaveCopy) != 0
            && !AllowsForeignCopy(role, PermissionDomain.SaveCopy, PermissionAction.SaveCopy))
        {
            reason = $"role '{role.Name}' cannot save a copy of another user's object";
            return false;
        }

        if ((action & PermissionAction.Export) != 0
            && !AllowsForeignCopy(role, PermissionDomain.Export, PermissionAction.Export))
        {
            reason = $"role '{role.Name}' cannot export another user's object";
            return false;
        }

        return true;
    }

    private bool AllowsForeignCopy(PermissionRole role, PermissionDomain domain, PermissionAction action)
    {
        if (!AllowsDomainFor(role, domain))
            return false;

        // Compiled answer first: only the host tier carries copy rights over other people's objects. An
        // explicit Grant is what widens it; Inherit leaves it exactly where the compiled cap put it, which
        // is the whole point of the tri-state.
        if (role.Allows(action, ownsTarget: false))
            return true;

        return ToggleFor(role, domain) == PermissionToggle.Grant;
    }

    private static bool IsSystemMutation(in PermissionRequest request)
    {
        if (!request.IsNetwork && request.Target is { IsLocalElement: true })
        {
            return true;
        }

        if (request.Member is { PermissionKind: PermissionTargetKind.SyncElement, IsInitializing: true })
        {
            return true;
        }

        return request.Target is { PermissionKind: PermissionTargetKind.SyncElement, IsInitializing: true };
    }

    private bool IsHostUser(IPermissionActor user)
    {
        return user.IsHost || (_world.IsAuthority && ReferenceEquals(_world.LocalActor, user));
    }

    // Whether this write is a permitted move in the grab protocol. False does NOT mean refused: it means
    // the grab protocol has nothing to say, and the ordinary ownership/role rules decide. That fall-through
    // is what keeps host arbitration working - the host handing an object to someone else is not a grab it
    // is making, it is a decision its role entitles it to. `grabTraffic` reports whether the write was
    // grab-shaped at all, so a refusal that comes out of the role check downstream can be weighted as
    // contention rather than as an attack.
    //
    // WHO MAY WRITE WHAT:
    //   HolderRef - who is holding it. Free object: anyone may claim it. Held: only the holder (that is a
    //               release) unless the object opts into being stolen.
    //   Transform - the held object's parent and pose. The holder, or - and this is the case that needs
    //               the batch - the user whose SAME BATCH also claims the holder ref. The authority
    //               validates every record in a batch before it decodes any of them, so at the moment a
    //               pickup's reparent is judged, the holder ref still reads pre-grab; without the in-batch
    //               claim every legitimate pickup would be refused for not already holding the thing it is
    //               picking up. A claim does NOT buy the transform when the object is held and unstealable,
    //               or the holder-ref refusal would be worth nothing.
    //   GrabState - restore-parent and release velocities. Same rule as Transform: this is the hand-off,
    //               and it belongs to whoever is doing the handing.
    //   Placing   - once the hand has let go, the releasing user keeps Transform and GrabState while the
    //               thing it was let go into is still putting it down, and only while nobody holds it.
    //               The claim is written in the release batch, by the holder, so it can only ever name
    //               the user who just let go, and the next grab ends it.
    //
    // LOCAL WRITES are held to the weaker "held by nobody, or held by you" line. A client authors its own
    // grab optimistically and in pieces - the release clears the holder ref BEFORE it reparents, so at the
    // moment of the local reparent nobody holds the object - and the authority re-judges the replicated
    // records under the full rule anyway. Refusing locally would only break the honest client's own
    // animation while changing nothing a hostile one can do. -xlinka
    private static bool IsAllowedGrabWrite(in PermissionRequest request, IPermissionActor actor, IPermissionWorldFacts world, out bool grabTraffic)
    {
        grabTraffic = false;

        // Holding takes a hand, and hands hang under a body. An actor with none, a script among them,
        // gets no grab allowance and answers the ordinary ownership question like any other edit. -xlinka
        if (actor.RootElement == null)
            return false;

        var member = request.Member;
        if (member == null)
            return false;

        if (request.Parent is not IPermissionGrabSurface surface)
            return false;

        var kind = surface.ClassifyGrabWrite(member);
        if (kind == GrabWriteKind.None || !surface.AllowsGrab)
            return false;

        grabTraffic = true;
        var holder = surface.CurrentHolder;

        // Keep a current hold or placement valid when moving the item changes its wearer filter.
        // A new claimant must pass the object's own restrictions before it gets any grab rights. -xlinka
        if (!ReferenceEquals(holder, actor) && !ReferenceEquals(surface.Placer, actor) && !surface.AllowsGrabBy(actor))
            return false;

        if (kind == GrabWriteKind.HolderRef)
        {
            if (ReferenceEquals(holder, actor))
                return true;
            if (holder != null && !surface.AllowsSteal)
                return false;
            return !request.IsNetwork || world.BatchClaimsGrab(surface, actor);
        }

        if (ReferenceEquals(holder, actor))
            return true;

        if (holder == null && ReferenceEquals(surface.Placer, actor))
            return true;

        if (!request.IsNetwork)
            return holder == null;

        if (holder == null)
            return world.BatchClaimsGrab(surface, actor);

        return surface.AllowsSteal && world.BatchClaimsGrab(surface, actor);
    }

    // Whether the actor reads as owning the target only because it is holding it: the target hangs off a grab
    // the actor holds or rides along under one, and is not the actor's own by its id.
    private bool IsCarriedOnly(in PermissionRequest request, IPermissionActor actor, bool ownsRegistryKey)
        => !ownsRegistryKey
            && !OwnsIdStrong(actor, request.Target) && !OwnsIdStrong(actor, request.Parent)
            && (IsHeldByActor(in request, actor) || (request.Target ?? request.Parent)?.IsCarriedBy(actor) == true);

    // The one edit of a person's things the Social/Event floor hands on to the role: removing an item somebody
    // spawned. Destroy only, by a person by hand (no script, no world), of an element in a user's own byte that is
    // not the actor's and sits in nobody's body or hands, so never a worn avatar, a user root or the world. The
    // role still decides after this, and only a moderator's mask and up holds a foreign Destroy. -xlinka
    private const PermissionAction RemovalActions = PermissionAction.Destroy | PermissionAction.CollectionRemove;

    private bool IsItemRemoval(in PermissionRequest request, IPermissionActor actor, IPermissionWorldFacts world)
    {
        if (actor.RootElement == null || actor.IsWorldContent)
            return false;
        var action = request.Action & ~(PermissionAction.Write | PermissionAction.Replicate);
        if ((action & PermissionAction.Destroy) == 0 || (action & ~RemovalActions) != 0)
            return false;

        // A collection removal names what it takes out by key; a destroy names it as its target.
        IPermissionTarget? removed = request.Target;
        if ((action & PermissionAction.CollectionRemove) != 0)
        {
            if (!_ids.TryGetElementId(request.Key, out var keyId))
                return false;
            removed = world.FindTarget(keyId);
            // A destroy takes the slot out of the id table before the registry, so its own last step finds
            // nothing here; the removal was judged as that destroy began. Only this machine's own, never the wire's.
            if (removed == null)
                return !request.IsNetwork && !_ids.IsAuthority(keyId) && _ids.IsValidOwnerByte(_ids.OwnerByte(keyId))
                    && !OwnsId(actor, keyId);
        }
        if (removed == null || BelongsToAUser(removed))
            return false;

        for (int depth = 0; removed != null && depth < 8; depth++)
        {
            if (_ids.IsAuthority(removed.Id))
                return false;
            if (!_ids.IsNull(removed.Id) && _ids.IsValidOwnerByte(_ids.OwnerByte(removed.Id)))
                return !OwnsId(actor, removed.Id);
            removed = removed.OwnershipParent;
        }
        return false;
    }

    // The person whose user root a request reaches into, or null: up the target's ownership chain to the first slot
    // or component that sits under someone. A removal names what it takes out by key. rootLevel says what is reached
    // is the person's root slot itself, one of its members or one of its components. A script counts as its author
    // (same id), so a person's script still keeps house in its own author's avatar.
    private IPermissionActor? PersonReached(in PermissionRequest request, IPermissionWorldFacts world, out bool rootLevel,
        out IPermissionTarget? reached)
    {
        rootLevel = false;
        reached = request.Target ?? request.Parent;
        if ((request.Action & RemovalActions) != 0 && _ids.TryGetElementId(request.Key, out var keyId))
            reached = world.FindTarget(keyId) ?? reached;
        var node = reached;
        for (int depth = 0; node != null && depth < 8; depth++)
        {
            if (node.StructuralOwner is { } person)
            {
                var slot = node.PermissionKind == PermissionTargetKind.Slot ? node : node.OwnershipParent;
                rootLevel = slot != null && ReferenceEquals(slot, person.RootElement);
                return person;
            }
            node = node.OwnershipParent;
        }
        return null;
    }

    // What passes the person boundary on shared UI: a member that is a control's own interaction state or a
    // panel's own view, for whoever points at it, and the authority keeping a panel up inside an upkeep scope.
    // Both stay inside the UI they are about.
    private bool KeepsSharedUi(in PermissionRequest request, IPermissionTarget? reached)
        => request.Member is { IsUiState: true } || (!request.IsNetwork && InUpkeepOf(reached));

    // The person boundary for a slot being put somewhere: a new child made under `parent`, or `placed` moved there.
    // Only the boundary: whatever the move writes is judged on its own. Your own body may sit in a seat someone else
    // wears (that is the seat's business), and a world script keeps to the object it sits in. Same scopes and the
    // same actor as Authorize. -xlinka
    public bool AllowsPlacingUnder(IPermissionActor? actor, IPermissionTarget? placed, IPermissionTarget? parent, bool isNetwork)
    {
        if (!Enabled || parent == null || _world.IsDisposed || !_world.IsRunning || s_systemBypassDepth > 0)
            return true;
        if (isNetwork && !_world.IsAuthority)
            return true;
        if (!isNetwork && (placed is { IsLocalElement: true } || parent.IsLocalElement))
            return true;

        actor = isNetwork ? actor : actor ?? s_currentActor ?? _world.LocalActor;
        if (actor == null)
            return false;

        IPermissionActor? person = null;
        for (var node = parent; node != null && person == null; node = node.OwnershipParent)
            person = node.StructuralOwner;
        if (person == null || person.Id == actor.Id)
            return true;
        if (placed != null && ReferenceEquals(placed, actor.RootElement))
            return true;
        if (!isNetwork && InUpkeepOf(parent) && (placed == null || InUpkeepOf(placed)))
            return true;
        if (actor.IsWorldContent)
            return InOwnWornObject(actor, parent, PermissionAction.Create);
        return person.AllowsEditsBy(actor);
    }

    // Whether the thing being written to is, or hangs off, a grabbable this actor is holding right now.
    // Walks the ownership chain because a write to some other component on a held prop has that component
    // as its parent, not the slot the grabbable sits on.
    private static bool IsHeldByActor(in PermissionRequest request, IPermissionActor actor)
    {
        var node = request.Parent ?? request.Target;
        for (int depth = 0; node != null && depth < 4; depth++)
        {
            if (node is IPermissionGrabSurface surface
                && surface.AllowsGrab
                && ReferenceEquals(surface.CurrentHolder, actor))
            {
                return true;
            }
            node = node.OwnershipParent;
        }
        return false;
    }

    // Actions that hand a user real control over an object's existence - destroying it, pulling it out of a
    // collection, or clearing a collection. For a HELD foreign object these must require REAL ownership (the
    // creator's allocation byte), not the structural "it's parked under my hand right now" signal a grab flips.
    // Move / reparent / grab-state writes are NOT here - those are the grab interaction, allowed earlier.
    // Holding an object confers no edit/destroy authority; that comes only from real per-byte ownership. -xlinka
    private const PermissionAction DestructiveActions =
        PermissionAction.Destroy |
        PermissionAction.CollectionRemove |
        PermissionAction.CollectionClear;

    private const PermissionAction StructuralActions =
        PermissionAction.Create |
        PermissionAction.Destroy |
        PermissionAction.CollectionAdd |
        PermissionAction.CollectionInsert |
        PermissionAction.CollectionRemove |
        PermissionAction.CollectionClear |
        PermissionAction.CollectionResize;

    // What a role mask is asked. Replicate only says the request came over the wire, and no foreign mask
    // carries it, so every remote foreign write was refused even for a role granted it. A structural op
    // also carries Write because it changes the collection, which made the moderator's foreign remove and
    // destroy unreachable: its mask has Destroy and Remove but not Write. The op itself is the question.
    // CollectionSet keeps Write, it is an edit. -xlinka
    private static PermissionAction RoleQuestion(PermissionAction action)
    {
        var question = action & ~PermissionAction.Replicate;
        if ((question & StructuralActions) != 0)
            question &= ~PermissionAction.Write;
        return question == 0 ? action : question;
    }

    // STRONG ownership: only the real per-byte signal (and self), never the grab-flippable structural one. A
    // grab reparents the object under the grabber's root, which flips the structural owner to the grabber - so
    // that CANNOT gate destroying a foreign object, or a client could destroy a host prop just by holding it.
    // The id is minted in the creator's byte and a grab never changes it, so a user's own per-peer-spawned
    // equipment still passes here. -xlinka
    private bool OwnsIdStrong(IPermissionActor actor, IPermissionTarget? target)
    {
        if (target == null)
            return false;
        if (ReferenceEquals(actor, target))
            return true;
        if (OwnsId(actor, target.Id))
            return true;

        // Climb the same logical ownership chain OwnsTarget uses, but ONLY via the byte signal - never the
        // structural owner. A component's real owner is its slot's byte; a sync element's or worker's is its
        // parent's.
        return target.PermissionKind switch
        {
            PermissionTargetKind.Component or PermissionTargetKind.SyncElement or PermissionTargetKind.Worker
                => OwnsIdStrong(actor, target.OwnershipParent),
            _ => false
        };
    }

    private bool OwnsTarget(IPermissionActor actor, IPermissionTarget? target)
    {
        if (target == null)
        {
            return false;
        }

        if (ReferenceEquals(actor, target))
        {
            return true;
        }

        if (OwnsId(actor, target.Id))
        {
            return true;
        }

        // Slots and components fall back to the STRUCTURAL signals, and those are the reliable ones: a user's
        // allocation byte reads as 0 on their own client until the host-authored value syncs across, and the
        // cached root lags a beat behind the body being built - both of which otherwise (wrongly) deny a user
        // the right to drive their own head/hands/nameplate. The user -> root link is replicated and set on
        // both ends. -xlinka
        if (target.PermissionKind is PermissionTargetKind.Slot or PermissionTargetKind.Component)
        {
            return ReferenceEquals(target.StructuralOwner, actor) || target.IsUnderActorRoot(actor);
        }

        if (target.PermissionKind is PermissionTargetKind.SyncElement or PermissionTargetKind.Worker)
        {
            return OwnsTarget(actor, target.OwnershipParent);
        }

        return false;
    }

    private bool OwnsId(IPermissionActor actor, ulong id)
    {
        if (_ids.IsNull(id) || _ids.IsAuthority(id))
        {
            return false;
        }

        byte owner = _ids.OwnerByte(id);
        byte itemByte = actor.ItemAllocationByte;
        if (_ids.IsValidOwnerByte(itemByte) && owner == itemByte)
        {
            return true;
        }

        byte actorByte = actor.AllocationByte;
        if (!_ids.IsValidOwnerByte(actorByte))
        {
            actorByte = _ids.OwnerByte(actor.Id);
        }

        return _ids.IsValidOwnerByte(actorByte) && owner == actorByte;
    }
}
