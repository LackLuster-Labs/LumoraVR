// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets.Interop;

// Seats: a package anchor is a seat here, and its nodes sit someone in one and stand them up again. -xlinka
internal static partial class PackageNodeTable
{
    private static void Seats()
    {
        On("AnchorUser", s =>
        {
            string sit = s.Add("SitInSeat");
            s.In("Anchor", sit, "seat");
            UserOrMachine(s, s.Node, "User", sit, "user");
            Outcome(s, sit, "sat", "OnAnchored", "OnFailure");
        });
        On("ReleaseUser", s =>
        {
            string leave = s.Add("LeaveSeat");
            s.In("Anchor", leave, "seat");
            Outcome(s, leave, "released", "OnReleased", "OnFailure");
        });

        // The seat a user sits in and whether they sit at all, the user at this machine when none is named.
        On("GetUserAnchor", s =>
        {
            string seat = s.Add("UserSeat");
            UserOrMachine(s, s.Node, "User", seat, "user");
            s.Out("", seat, "seat");
        });
        On("IsUserAnchored", s =>
        {
            string seat = s.Add("UserSeat");
            UserOrMachine(s, s.Node, "User", seat, "user");
            s.Out("", seat, "seated");
        });
    }

    // An action that says whether it worked, its flow going on one way or the other.
    private static void Outcome(PackageNodeScope s, string action, string worked, string onWorked, string onFailed)
    {
        s.Op("", action);
        string which = s.Add("If");
        s.Wire(action, worked, which, "condition");
        s.Flow(action, "next", which);
        s.Exit(onWorked, which, "true");
        s.Exit(onFailed, which, "false");
    }
}
