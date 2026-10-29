// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Components.Scripting;

namespace Lumora.Core.Scripting;

// The host asking its world's join checks about someone joining.
//
// Only a world script the host has let check joins hears it (ScriptHost.JoinCheck), and only once the joiner
// has proved who they are and passed every rule the session has of its own: bans, the access level, the doors.
// So a check can only narrow who gets in, never open a door the session keeps shut, and it can pick the role
// they arrive with from the ones the world's mode offers. A guest's script, a Local one or one the host never
// switched on is never asked.
//
// A check has answered when it refuses, when the flow that let someone in has run to its end, or when it has
// nothing left to run: every check has heard the request, and none has events, awaits or delays still under
// way. A refusal from any of them turns the joiner away. Still busy after AnswerSeconds, the joiner is turned
// away too, as a check that never finished would never have let them in. -xlinka
public static class ScriptJoinChecks
{
    public const double AnswerSeconds = 15.0;

    public readonly record struct Verdict(bool Refused, string? Reason, string? Role);

    private sealed class Asking
    {
        public required JoinRequest Request;
        public required List<ScriptHostRuntime> Checks;
        public required bool[] Heard;
        public required long Deadline;
        public required TaskCompletionSource<Verdict> Answer;
    }

    // From any thread: the checks are asked on the world's own, and the answer comes back on a worker.
    public static Task<Verdict> AskAsync(World world, string accountId, string name, string machineId, bool friend, bool silenced)
    {
        var answer = new TaskCompletionSource<Verdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (world == null || world.IsDisposed)
        {
            answer.TrySetResult(default);
            return answer.Task;
        }
        world.RunSynchronously(() =>
        {
            try
            {
                Begin(world, accountId ?? string.Empty, name ?? string.Empty, machineId ?? string.Empty, friend, silenced, answer);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Join checks: could not ask the world's checks: {ex.Message}");
                answer.TrySetResult(new Verdict(true, "This world's join check could not run", null));
            }
        });
        return answer.Task;
    }

    private static void Begin(World world, string accountId, string name, string machineId, bool friend, bool silenced,
        TaskCompletionSource<Verdict> answer)
    {
        if (world.IsDisposed || !world.IsAuthority)
        {
            answer.TrySetResult(default);
            return;
        }
        var hosts = new List<ScriptHost>();
        world.Scripts.GetHosts(hosts);
        var checks = new List<ScriptHostRuntime>();
        foreach (var host in hosts)
        {
            if (host.JoinCheck is { } runtime)
                checks.Add(runtime);
        }
        if (checks.Count == 0)
        {
            answer.TrySetResult(default);
            return;
        }

        string key = UserMachineId.KeyFor(world, machineId);
        var request = new JoinRequest(world, accountId, name, key, accountId.Length == 0, friend, silenced);
        var payload = new JoinRequested(request, accountId, name, key, request.Guest, friend, silenced);
        var asking = new Asking
        {
            Request = request,
            Checks = checks,
            Heard = new bool[checks.Count],
            Deadline = Environment.TickCount64 + (long)(AnswerSeconds * 1000),
            Answer = answer,
        };
        for (int i = 0; i < checks.Count; i++)
        {
            int at = i;
            checks[i].Enqueue(runner =>
            {
                var result = runner.Raise(payload);
                asking.Heard[at] = true;
                return result;
            });
        }
        world.RunInUpdates(1, () => Poll(world, asking));
    }

    private static void Poll(World world, Asking asking)
    {
        var request = asking.Request;
        if (world.IsDisposed)
        {
            Settle(asking, new Verdict(true, "The world closed", null));
            return;
        }
        bool busy = false;
        for (int i = 0; i < asking.Checks.Count && !busy; i++)
        {
            var check = asking.Checks[i];
            // A check stopped or swapped out has nothing more to say.
            if (!check.CanRun)
                continue;
            var instance = check.Runner.Instance;
            busy = !asking.Heard[i] || check.QueuedEvents > 0 || instance.PendingAwaits > 0 || instance.PendingTimers > 0;
        }
        if (request.Answer != JoinAnswer.None || !busy)
        {
            Settle(asking, request.Answer == JoinAnswer.Refused
                ? new Verdict(true, request.Reason ?? "This world's join check turned you away", null)
                : new Verdict(false, null, request.Role));
            return;
        }
        if (Environment.TickCount64 >= asking.Deadline)
        {
            Logging.Logger.Warn($"Join checks: no answer for '{request.Name}' after {AnswerSeconds:0} s; turned away");
            Settle(asking, new Verdict(true, "This world's join check did not answer in time", null));
            return;
        }
        world.RunInUpdates(1, () => Poll(world, asking));
    }

    private static void Settle(Asking asking, in Verdict verdict)
    {
        asking.Request.Settled = true;
        asking.Answer.TrySetResult(verdict);
    }

    // Whether a script may answer this request: the world's own script, on the host, for a request of its world.
    internal static bool Answerable(ScriptContext ctx, JoinRequest? request)
    {
        if (request == null)
        {
            ctx.Fail("no join request");
            return false;
        }
        if (!ReferenceEquals(request.World, ctx.World) || !ctx.Principal.IsWorldContent || ctx.IsLocal || !ctx.World.IsAuthority)
        {
            ctx.Fail("only the world's own scripts on the host answer join requests");
            return false;
        }
        return true;
    }

    // The role's own name when the world's mode offers it, Host never.
    public static string? AssignableRole(World world, string? role, out string why)
    {
        why = string.Empty;
        var permissions = world.DataModelPermissions;
        var found = permissions?.FindRole(role?.Trim());
        if (permissions == null || found == null)
        {
            why = $"no role is named '{role}'";
            return null;
        }
        if (ReferenceEquals(found, permissions.HostRole))
        {
            why = "nobody is made host";
            return null;
        }
        foreach (var offered in permissions.AssignableRolesFor(world.Mode))
        {
            if (ReferenceEquals(offered, found))
                return found.Name;
        }
        why = $"this world's mode offers no {found.Name} role";
        return null;
    }

    // The role a check chose, given to the person who just came in, the way the session screen gives one:
    // pinned to them in the world's config, and in force at once.
    internal static void GiveRole(World world, User user, string? role)
    {
        if (string.IsNullOrEmpty(role) || !world.IsAuthority || AssignableRole(world, role, out _) is not { } name
            || world.DataModelPermissions?.FindRole(name) is not { } found)
            return;
        world.PermissionConfig?.SetRole(user, name);
        world.DataModelPermissions.SetUserRole(user, found);
    }
}
