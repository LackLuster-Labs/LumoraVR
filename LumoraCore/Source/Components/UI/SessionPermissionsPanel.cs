// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Helio.UI.Listing;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.UI;

// Every string this panel can put on screen. Gathered in one place so a locale sweep can walk it by
// reflection and prove nothing ships a key with no English behind it.
//
// The wording is deliberately not the engine's. A player does not know what a denial score or an access
// class is, and "host-authoritative" tells them nothing they can act on: the page says who can do what,
// and the enforcement story stays in the code where it belongs. -xlinka
public static class PermissionLocale
{
    public static readonly LocaleText NoWorld = "Session.Perm.NoWorld".AsLocale("No world open.");
    public static readonly LocaleText Syncing =
        "Session.Perm.Syncing".AsLocale("Still receiving the host's settings.");
    public static readonly LocaleText IntroGuest =
        "Session.Perm.Intro.Guest".AsLocale("Only the host changes these. You are seeing the host's settings.");

    public static readonly LocaleText People = "Session.Perm.People".AsLocale("People here");
    public static readonly LocaleText PeopleEmpty = "Session.Perm.People.Empty".AsLocale("Just you so far.");
    public static readonly LocaleText You = "Session.Perm.You".AsLocale("You");
    public static readonly LocaleText RoleHost = "Session.Perm.RoleHost".AsLocale("Host");

    public static readonly LocaleText Kick = "Session.Perm.Kick".AsLocale("Kick");
    public static readonly LocaleText TempBan = "Session.Perm.TempBan".AsLocale("Temp ban");
    public static readonly LocaleText KickTitle = "Session.Perm.Kick.Title".AsLocale("Kick this user?");
    public static readonly LocaleText KickConfirm = "Session.Perm.Kick.Confirm".AsLocale("Kick");
    public static readonly LocaleText TempBanTitle =
        "Session.Perm.TempBan.Title".AsLocale("Temporarily ban this user?");
    public static readonly LocaleText TempBanConfirm = "Session.Perm.TempBan.Confirm".AsLocale("Temp ban");

    public static readonly LocaleText RoleClasses = "Session.Perm.Roles".AsLocale("Who gets which role");
    public static readonly LocaleText RoleClassesNote = "Session.Perm.Roles.Note".AsLocale(
        "Friends count as signed in until friends lists exist. Group members only apply in a world hosted for a group.");
    public static readonly LocaleText ClassAnonymous = "Session.Perm.Roles.Anonymous".AsLocale("Not signed in");
    public static readonly LocaleText ClassVisitor = "Session.Perm.Roles.Visitor".AsLocale("Signed in");
    public static readonly LocaleText ClassContact = "Session.Perm.Roles.Contact".AsLocale("Friends");
    public static readonly LocaleText ClassGroup = "Session.Perm.Roles.Group".AsLocale("Group members");
    // The quiet marker beside a role pill: this person is on the group's roster, which is why they land
    // where they land. Not a badge, not a title, and deliberately not a word anyone could read as staff.
    public static readonly LocaleText GroupMark = "Session.Perm.GroupMark".AsLocale("group");
    public static readonly LocaleText ModeDefault = "Session.Perm.Policy.ModeDefault".AsLocale("By world mode");

    public static readonly LocaleText Caps = "Session.Perm.Caps".AsLocale("What each role can do");
    public static readonly LocaleText CapSpawn = "Session.Perm.Cap.Spawn".AsLocale("Spawn items");
    public static readonly LocaleText CapSaveCopy = "Session.Perm.Cap.SaveCopy".AsLocale("Save a copy");
    public static readonly LocaleText CapExport = "Session.Perm.Cap.Export".AsLocale("Export");
    public static readonly LocaleText CapToolUse = "Session.Perm.Cap.ToolUse".AsLocale("Use tools");
    public static readonly LocaleText CapTouch = "Session.Perm.Cap.Touch".AsLocale("Touch things");
    public static readonly LocaleText CapScript = "Session.Perm.Cap.Script".AsLocale("Run their scripts");

    // Read once, under the matrix header, by someone who has never seen a permission table.
    public static readonly LocaleText CapsLegend = "Session.Perm.Caps.Legend".AsLocale(
        "Green is allowed, red is not. A faded switch follows the world mode; flip it to set your own answer.");
    public static readonly LocaleText CapsReset =
        "Session.Perm.Caps.Reset".AsLocale("Reset to mode defaults");
    public static readonly LocaleText RoleGuide =
        "Session.Perm.Caps.RoleGuide".AsLocale("Co-host does anything · Creator builds and changes the world · Moderator manages people · Member brings and uses their own things · Viewer only looks");
    public static readonly LocaleText ToggleAllow = "Session.Perm.Toggle.Allow".AsLocale("Yes");
    public static readonly LocaleText ToggleDeny = "Session.Perm.Toggle.Deny".AsLocale("No");

    public static readonly LocaleText ForcedOffByMode = "Session.Perm.Cap.ForcedOff".AsLocale("by mode");

    public static readonly LocaleText MinScale = "Session.Perm.Scale.Min".AsLocale("Min scale");
    public static readonly LocaleText MaxScale = "Session.Perm.Scale.Max".AsLocale("Max scale");
    public static readonly LocaleText ScaleAny = "Session.Perm.Scale.Any".AsLocale("any");
    public static readonly LocaleText ScaleHint =
        "Session.Perm.Scale.Hint".AsLocale("Press the same cell again to close this.");

    public static readonly LocaleText Violations =
        "Session.Perm.Violations".AsLocale("When someone breaks the rules");
    public static readonly LocaleText Response = "Session.Perm.Policy.Response".AsLocale("Response");
    public static readonly LocaleText ResponseWarnOnly = "Session.Perm.Response.None".AsLocale("Warn only");
    public static readonly LocaleText ResponseKick = "Session.Perm.Response.Kick".AsLocale("Kick");
    public static readonly LocaleText ResponseTempBan = "Session.Perm.Response.TempBan".AsLocale("Temp ban");
    public static readonly LocaleText WarnScore = "Session.Perm.Policy.Warn".AsLocale("Warn after");
    public static readonly LocaleText KickScore = "Session.Perm.Policy.KickScore".AsLocale("Kick after");
    public static readonly LocaleText TempBanScore = "Session.Perm.Policy.TempBanScore".AsLocale("Temp ban after");
    public static readonly LocaleText HalfLife = "Session.Perm.Policy.HalfLife".AsLocale("Forget refusals after");
    public static readonly LocaleText PolicyDefault = "Session.Perm.Policy.Default".AsLocale("default");

    // Replaces the note above when the world is actually hosted for a group: the row stops being a
    // promise about a day when groups are wired and starts describing what happens at the door.
    public static LocaleText RoleClassesNoteGroup(string tag)
        => "Session.Perm.Roles.NoteGroup".AsLocale("Members of {0} on arrival", tag);

    public static LocaleText IntroHost(string worldName)
        => "Session.Perm.Intro.Host".AsLocale("Who can do what in {0}. Only the host changes these.", worldName);

    public static LocaleText Refusals(int count) => count == 1
        ? "Session.Perm.Refusals.One".AsLocale("1 refusal")
        : "Session.Perm.Refusals".AsLocale("{0} refusals", count);

    public static LocaleText MinScaleFor(string roleName)
        => "Session.Perm.Scale.MinFor".AsLocale("Min scale for {0}", roleName);

    public static LocaleText MaxScaleFor(string roleName)
        => "Session.Perm.Scale.MaxFor".AsLocale("Max scale for {0}", roleName);

    public static LocaleText Points(string value) => "Session.Perm.Points".AsLocale("{0} points", value);

    public static LocaleText Seconds(string value) => "Session.Perm.Seconds".AsLocale("{0}s", value);

    public static LocaleText KickMessage(string userName)
        => "Session.Perm.Kick.Message".AsLocale("\"{0}\" is disconnected from this session. They can rejoin.", userName);

    public static LocaleText TempBanMessage(string userName)
        => "Session.Perm.TempBan.Message".AsLocale(
            "\"{0}\" is disconnected and blocked from rejoining until this session ends.", userName);
}

// The Session screen's Permissions tab, over the Listing system: one virtualized page carrying the live
// roster, the per-role capability matrix and the escalation policy.
//
// Reads are the same for everybody - every member of WorldPermissionConfig is MarkHostOnly, so a guest
// RECEIVES all of it and can show correct live values. Writes are the host's alone, and the sync layer
// refuses a guest's delta whatever the UI drew. That still leaves a UI decision to make, and the answer
// here is that a guest gets read-only ROWS rather than greyed controls: a control that looks pressable
// and silently changes nothing is worse than no control at all. -xlinka
public sealed class SessionPermissionsPanel
{
    public const string KindNote = "permnote";
    public const string KindUser = "permuser";
    public const string KindPills = "permpills";
    public const string KindMatrix = "permmatrix";

    // A rebind is cheap (every template write is equality-gated) and this is what catches the changes no
    // event reaches us for: a per-entry Role field the host rewrote, a denial score decaying, a user's
    // name arriving late.
    private const float RefreshInterval = 0.25f;

    // Between the viewport's right edge and the scrollbar track.
    private const float ScrollbarGap = 6f;

    private static readonly DataModelPermissionDomain[] Domains =
    {
        DataModelPermissionDomain.Spawn,
        DataModelPermissionDomain.SaveCopy,
        DataModelPermissionDomain.Export,
        DataModelPermissionDomain.ToolUse,
        DataModelPermissionDomain.Touch,
        DataModelPermissionDomain.Script,
    };

    // The classes the host answers for, in the order the section reads them. Host is not here: nobody
    // configures what the owner of the world gets.
    private static readonly DataModelAccessClass[] JoinClasses =
    {
        DataModelAccessClass.Anonymous,
        DataModelAccessClass.Visitor,
        DataModelAccessClass.Contact,
        DataModelAccessClass.Group,
    };

    // Index order of the response pills. None is "warn and stop there": the ledger only reaches for a
    // kick when the response is not None, and the warn path fires either way.
    private static readonly DataModelViolationResponse[] Responses =
    {
        DataModelViolationResponse.None,
        DataModelViolationResponse.Kick,
        DataModelViolationResponse.TempBan,
    };

    private readonly ListingItemSource _items = new();
    private readonly SettingsFonts _fonts = new();
    private ListingStyle _style = new ListingStyle();
    private ListingView? _view;
    private RectTransform? _viewport;
    private readonly SettingsRowFit _fit = new();
    private DashScrollbar? _scrollbar;
    private SessionPermissionRelay? _relay;
    private Slot? _host;
    private World? _world;
    private Action<IChangeable>? _configChanged;
    private WorldPermissionConfig? _watched;
    private string _userSignature = string.Empty;
    // Which scale cell has its slider open under the matrix. Empty means none.
    private string _scaleRole = string.Empty;
    private bool _scaleMinimum;
    private float _accum;
    private bool _rebuildQueued;

    public bool IsDetached => _host == null || _host.IsDestroyed;
    public ListingView? View => _view;
    public IReadOnlyList<ListingItem> Items => _items.ItemsAt(string.Empty);
    public bool IsHost => _world != null && !_world.IsDestroyed && _world.IsAuthority;

    // Fills page with the scrolled list. page keeps whatever layout its parent gave it.
    //
    // No card of its own: the dash already hands this tab a panel to sit on, and a second fill in the
    // same tone inside it is a box drawn around nothing. The rows carry the surface. -xlinka
    public void Attach(Slot page, ListingStyle style)
    {
        _style = style;
        _host = page;

        var dashboard = Dashboard.Of(page);
        _fonts.Regular = dashboard?.Font.Target;
        _fonts.Semibold = dashboard?.FontSemibold.Target;
        _fonts.Bold = dashboard?.FontBold.Target;
        // Text with no font renders NOTHING, so the style has to come out of here holding one whichever
        // way the screen built it.
        _style.Font ??= _fonts.Body;

        var host = page.AddSlot("Permissions");
        host.AttachComponent<RectTransform>();
        var element = host.AttachComponent<LayoutElement>();
        element.FlexibleWidth.Value = 1f;
        element.FlexibleHeight.Value = 1f;

        _relay = host.AttachComponent<SessionPermissionRelay>();
        _relay.Panel = this;

        // The viewport keeps its right inset whether or not the bar is showing. A row set that slides
        // sideways every time the content crosses the fit threshold is worse than a strip of empty panel.
        var viewport = SettingsUI.Fill(host, "Viewport", SettingsMetrics.PanelInset, SettingsMetrics.PanelInset,
            SettingsMetrics.PanelInset + DashScrollbar.Width + ScrollbarGap, 0f);

        var labels = new SettingsLabelTemplate(_fonts) { Fit = _fit };
        var templates = new ListingTemplateMapper { DefaultHeight = SettingsMetrics.RowHeight };
        templates.Map<ListingHeader>(new SettingsSectionTemplate(_fonts));
        templates.Map<ListingSlider>(new SettingsSliderTemplate(_fonts) { Fit = _fit });
        templates.Map<ListingLabel>(labels);
        templates.Fallback(labels);
        templates.MapKind(KindNote, new PermNoteTemplate(_fonts) { Fit = _fit });
        templates.MapKind(KindUser, new PermUserRowTemplate(_fonts, this, _relay) { Fit = _fit });
        templates.MapKind(KindPills, new PermPillRowTemplate(_fonts, _relay) { Fit = _fit });
        templates.MapKind(KindMatrix, new PermMatrixRowTemplate(_fonts, this, _relay));
        _viewport = SettingsUI.Rect(viewport);
        _view = ListingView.Attach(viewport, _style, templates, _items);

        // Same bar the Settings tab drives, over the listing's own ScrollRect rather than a form's. The
        // wheel already reaches it: the ScrollRect is an interaction element covering the whole viewport
        // and the canvas walks a hovered row up to it, so a row under the pointer scrolls the list rather
        // than eating the axis. -xlinka
        _scrollbar = DashScrollbar.OnRightEdge(host, SettingsMetrics.PanelInset, 0f, SettingsMetrics.PanelInset);
        _scrollbar.Bind(_view.Scroll, SettingsUI.Rect(viewport),
            _view.ContentSlot == null ? null : SettingsUI.Rect(_view.ContentSlot), MarkDirty);
        host.World?.RunInUpdates(2, RefreshScrollbar);
    }

    // The listing pins its content height from its own update, so the bar is sized a frame behind whatever
    // just changed the item set. Called from the screen's tick and after every rebuild.
    public void RefreshScrollbar() => _scrollbar?.Refresh();

    public void Detach()
    {
        UnwatchConfig();
        _relay = null;
        _view = null;
        _scrollbar = null;
        _host = null;
        _world = null;
    }

    // Re-point at whatever world is focused now. Cheap when the world has not changed: the item set is
    // rebuilt, the slots are not.
    public void Bind(World? world)
    {
        if (IsDetached)
            return;
        if (!ReferenceEquals(world, _world))
        {
            UnwatchConfig();
            _world = world;
            _userSignature = string.Empty;
            _scaleRole = string.Empty;
        }
        WatchConfig();
        Rebuild();
    }

    public void Tick(float delta)
    {
        if (IsDetached || _view == null)
            return;

        // Every frame, not on the refresh interval: the listing pins its content height from its own
        // update, so a rebuild that added or dropped rows only shows up in the rect a frame later and a
        // quarter-second-late scrollbar reads as a stuck one. The call is two rect reads and equality-gated
        // writes. -xlinka
        RefreshScrollbar();
        // The pill rows take their shape from the list's width, so a new width measures every row again.
        if (_viewport != null && _fit.Update(_viewport.LocalComputeRect.width))
            _view.Refresh();

        if (_rebuildQueued)
        {
            _rebuildQueued = false;
            Rebuild();
            return;
        }

        _accum += delta;
        if (_accum < RefreshInterval)
            return;
        _accum = 0f;

        // The config can arrive long after this page opened - it is created on demand on the authority and
        // reaches a guest by state sync. Pick it up once rather than leaving the "still receiving" line and
        // a dead watch up for the rest of the session.
        if (_watched == null && _world?.PermissionConfig != null)
        {
            WatchConfig();
            Rebuild();
            return;
        }

        // A user joining or leaving changes the ROW SET, which no config event covers - the roster is not
        // part of the permission config. Signature-gated so the common case (nothing moved) costs one
        // string build and no structural work.
        if (BuildUserSignature() != _userSignature)
        {
            Rebuild();
            return;
        }
        _view.RefreshValues();
    }

    // A pill strip holds RESOLVED option strings (a ListingChoice takes a plain string list), so a
    // language switch has to rebuild the items rather than just rebind them. The scroll holds its place
    // either way.
    public void OnLocaleChanged()
    {
        _rebuildQueued = true;
        _view?.RefreshValues();
    }

    // CONFIG WATCH

    private void WatchConfig()
    {
        var config = _world?.PermissionConfig;
        if (config == null || config.IsDestroyed || ReferenceEquals(config, _watched))
            return;

        UnwatchConfig();
        _configChanged = _ => _view?.RefreshValues();
        for (int i = 0; i < config.SyncMemberCount; i++)
        {
            if (config.GetSyncMember(i) is IChangeable changeable)
                changeable.Changed += _configChanged;
        }
        config.Assignments.ElementsAdded += OnAssignmentsChanged;
        config.Assignments.ElementsRemoved += OnAssignmentsRemoved;
        _watched = config;
    }

    private void UnwatchConfig()
    {
        var config = _watched;
        _watched = null;
        if (config == null || config.IsDestroyed || _configChanged == null)
        {
            _configChanged = null;
            return;
        }
        for (int i = 0; i < config.SyncMemberCount; i++)
        {
            if (config.GetSyncMember(i) is IChangeable changeable)
                changeable.Changed -= _configChanged;
        }
        config.Assignments.ElementsAdded -= OnAssignmentsChanged;
        config.Assignments.ElementsRemoved -= OnAssignmentsRemoved;
        _configChanged = null;
    }

    private void OnAssignmentsChanged(Networking.Sync.SyncElementList<PermissionRoleAssignment> list, int index, int count)
        => _view?.RefreshValues();

    private void OnAssignmentsRemoved(Networking.Sync.SyncElementList<PermissionRoleAssignment> list, int index, int count)
        => _view?.RefreshValues();

    // BUILD

    private void Rebuild()
    {
        _items.ClearAll();
        _userSignature = BuildUserSignature();

        var world = _world;
        if (world == null || world.IsDestroyed)
        {
            AddNote("intro", PermissionLocale.NoWorld);
            return;
        }

        var permissions = world.DataModelPermissions;
        if (permissions == null)
        {
            AddNote("intro", PermissionLocale.Syncing);
            return;
        }

        bool host = world.IsAuthority;
        var roles = permissions.AssignableRolesFor(world.Mode);
        ValidateScaleEditor(roles, host);

        // One line, and only one. Everything else this page has to say, it says in the row it belongs to.
        AddNote("intro", host
            ? PermissionLocale.IntroHost(WorldLabel(world))
            : world.PermissionConfig == null ? PermissionLocale.Syncing : PermissionLocale.IntroGuest);

        BuildPeople(world, host);
        BuildRoleClasses(roles, host);
        BuildMatrix(host);
        BuildViolations(host);
    }

    private void AddNote(string key, LocaleText text)
        => _items.AddRoot(new ListingCustom(key, KindNote, text));

    private void BuildPeople(World world, bool host)
    {
        _items.AddRoot(new ListingHeader("h.people", PermissionLocale.People));

        int shown = 0;
        foreach (var user in world.GetAllUsers())
        {
            if (user == null || user.IsDestroyed)
                continue;
            // Keyed on the identity, not the name: a name arriving late must not read as a brand new row
            // to the reconcile.
            string key = "user." + (string.IsNullOrEmpty(user.UserID.Value)
                ? user.ReferenceID.ToString()
                : user.UserID.Value);
            _items.AddRoot(new ListingCustom(key, KindUser, LocaleText.Plain(DisplayName(user)))
            {
                Tag = user,
                Interactable = host,
            });
            shown++;
        }

        if (shown <= 1)
            AddNote("people.empty", PermissionLocale.PeopleEmpty);
    }

    // WHO GETS WHICH ROLE
    //
    // One "role on arrival" pill could not answer the question anybody actually has, which is not "what
    // does a stranger get" but "what does a stranger get, and what does someone I know get". So the row
    // became four, one per access class, and each one lights the role that class really lands on rather
    // than the word "default" - dimmed when that answer is the world mode's and lit when the host chose
    // it, the same rule the capability cells below read by. -xlinka
    private void BuildRoleClasses(IReadOnlyList<DataModelPermissionRole> roles, bool host)
    {
        _items.AddRoot(new ListingHeader("h.roles", PermissionLocale.RoleClasses));
        string hostGroupTag = HostGroupLabel();
        AddNote("roles.note", hostGroupTag.Length > 0
            ? PermissionLocale.RoleClassesNoteGroup(hostGroupTag)
            : PermissionLocale.RoleClassesNote);

        for (int c = 0; c < JoinClasses.Length; c++)
        {
            var accessClass = JoinClasses[c];
            string key = "role." + accessClass;

            if (!host)
            {
                _items.AddRoot(new ListingLabel(key, ClassLabel(accessClass),
                    () => RoleLabels.Of(ResolveClassRole(accessClass, out _))));
                continue;
            }

            var options = new List<string>(roles.Count + 1) { PermissionLocale.ModeDefault.Resolve() };
            var names = new List<string>(roles.Count);
            for (int i = 0; i < roles.Count; i++)
            {
                options.Add(RoleLabels.Of(roles[i]));
                names.Add(roles[i].Name);
            }

            _items.AddRoot(new PermChoiceItem(key, ClassLabel(accessClass))
            {
                Kind = KindPills,
                Options = options,
                // Nothing chosen means the lit role came from the mode, and the pill says so by going grey.
                Dimmed = () =>
                {
                    ResolveClassRole(accessClass, out bool chosen);
                    return !chosen;
                },
                Read = () =>
                {
                    int index = names.IndexOf(ClassRoleName(accessClass));
                    return index < 0 ? -1 : index + 1;
                },
                Write = index => WriteRoleClass(accessClass,
                    index <= 0 || index > names.Count ? string.Empty : names[index - 1]),
            });
        }
    }

    private static LocaleText ClassLabel(DataModelAccessClass accessClass) => accessClass switch
    {
        DataModelAccessClass.Anonymous => PermissionLocale.ClassAnonymous,
        DataModelAccessClass.Contact => PermissionLocale.ClassContact,
        DataModelAccessClass.Group => PermissionLocale.ClassGroup,
        _ => PermissionLocale.ClassVisitor,
    };

    private static Sync<string>? RoleClassField(WorldPermissionConfig config, DataModelAccessClass accessClass)
        => accessClass switch
        {
            DataModelAccessClass.Anonymous => config.AnonymousRole,
            DataModelAccessClass.Visitor => config.VisitorRole,
            DataModelAccessClass.Contact => config.ContactRole,
            DataModelAccessClass.Group => config.GroupRole,
            _ => null,
        };

    // What this class lands on, and whether the host is the one who said so.
    //
    // Resolved the same way ApplyConfig resolves it, off the same two facts: a named role the mode still
    // offers wins, anything else falls back to the mode's own seed. Deliberately NOT read back out of the
    // gate's default table - only the host runs the mode preset into its engine, so a guest asking its
    // local gate would report a Builder world's seeds while standing in a Social one. World.Mode is
    // replicated, so resolving from it is right on both sides of the wire. -xlinka
    private DataModelPermissionRole? ResolveClassRole(DataModelAccessClass accessClass, out bool chosen)
    {
        chosen = false;
        var world = _world;
        var permissions = world?.DataModelPermissions;
        if (world == null || world.IsDestroyed || permissions == null)
            return null;

        var config = world.PermissionConfig;
        var field = config == null || config.IsDestroyed ? null : RoleClassField(config, accessClass);
        var named = field == null || field.Value.Length == 0 ? null : permissions.FindRole(field.Value);
        if (named != null && IsAssignableRole(named))
        {
            chosen = true;
            return named;
        }
        return permissions.SeedRoleFor(world.Mode, accessClass);
    }

    private string ClassRoleName(DataModelAccessClass accessClass)
        => ResolveClassRole(accessClass, out _)?.Name ?? string.Empty;

    private bool IsAssignableRole(DataModelPermissionRole role)
    {
        var roles = AssignableRoles();
        for (int i = 0; i < roles.Count; i++)
        {
            if (ReferenceEquals(roles[i], role))
                return true;
        }
        return false;
    }

    private void WriteRoleClass(DataModelAccessClass accessClass, string roleName)
    {
        EditConfig(config =>
        {
            var field = RoleClassField(config, accessClass);
            if (field == null)
                return;
            if (field.Value != roleName)
                field.Value = roleName;

            // The retired single default outranks every per-class answer at the gate, so the first time the
            // host touches one of these rows it goes. Otherwise a host who set "everyone is a Spectator" last
            // year would edit these four rows and watch nothing change. -xlinka
            if (config.DefaultJoinerRole.Value.Length > 0)
                config.DefaultJoinerRole.Value = string.Empty;
        });
    }

    // Five roles times five capabilities plus two bounds each was thirty-five rows to scroll past. The
    // same data is one row per capability with one cell per role, which is also how a person thinks about
    // it: they want to know who can spawn, not what Builder can do. -xlinka
    private void BuildMatrix(bool host)
    {
        _items.AddRoot(new ListingHeader("h.caps", PermissionLocale.Caps));
        AddNote("caps.roles", PermissionLocale.RoleGuide);
        AddNote("caps.legend", PermissionLocale.CapsLegend);
        _items.AddRoot(new PermMatrixItem("caps.head", PermMatrixKind.Header, LocaleText.Empty)
        {
            Interactable = false,
            ShowReset = host,
        });

        for (int i = 0; i < Domains.Length; i++)
        {
            var domain = Domains[i];
            _items.AddRoot(new PermMatrixItem("caps." + domain, PermMatrixKind.Capability, DomainLabel(domain))
            {
                Domain = domain,
                Interactable = host,
            });
        }

        _items.AddRoot(new PermMatrixItem("scale.min", PermMatrixKind.MinScale, PermissionLocale.MinScale)
        {
            Interactable = host,
        });
        AddScaleEditor(host, minimum: true);

        _items.AddRoot(new PermMatrixItem("scale.max", PermMatrixKind.MaxScale, PermissionLocale.MaxScale)
        {
            Interactable = host,
        });
        AddScaleEditor(host, minimum: false);
    }

    // Pressing a scale cell opens the real slider directly under its matrix row.
    //
    // The Listing system has no row kind that can hold five live sliders side by side, and one slider per
    // role per bound is exactly the row wall this page just lost, so the editor is an item the rebuild
    // inserts and removes. The write path underneath is the one the old per-role rows used. -xlinka
    private void AddScaleEditor(bool host, bool minimum)
    {
        if (!host || _scaleRole.Length == 0 || _scaleMinimum != minimum)
            return;

        string roleName = _scaleRole;
        _items.AddRoot(new ListingSlider("scale.edit", minimum
            ? PermissionLocale.MinScaleFor(RoleLabels.Of(roleName))
            : PermissionLocale.MaxScaleFor(RoleLabels.Of(roleName)))
        {
            Min = 0f,
            Max = minimum ? 5f : 20f,
            Step = minimum ? 0.05f : 0.25f,
            DetailText = PermissionLocale.ScaleHint,
            Read = () => ReadScale(roleName, minimum),
            Write = value => EditConfig(config =>
            {
                var entry = config.GetOrAddCap(roleName);
                if (minimum)
                    entry.MinScale.Value = value;
                else
                    entry.MaxScale.Value = value;
            }),
            Format = DescribeScale,
        });
    }

    private void BuildViolations(bool host)
    {
        _items.AddRoot(new ListingHeader("h.violations", PermissionLocale.Violations));

        if (!host)
        {
            _items.AddRoot(new ListingLabel("policy.response", PermissionLocale.Response,
                () => ResponseLabel(_world?.PermissionConfig?.ViolationResponse.Value ?? DataModelViolationResponse.Kick)));
            AddScoreLabel("policy.warn", PermissionLocale.WarnScore, config => config.WarnScore);
            AddScoreLabel("policy.kick", PermissionLocale.KickScore, config => config.KickScore);
            AddScoreLabel("policy.tempban", PermissionLocale.TempBanScore, config => config.TempBanScore);
            AddScoreLabel("policy.halflife", PermissionLocale.HalfLife,
                config => config.DenialHalfLifeSeconds, seconds: true);
            return;
        }

        _items.AddRoot(new ListingChoice("policy.response", PermissionLocale.Response)
        {
            Kind = KindPills,
            Options = ResponseOptions(),
            Read = () =>
            {
                int index = Array.IndexOf(Responses,
                    _world?.PermissionConfig?.ViolationResponse.Value ?? DataModelViolationResponse.Kick);
                return index < 0 ? 0 : index;
            },
            Write = index =>
            {
                if (index >= 0 && index < Responses.Length)
                    EditConfig(config => config.ViolationResponse.Value = Responses[index]);
            },
        });

        AddScoreSlider("policy.warn", PermissionLocale.WarnScore, 0f, 300f, 5f,
            config => config.WarnScore);
        AddScoreSlider("policy.kick", PermissionLocale.KickScore, 0f, 600f, 5f,
            config => config.KickScore);
        AddScoreSlider("policy.tempban", PermissionLocale.TempBanScore, 0f, 1000f, 10f,
            config => config.TempBanScore);
        AddScoreSlider("policy.halflife", PermissionLocale.HalfLife, 0f, 600f, 5f,
            config => config.DenialHalfLifeSeconds, seconds: true);
    }

    private void AddScoreSlider(string key, LocaleText label, float min, float max, float step,
        Func<WorldPermissionConfig, Sync<float>> field, bool seconds = false)
    {
        _items.AddRoot(new ListingSlider(key, label)
        {
            Min = min,
            Max = max,
            Step = step,
            Read = () =>
            {
                var config = _world?.PermissionConfig;
                return config == null ? 0f : field(config).Value;
            },
            Write = value => EditConfig(config => field(config).Value = value),
            Format = value => DescribeScore(value, seconds),
        });
    }

    private void AddScoreLabel(string key, LocaleText label,
        Func<WorldPermissionConfig, Sync<float>> field, bool seconds = false)
    {
        _items.AddRoot(new ListingLabel(key, label, () =>
        {
            var config = _world?.PermissionConfig;
            return DescribeScore(config == null ? 0f : field(config).Value, seconds);
        }));
    }

    // HOST ACTIONS

    internal void AssignRole(User? user, int roleIndex)
    {
        var world = _world;
        if (user == null || world == null || world.IsDestroyed || !world.IsAuthority)
            return;
        var roles = world.DataModelPermissions?.AssignableRolesFor(world.Mode);
        if (roles == null || roleIndex < 0 || roleIndex >= roles.Count)
            return;

        if (GetWritableConfig() == null)
            return;
        // The config is the durable half (it survives the user leaving and it saves with the world); the
        // gate's own session override is what takes effect this instant. Both, so a role change holds
        // whether the user reconnects or not.
        var role = roles[roleIndex];
        EditConfig(config => config.SetRole(user, role.Name));
        world.DataModelPermissions?.SetUserRole(user, role);
        _view?.RefreshValues();
        MarkDirty();
    }

    // A switch press writes Grant or Deny. The tri-state is still what the config stores and what the gate
    // resolves; there is just no press that lands on Inherit, because "cycle until it says the thing you
    // want" was never a control anybody could read. Getting back to the mode's own answer is the header's
    // reset, which is a deliberate act rather than a third click. -xlinka
    internal void SetCap(string roleName, DataModelPermissionDomain domain, DataModelPermissionToggle value)
    {
        if (CapState(roleName, domain) == value)
            return;
        EditConfig(config => ToggleField(config.GetOrAddCap(roleName), domain).Value = value);
        _view?.RefreshValues();
        MarkDirty();
    }

    // Hands every capability of every role back to the world mode. Walks the config's own entries rather
    // than the roles the current mode offers, so a toggle left behind by a role this mode does not show is
    // cleared too instead of quietly outliving the reset. Scale bounds are not capabilities and are left
    // exactly where the host put them.
    internal void ResetCapsToMode()
    {
        var config = GetWritableConfig();
        if (config == null)
            return;

        bool changed = false;
        EditConfig(_ =>
        {
            foreach (var entry in config.RoleCaps)
            {
                if (entry == null || entry.IsDestroyed)
                    continue;
                for (int i = 0; i < Domains.Length; i++)
                {
                    var field = ToggleField(entry, Domains[i]);
                    if (field.Value == DataModelPermissionToggle.Inherit)
                        continue;
                    field.Value = DataModelPermissionToggle.Inherit;
                    changed = true;
                }
            }
        });

        if (!changed)
            return;
        _view?.RefreshValues();
        MarkDirty();
    }

    internal void ToggleScaleEditor(string roleName, bool minimum)
    {
        if (!IsHost)
            return;
        if (_scaleRole == roleName && _scaleMinimum == minimum)
        {
            _scaleRole = string.Empty;
        }
        else
        {
            _scaleRole = roleName;
            _scaleMinimum = minimum;
        }
        QueueRebuild();
    }

    internal void RequestKick(User? user)
    {
        var world = _world;
        if (user == null || world == null || !world.IsAuthority || IsHostUser(user))
            return;
        string name = DisplayName(user);
        ModalHost.Confirm(_host, PermissionLocale.KickTitle, PermissionLocale.KickMessage(name),
            PermissionLocale.KickConfirm, () =>
            {
                if (user.IsDestroyed)
                    return;
                user.Kick();
                QueueRebuild();
            });
    }

    internal void RequestTempBan(User? user)
    {
        var world = _world;
        if (user == null || world == null || !world.IsAuthority || IsHostUser(user))
            return;
        string name = DisplayName(user);
        ModalHost.Confirm(_host, PermissionLocale.TempBanTitle, PermissionLocale.TempBanMessage(name),
            PermissionLocale.TempBanConfirm, () =>
            {
                if (user.IsDestroyed)
                    return;
                // TEMP, not durable: a bad session is not evidence of a person who should never come back,
                // and User.Ban() writes a permanent record. Same pair the gate's own escalation uses.
                Security.BanManager.TempBan(user.AccountId.Value, user.MachineID.Value);
                user.Kick();
                QueueRebuild();
            });
    }

    private void QueueRebuild() => _rebuildQueued = true;

    // READS

    public DataModelPermissionRole? RoleOf(User? user)
        => user == null ? null : _world?.DataModelPermissions?.GetRole(user);

    // On the group's roster, as the HOST fetched it. Always false on a guest, which is correct rather
    // than a gap: a guest holds no roster and is not entitled to one, so it cannot honestly say who is a
    // member. The role pills this marker sits beside are host-only anyway. -xlinka
    public bool IsGroupActor(User? user)
    {
        var permissions = _world?.DataModelPermissions;
        if (permissions == null || user == null || user.IsDestroyed)
            return false;
        return permissions.IsGroupMember(user.AccountId.Value);
    }

    // The short tag of the group this world is hosted for, or empty when it is not hosted for one.
    // Read through the root slot's settings rather than World.Configuration, which attaches on the
    // authority and would make a guest create a second one.
    public string HostGroupLabel()
    {
        var world = _world;
        if (world == null || world.IsDestroyed)
            return string.Empty;
        return GroupSessionTags.WorldShortTag(world);
    }

    public bool IsHostUser(User? user)
    {
        var permissions = _world?.DataModelPermissions;
        return permissions != null && user != null && ReferenceEquals(permissions.GetRole(user), permissions.HostRole);
    }

    public IReadOnlyList<DataModelPermissionRole> AssignableRoles()
    {
        var world = _world;
        if (world == null || world.IsDestroyed || world.DataModelPermissions == null)
            return Array.Empty<DataModelPermissionRole>();
        return world.DataModelPermissions.AssignableRolesFor(world.Mode);
    }

    public double DenialScore(User? user) => user == null ? 0.0 : _world?.DataModelPermissions?.DenialScore(user) ?? 0.0;

    // Warn / kick thresholds as the gate will actually read them: anything not positive falls back to the
    // shipped default inside ApplyConfig, so a zero here must not read as "kick on the first refusal".
    public void DenialThresholds(out float warn, out float kick)
    {
        var config = _world?.PermissionConfig;
        warn = config != null && config.WarnScore.Value > 0f ? config.WarnScore.Value : 10f;
        kick = config != null && config.KickScore.Value > 0f ? config.KickScore.Value : 50f;
    }

    public static string DisplayName(User user)
    {
        string? name = user.UserName?.Value;
        return string.IsNullOrEmpty(name) ? "User " + user.ReferenceID : name!;
    }

    private static string WorldLabel(World world) => Management.WorldManager.DisplayName(world);

    private string BuildUserSignature()
    {
        var world = _world;
        if (world == null || world.IsDestroyed)
            return string.Empty;
        var users = world.GetAllUsers();
        var builder = new System.Text.StringBuilder(users.Count * 12);
        for (int i = 0; i < users.Count; i++)
        {
            var user = users[i];
            if (user == null || user.IsDestroyed)
                continue;
            builder.Append(user.ReferenceID).Append(':');
        }
        return builder.ToString();
    }

    // CAPS

    private PermissionRoleCapEntry? FindCap(string roleName)
    {
        var config = _world?.PermissionConfig;
        if (config == null || config.IsDestroyed)
            return null;
        // Read-only lookup on purpose: GetOrAddCap would WRITE, and a guest merely rendering this page must
        // never author a list entry the authority would refuse anyway.
        foreach (var entry in config.RoleCaps)
        {
            if (entry != null && !entry.IsDestroyed && entry.Role.Value == roleName)
                return entry;
        }
        return null;
    }

    private WorldPermissionConfig? GetWritableConfig()
    {
        var world = _world;
        if (world == null || world.IsDestroyed || !world.IsAuthority)
            return null;
        var config = world.PermissionConfig;
        return config == null || config.IsDestroyed ? null : config;
    }

    // THE SANCTIONED WRITER for the permission half of the config.
    //
    // Every member of WorldPermissionConfig is config-locked, so a write that does not open the world's
    // edit scope is refused at the datamodel - including one made from a scene inspector row by the host.
    // This panel is host-gated already (GetWritableConfig), so this only adds the scope, in one place
    // rather than at each of the places that write a field or add an entry. The scope is also what gets
    // the write past a Social or Event world's freeze (see World.EnterConfigEdit). -xlinka
    private void EditConfig(Action<WorldPermissionConfig> apply)
    {
        var config = GetWritableConfig();
        if (config == null)
            return;
        using var configEdit = config.World!.EnterConfigEdit();
        apply(config);
    }

    public DataModelPermissionToggle CapState(string roleName, DataModelPermissionDomain domain)
    {
        var entry = FindCap(roleName);
        return entry == null ? DataModelPermissionToggle.Inherit : ToggleField(entry, domain).Value;
    }

    // What the gate WILL answer for this role and domain, with nobody standing in the role.
    //
    // This used to hunt for a user already in the role and ask about them, because there was no per-role
    // query to ask - so an empty role got a blank cell and the host was told nothing about the thing they
    // were configuring. AllowsDomainFor asks the role itself: the compiled cap, the host's toggle, then
    // the mode ceiling last and binding. Every role has an answer now whether anyone is in it or not,
    // which is the whole reason a lit pill can be the truth rather than a restatement of the setting.
    // -xlinka
    public bool CapAllows(DataModelPermissionRole role, DataModelPermissionDomain domain)
    {
        var world = _world;
        var permissions = world?.DataModelPermissions;
        if (world == null || permissions == null)
            return false;

        // The mode ceiling lands twice here and that is on purpose. AllowsDomainFor applies the gate's OWN
        // mode, which is the session's on the authority and stale on a guest - only the host runs the mode
        // preset into its engine. World.Mode is replicated, so laying the same policy ceiling over the top
        // is what stops a guest reading Yes on a capability an Event world already took away. It can only
        // narrow the answer, never widen it, so the host's cell is unchanged. -xlinka
        return permissions.AllowsDomainFor(role, domain)
            && Lumora.Warden.WorldModePolicy.ModeAllows(world.Mode, domain);
    }

    public string ScaleText(string roleName, bool minimum) => DescribeScale(ReadScale(roleName, minimum));

    public bool IsScaleOpen(string roleName, bool minimum)
        => _scaleRole == roleName && _scaleMinimum == minimum;

    // Reading must never CREATE the cap entry: GetOrAddCap writes, and a guest merely rendering a cell
    // would then author a list element the authority refuses anyway. Unset reads as zero, which is exactly
    // what the gate treats as an inactive bound.
    private float ReadScale(string roleName, bool minimum)
    {
        var entry = FindCap(roleName);
        if (entry == null)
            return 0f;
        return minimum ? entry.MinScale.Value : entry.MaxScale.Value;
    }

    // A mode switch can drop the role whose slider is open, and a guest never gets one at all.
    private void ValidateScaleEditor(IReadOnlyList<DataModelPermissionRole> roles, bool host)
    {
        if (_scaleRole.Length == 0)
            return;
        if (!host)
        {
            _scaleRole = string.Empty;
            return;
        }
        for (int i = 0; i < roles.Count; i++)
        {
            if (roles[i].Name == _scaleRole)
                return;
        }
        _scaleRole = string.Empty;
    }

    private static Sync<DataModelPermissionToggle> ToggleField(PermissionRoleCapEntry entry, DataModelPermissionDomain domain)
        => domain switch
        {
            DataModelPermissionDomain.Spawn => entry.Spawn,
            DataModelPermissionDomain.SaveCopy => entry.SaveCopy,
            DataModelPermissionDomain.Export => entry.Export,
            DataModelPermissionDomain.ToolUse => entry.ToolUse,
            DataModelPermissionDomain.Script => entry.Script,
            _ => entry.Touch,
        };

    public bool ModeForcesOff(DataModelPermissionDomain domain)
    {
        var world = _world;
        var permissions = world?.DataModelPermissions;
        if (world == null || permissions == null)
            return false;

        // The replicated mode's ceiling first. It is a pure function of a value every peer holds, so it is
        // the half of this answer a guest can also be right about - asking only the local gate had the
        // guest's page telling them a frozen world still allowed tools.
        if (!Lumora.Warden.WorldModePolicy.ModeAllows(world.Mode, domain))
            return true;

        // Then the gate itself, through the host. The host role's compiled cap grants every domain, so a
        // host that still comes back denied can only have been stopped by a ceiling - and a hand-set
        // SocialLock in a Builder world is the one ceiling the replicated mode cannot see.
        var hostUser = FirstHostUser();
        return hostUser != null && !permissions.AllowsDomain(hostUser, domain);
    }

    private User? FirstHostUser()
    {
        var world = _world;
        var permissions = world?.DataModelPermissions;
        if (world == null || permissions == null)
            return null;
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && !user.IsDestroyed && ReferenceEquals(permissions.GetRole(user), permissions.HostRole))
                return user;
        }
        return null;
    }

    // FORMATTING

    private static LocaleText DomainLabel(DataModelPermissionDomain domain) => domain switch
    {
        DataModelPermissionDomain.Spawn => PermissionLocale.CapSpawn,
        DataModelPermissionDomain.SaveCopy => PermissionLocale.CapSaveCopy,
        DataModelPermissionDomain.Export => PermissionLocale.CapExport,
        DataModelPermissionDomain.ToolUse => PermissionLocale.CapToolUse,
        DataModelPermissionDomain.Script => PermissionLocale.CapScript,
        _ => PermissionLocale.CapTouch,
    };

    private static string[] ResponseOptions() => new[]
    {
        PermissionLocale.ResponseWarnOnly.Resolve(),
        PermissionLocale.ResponseKick.Resolve(),
        PermissionLocale.ResponseTempBan.Resolve(),
    };

    private static string ResponseLabel(DataModelViolationResponse value) => value switch
    {
        DataModelViolationResponse.Kick => PermissionLocale.ResponseKick.Resolve(),
        DataModelViolationResponse.TempBan => PermissionLocale.ResponseTempBan.Resolve(),
        _ => PermissionLocale.ResponseWarnOnly.Resolve(),
    };

    private static string DescribeScale(float value)
        => value <= 0f ? PermissionLocale.ScaleAny.Resolve() : value.ToString("0.##") + "x";

    // Zero is not "never": ApplyConfig treats anything not positive as "use the shipped default", so that
    // is what the row says.
    private static string DescribeScore(float value, bool seconds)
    {
        if (value <= 0f)
            return PermissionLocale.PolicyDefault.Resolve();
        return seconds
            ? PermissionLocale.Seconds(value.ToString("0")).Resolve()
            : PermissionLocale.Points(value.ToString("0")).Resolve();
    }

    private void MarkDirty() => _host?.GetComponentInParents<Canvas>()?.MarkDirty();
}
