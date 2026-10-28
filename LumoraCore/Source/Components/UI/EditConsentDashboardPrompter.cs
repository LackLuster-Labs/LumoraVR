// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Networking.Session;

namespace Lumora.Core.Components.UI;

// One ask as the dashboard holds it: who asked, in which session world, and the names to show, taken on that world's
// thread when the ask came in.
public sealed class EditConsentAsk
{
    public World World { get; }
    public User Asker { get; }
    public string Name { get; }
    public string WorldName { get; }

    public EditConsentAsk(World world, User asker, string name, string worldName)
    {
        World = world;
        Asker = asker;
        Name = name;
        WorldName = worldName;
    }

    public bool Is(World world, User asker) => ReferenceEquals(World, world) && ReferenceEquals(Asker, asker);
}

// Puts edit asks on this machine's dashboard, in userspace where only the person at this machine sees them, the same
// way web access questions go up; and tells this machine's user how their own asks went with a card in front of them
// in the world they asked in. Called on the session world's thread; everything touching the dash is posted onto the
// userspace world's own update and checked again there. -xlinka
public sealed class EditConsentDashboardPrompter : IEditConsentPrompter
{
    public void Show(World world, User asker)
    {
        var dash = UserspaceDashboard.LocalInstance;
        var home = dash?.World;
        if (dash == null || dash.IsDestroyed || home == null || home.IsDisposed || asker == null)
            return;
        var ask = new EditConsentAsk(world, asker, SessionPermissionsPanel.DisplayName(asker), WorldName(world));
        home.RunSynchronously(() =>
        {
            if (dash.IsDestroyed || dash.Dashboard is not { IsDestroyed: false } board)
                return;
            EditConsentPrompt.For(board)?.Enqueue(ask);
        });
    }

    public void Withdraw(World world, User asker)
    {
        var dash = UserspaceDashboard.LocalInstance;
        var home = dash?.World;
        if (dash == null || home == null || home.IsDisposed)
            return;
        home.RunSynchronously(() =>
        {
            if (dash.IsDestroyed || dash.Dashboard is not { IsDestroyed: false } board)
                return;
            board.Slot.GetComponent<EditConsentPrompt>()?.Withdraw(world, asker);
        });
    }

    public void Answered(World world, User owner, EditAskState was, EditAskState state) => EditConsentNotices.Answered(world, owner, was, state);

    internal static string WorldName(World? world)
        => world == null ? string.Empty : string.IsNullOrEmpty(world.WorldName?.Value) ? world.Name : world.WorldName!.Value;
}

// The cards this machine's user gets about editing other people: how an ask went, and why an edit of somebody did
// not happen. Must run on the world's thread.
public static class EditConsentNotices
{
    // One refusal card per person every so often: pointing a tool at somebody over and over is not news each time.
    private const double RefusedGapSeconds = 20.0;

    private sealed class Seen
    {
        public double At = double.NegativeInfinity;
    }

    private static readonly ConditionalWeakTable<User, Seen> Refusals = new();

    // was is where this user stood with owner before, state where they stand now.
    public static void Answered(World? world, User? owner, EditAskState was, EditAskState state)
    {
        if (world == null || owner == null || world.IsDestroyed)
            return;
        string name = SessionPermissionsPanel.DisplayName(owner);
        if (state == EditAskState.Granted)
            ToolNotice.Say(world, EditConsentLocale.YesTitle(name).Resolve(), EditConsentLocale.YesText(name).Resolve(), DashTheme.Positive);
        else if (state == EditAskState.Denied)
            ToolNotice.Say(world, EditConsentLocale.NoTitle(name).Resolve(), EditConsentLocale.NoText.Resolve());
        else if (was == EditAskState.Granted)
            ToolNotice.Say(world, EditConsentLocale.TakenBackTitle(name).Resolve(), EditConsentLocale.TakenBackText(name).Resolve());
        else if (was == EditAskState.Pending)
            ToolNotice.Say(world, EditConsentLocale.NoAnswerTitle(name).Resolve(), EditConsentLocale.NoAnswerText.Resolve());
    }

    // Something this machine's user tried on somebody was not theirs to do.
    public static void Refused(World? world, User? person, bool rootItself = false)
    {
        if (world == null || person == null || world.IsDestroyed || person.IsDestroyed)
            return;
        var seen = Refusals.GetOrCreateValue(person);
        double now = world.Time.TotalTime;
        if (now - seen.At < RefusedGapSeconds)
            return;
        seen.At = now;
        string name = SessionPermissionsPanel.DisplayName(person);
        ToolNotice.Say(world, EditConsentLocale.RefusedTitle(name).Resolve(),
            (rootItself ? EditConsentLocale.RootText(name) : EditConsentLocale.RefusedText(name)).Resolve());
    }
}
