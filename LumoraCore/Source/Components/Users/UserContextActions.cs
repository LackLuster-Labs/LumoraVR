// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Social;

namespace Lumora.Core.Components;

// Point at somebody and open the menu: their profile is on the ring, and Report beside it. The beam rarely
// lands on a person, whose body is all query-only colliders the laser passes through, so besides walking up
// from what it did hit, the person whose chat bubble the beam crosses counts, then the one whose head sits
// closest to the beam, as long as nothing solid stands in between. A guest has no account and so no profile, and gets no entry. -xlinka
[ComponentCategory("Users")]
public class UserContextActions : ContextMenuItemSource
{
    // how far off the beam a head may sit, at least, and per metre out
    private const float NearMiss = 0.25f;
    private const float MissPerMetre = 0.05f;
    private const float Reach = 20f;
    private const int MaxNameChars = 20;

    private static readonly float[] ProfileFill = { 0.14f, 0.20f, 0.34f, 0.92f };
    private static readonly float[] ReportFill = { 0.34f, 0.13f, 0.16f, 0.92f };

    private readonly List<User> _users = new();

    public override void PopulateContextMenu(ContextMenuPage page, ContextMenuContext context)
    {
        var world = World;
        var local = world?.LocalUser;
        if (world == null || local == null || Slot?.ActiveUserRoot?.ActiveUser != local)
            return;

        var person = HitPerson(context?.Target, local) ?? PersonOnBeam(context, world, local);
        string? account = person?.AccountId?.Value;
        if (person == null || string.IsNullOrEmpty(account))
            return;

        string name = person.UserName?.Value ?? string.Empty;
        if (name.Length > MaxNameChars)
            name = name[..(MaxNameChars - 3)] + "...";
        page.AddItem(new ContextMenuItem
        {
            Group = ContextMenuGroup.Content,
            Label = "Profile",
            Detail = name,
            IconPath = "avatar",
            FillColor = ProfileFill,
            OnPressed = _ => Profiles.Open(account),
        });
        var report = ReportItem(account!, name, Engine.Current?.Social?.SelfId);
        if (report != null)
            page.AddItem(report);
    }

    // Never for the account signed in here. Signed out, it says to sign in first and the press goes there.
    internal static ContextMenuItem? ReportItem(string account, string name, string? self)
    {
        bool guest = string.IsNullOrEmpty(self);
        if (!guest && !Reports.CanReport(account, self))
            return null;
        return new ContextMenuItem
        {
            Group = ContextMenuGroup.Content,
            Label = ReportLocale.Report.Resolve(),
            Detail = guest ? ReportLocale.SignInToReport.Resolve() : name,
            IconPath = "alert",
            FillColor = ReportFill,
            OnPressed = _ => Profiles.Open(account, PersonView.Report),
        };
    }

    private static User? HitPerson(Slot? hit, User local)
    {
        var user = hit?.ActiveUserRoot?.ActiveUser;
        return user == null || ReferenceEquals(user, local) ? null : user;
    }

    private User? PersonOnBeam(ContextMenuContext? context, World world, User local)
    {
        var laser = context?.Pointer?.GetComponent<InteractionLaser>();
        if (laser == null)
            return null;
        var origin = laser.RayOrigin;
        var direction = laser.RayDirection;
        if (direction.LengthSquared < 0.5f)
            return null;
        // a wall the beam stopped on hides whoever is behind it
        float blocked = laser.CurrentHitSlot != null ? laser.CurrentHitDistance : float.PositiveInfinity;

        User? best = null;
        float bestMiss = float.PositiveInfinity;
        world.GetAllUsers(_users);
        foreach (var user in _users)
        {
            if (user == null || ReferenceEquals(user, local) || user.IsDisposed)
                continue;
            // On their chat bubble is on them, ahead of any near miss of a head.
            if (Chat.ChatBubble.Of(user) is { } bubble && bubble.Crosses(origin, direction, out float said)
                && said <= Reach && said <= blocked + 0.5f && bestMiss > 0f)
            {
                best = user;
                bestMiss = 0f;
                continue;
            }
            var head = user.Root?.HeadSlot;
            if (head == null || head.IsDestroyed)
                continue;
            var toHead = head.GlobalPosition - origin;
            float along = float3.Dot(toHead, direction);
            if (along <= 0.1f || along > Reach || along > blocked + 0.5f)
                continue;
            float miss = (toHead - direction * along).Length / (NearMiss + MissPerMetre * along);
            if (miss < 1f && miss < bestMiss)
            {
                best = user;
                bestMiss = miss;
            }
        }
        _users.Clear();
        return best;
    }
}
