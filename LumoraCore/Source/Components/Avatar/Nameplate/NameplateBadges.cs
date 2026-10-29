// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// One chip on a nameplate's badge row. A value type on purpose: the manager collects a fresh list every
// reconcile and compares it against what it drew, so a badge has to be cheap to build and cheap to
// compare.
//
// A badge carries EITHER a glyph or an icon, never both. A glyph is drawn with the plate font, so it has
// to be text the shipping face actually has - a letter, not a private-use codepoint. An icon names a file
// under Assets/Textures/Icons and the world's theme keeps one material per icon kind, so a room full of
// the same chip costs one texture, not one per plate. -xlinka
public readonly struct NameplateBadge : IEquatable<NameplateBadge>
{
    // Stable id. Doubles as the chip's slot name and as the dedup key, so two sources cannot fight over
    // the same chip.
    public readonly string Key;

    public readonly string Glyph;

    // File name under Assets/Textures/Icons, without the extension. Empty on a letter badge.
    public readonly string IconName;

    public readonly color Chip;

    public readonly color Ink;

    // Lower sorts further left. Ties break on Key so the row order is stable frame to frame.
    public readonly int Order;

    public NameplateBadge(string key, string glyph, color chip, color ink, int order)
        : this(key, glyph, string.Empty, chip, ink, order)
    {
    }

    public NameplateBadge(string key, string glyph, string? iconName, color chip, color ink, int order)
    {
        Key = key ?? string.Empty;
        Glyph = glyph ?? string.Empty;
        IconName = iconName ?? string.Empty;
        Chip = chip;
        Ink = ink;
        Order = order;
    }

    // A letter chip. The meaning rides the FILL, not the letter: one dark square with a coloured letter on
    // it beside a name reads as a dead pixel, and the same square filled with the colour reads at ten
    // metres. The letter is cut out of it in dark ink. -xlinka
    public static NameplateBadge Letter(string key, string glyph, color chip, int order)
        => new NameplateBadge(key, glyph, string.Empty, chip, NameplateBadgeColors.Ink, order);

    // An icon chip: the state colour fills the chip and the artwork is tinted to the same dark ink, so the
    // glyph reads as a hole in the colour rather than a coloured drawing on a dark square.
    public static NameplateBadge Icon(string key, string iconName, color chip, int order)
        => new NameplateBadge(key, string.Empty, iconName, chip, NameplateBadgeColors.Ink, order);

    public bool Equals(NameplateBadge other)
        => Key == other.Key && Glyph == other.Glyph && IconName == other.IconName
           && Chip == other.Chip && Ink == other.Ink && Order == other.Order;

    public override bool Equals(object? obj) => obj is NameplateBadge other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Glyph, IconName, Order);
}

// What a badge source gets to look at. Everything here is already replicated state or a local read of
// the permission gate - a source must not write anything.
public readonly struct NameplateBadgeContext
{
    public readonly World World;
    public readonly User User;
    public readonly UserRoot Root;

    public NameplateBadgeContext(World world, User user, UserRoot root)
    {
        World = world;
        User = user;
        Root = root;
    }
}

public interface INameplateBadgeSource
{
    void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output);
}

// The extension seam. Registration is process-wide, not per-world: a source is a rule about what a badge
// MEANS, and that answer cannot differ between the session world and the dashboard overlay.
//
// Collect runs on the world update thread, once every reconcile per visible user, so it must not
// allocate beyond what the caller's list already holds. Registration can come from anywhere (a module
// loading late), hence the lock on the source list and the snapshot copy. -xlinka
public static class NameplateBadgeRegistry
{
    private static readonly object Gate = new();
    private static readonly List<INameplateBadgeSource> Sources = new();
    private static INameplateBadgeSource[] _snapshot = Array.Empty<INameplateBadgeSource>();

    static NameplateBadgeRegistry()
    {
        Register(new HostBadgeSource());
        Register(new RoleBadgeSource());
        Register(new VoiceBadgeSource());
        Register(new LiveBadgeSource());
        Register(new SupporterBadgeSource());
        Register(new StaffBadgeSource());
        Register(new BotBadgeSource());
    }

    public static void Register(INameplateBadgeSource source)
    {
        if (source == null)
            return;
        lock (Gate)
        {
            if (Sources.Contains(source))
                return;
            Sources.Add(source);
            _snapshot = Sources.ToArray();
        }
    }

    public static bool Unregister(INameplateBadgeSource source)
    {
        if (source == null)
            return false;
        lock (Gate)
        {
            if (!Sources.Remove(source))
                return false;
            _snapshot = Sources.ToArray();
            return true;
        }
    }

    public static int SourceCount
    {
        get { lock (Gate) return Sources.Count; }
    }

    public static void Collect(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        if (output == null)
            return;
        output.Clear();
        if (context.World == null || context.World.IsDestroyed || context.User == null || context.User.IsDestroyed)
            return;

        var sources = _snapshot;
        for (int i = 0; i < sources.Length; i++)
        {
            try
            {
                sources[i].CollectBadges(in context, output);
            }
            catch (Exception ex)
            {
                // A badge source throwing must not take the plate (or the frame) down with it. Drop that
                // source's chips for this pass and carry on with the rest.
                Logging.Logger.Warn($"NameplateBadgeRegistry: {sources[i].GetType().Name} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        DedupAndSort(output);
    }

    private static void DedupAndSort(List<NameplateBadge> badges)
    {
        for (int i = badges.Count - 1; i > 0; i--)
        {
            for (int j = 0; j < i; j++)
            {
                if (badges[i].Key == badges[j].Key)
                {
                    badges.RemoveAt(i);
                    break;
                }
            }
        }

        badges.Sort(static (a, b) =>
        {
            int order = a.Order.CompareTo(b.Order);
            return order != 0 ? order : string.CompareOrdinal(a.Key, b.Key);
        });
    }
}

// SHIPPING SOURCES
// Four, and only the four that have a real signal behind them. A badge that cannot be derived from
// replicated state or from the gate is a badge that lies on somebody's plate.

// The ink a chip is read by. Straight off the dash tokens, so a role over somebody's head is the colour
// that role already has on the session screen, and every one of these is stored linear like the rest of
// the palette: a chip mesh takes it as a vertex colour with no conversion in between. -xlinka
public static class NameplateBadgeColors
{
    // What a mark on a filled chip is cut out in: the plate's own tint taken to full alpha. It sits ON the
    // colour rather than on the plate behind it, so it cannot be the translucent fill or the chip shows
    // through the letter. One ink for every chip on purpose - the chip says which badge it is, the mark
    // only has to be legible on it. -xlinka
    public static readonly color Ink =
        new color(DashTheme.Panel.r, DashTheme.Panel.g, DashTheme.Panel.b, 1f);

    public static readonly color Host = DashTheme.Warning;
    public static readonly color Staff = NameplateTheme.StaffRim;
    public static readonly color Admin = DashTheme.Negative;
    public static readonly color Moderator = DashTheme.IconBlue;
    public static readonly color Builder = DashTheme.Positive;
    public static readonly color Member = DashTheme.TextDim;
    public static readonly color Viewer = DashTheme.TextMuted;

    public static readonly color Muted = DashTheme.Negative;
    public static readonly color Whisper = DashTheme.TextDim;
    public static readonly color Shout = DashTheme.Warning;
    public static readonly color Broadcast = DashTheme.ModeEvent;

    // The camera's tally red, decoded like the tokens above, so the plate and the camera body say live in
    // the same colour.
    public static readonly color Live = new color(1f, 0.08f, 0.06f, 1f).ToLinear();

    // An account that is not a person: a cool teal no role, voice or tier chip wears. The People window
    // marks bots in the same colour.
    public static readonly color Bot = new color(0.30f, 0.80f, 0.86f, 1f).ToLinear();

    // Keyed on the gate's role identifiers, not the words on screen (see RoleLabels).
    public static color ForRole(string? roleName) => roleName switch
    {
        "Host" => Host,
        "Admin" => Admin,
        "Moderator" => Moderator,
        "Builder" => Builder,
        "Spectator" => Viewer,
        _ => Member,
    };
}

internal sealed class HostBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var permissions = context.World?.DataModelPermissions;
        if (permissions == null || context.User == null)
            return;
        if (!ReferenceEquals(permissions.GetRole(context.User), permissions.HostRole))
            return;

        output.Add(NameplateBadge.Letter("host", "H", NameplateBadgeColors.Host, 0));
    }
}

// Only badges a role that is NOT what this world hands a joiner. Everybody being a Builder in a builder
// world is not information, and a row of identical chips over every head is just noise. -xlinka
internal sealed class RoleBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var world = context.World;
        var permissions = world?.DataModelPermissions;
        if (permissions == null || context.User == null)
            return;

        var role = permissions.GetRole(context.User);
        if (role == null || ReferenceEquals(role, permissions.HostRole))
            return; // the host chip already says it

        var fallback = ResolveWorldDefault(world!, permissions);
        if (fallback != null && ReferenceEquals(role, fallback))
            return;

        output.Add(NameplateBadge.Letter("role", RoleLabels.Badge(role.Name),
            NameplateBadgeColors.ForRole(role.Name), 1));
    }

    // The role this world lands an UNASSIGNED joiner on: the host's configured joiner default when it
    // named one, otherwise the Visitor default the world mode baked in.
    //
    // Deliberately does not ask for THIS user's access class. An assigned user is classified by the tier
    // their own role belongs to, so GetDefaultRole(GetAccessClass(user)) walks straight back to roughly
    // the role they were assigned and every badge silently disappears. The comparison has to be against
    // a user-independent answer, and an unassigned non-host user is always a Visitor. -xlinka
    //
    // Read through RootSlot.GetComponent, NOT World.PermissionConfig - that property ATTACHES the config
    // on the authority, and a nameplate has no business authoring world components as a side effect of
    // drawing itself.
    private static DataModelPermissionRole? ResolveWorldDefault(World world,
        DataModelPermissionController permissions)
    {
        var config = world.RootSlot?.GetComponent<WorldPermissionConfig>();
        var named = permissions.FindRole(config?.DefaultJoinerRole.Value);
        if (named != null)
            return named;
        return permissions.GetDefaultRole(DataModelAccessClass.Visitor);
    }
}


// How loud this person is, and whether anything is coming out at all. Reads the same three answers the
// audio path acts on, so the plate cannot claim someone is talking while the mixer is discarding them.
// The muted chip wins over the mode chip: which mode a silenced person picked is not what a viewer
// needs to know. Local mutes (this viewer blocking them) deliberately do NOT show here - that is your
// setting on your machine, not a fact about them. -xlinka
internal sealed class VoiceBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var user = context.User;
        if (user == null || user.IsDestroyed)
            return;

        if (user.ActiveVoiceMode == Lumora.Core.Audio.VoiceMode.Mute
            || user.IsMuted.Value || user.IsSilenced.Value)
        {
            output.Add(NameplateBadge.Icon("voice", "mute", NameplateBadgeColors.Muted, 2));
            return;
        }

        // Normal is what everyone is by default, and a chip on every head is noise, not information. The
        // mic icon is the one in the set with nothing to say here for that reason. -xlinka
        switch (user.ActiveVoiceMode)
        {
            case Lumora.Core.Audio.VoiceMode.Whisper:
                output.Add(NameplateBadge.Icon("voice", "whisper", NameplateBadgeColors.Whisper, 2));
                break;
            case Lumora.Core.Audio.VoiceMode.Shout:
                output.Add(NameplateBadge.Icon("voice", "shout", NameplateBadgeColors.Shout, 2));
                break;
            case Lumora.Core.Audio.VoiceMode.Broadcast:
                output.Add(NameplateBadge.Icon("voice", "broadcast", NameplateBadgeColors.Broadcast, 2));
                break;
        }
    }
}

// Streaming, recording or showing a camera on their screen, as their own machine reports it: anyone in
// the room can end up in that picture, so it rides over the name for as long as it is true. The word is
// drawn in the icon rather than set in the plate font because four letters do not fit the square chip at
// any size that reads.
//
// One chip, the strongest thing true: LIVE for a stream or the camera on their screen, REC for a recording
// the streaming program confirmed. -xlinka
internal sealed class LiveBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var user = context.User;
        if (user == null || user.IsDestroyed)
            return;

        var capture = user.Capture.Value;
        if (user.IsLive.Value || capture == Photos.CaptureState.Streaming)
            output.Add(NameplateBadge.Icon("live", "live", NameplateBadgeColors.Live, 3));
        else if (capture == Photos.CaptureState.Recording)
            output.Add(NameplateBadge.Icon("live", "rec", NameplateBadgeColors.Live, 3));
    }
}

// Someone who keeps the lights on, as the accounts service says and the host wrote down: never something
// a peer can claim for itself. The chip takes their tier's colour when it has one. Staff do not wear it:
// the staff ring outranks any tier, and a tier colour beside it would only argue with it. -xlinka
internal sealed class SupporterBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var user = context.User;
        if (user == null || user.IsDestroyed || user.IsGuest || !user.IsSupporter.Value || user.IsStaff.Value)
            return;

        var tint = user.SupporterColor.Value;
        output.Add(NameplateBadge.Letter("supporter", "\u2665",
            tint.a > 0f ? tint : NameplateTheme.SupporterAccent, 4));
    }
}

// A bot or server login, as the accounts service has the account and the host wrote down. First in the row:
// before anything else on the plate, a viewer should know there is no person behind it. -xlinka
internal sealed class BotBadgeSource : INameplateBadgeSource
{
    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var user = context.User;
        if (user == null || user.IsDestroyed || user.IsGuest || !user.IsBot)
            return;

        output.Add(NameplateBadge.Icon("bot", "bot", NameplateBadgeColors.Bot, -2));
    }
}

// The staff mark, first in the row. Holds its place in the registry with no art yet: name an icon under
// the icon folder in IconName and the chip shows on every staff plate. The ring and the light across the
// name already say staff without it. -xlinka
internal sealed class StaffBadgeSource : INameplateBadgeSource
{
    public static string IconName = string.Empty;

    public void CollectBadges(in NameplateBadgeContext context, List<NameplateBadge> output)
    {
        var user = context.User;
        if (IconName.Length == 0 || user == null || user.IsDestroyed || user.IsGuest || !user.IsStaff.Value)
            return;

        output.Add(NameplateBadge.Icon("staff", IconName, NameplateBadgeColors.Staff, -1));
    }
}
