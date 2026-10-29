// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Networking.Session;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Social;

public enum InviteProblem
{
    None,
    // this user may not invite in this world (the right's Bar says why)
    Bar,
    Refused,
    NoAnswer,
    NotListed,
    // the service said no; Message has its words
    Service,
}

public sealed record InviteOutcome(string UserId, InviteProblem Problem, string? Message = null)
{
    public bool Sent => Problem == InviteProblem.None;
}

// SENDING INVITES
//
// In this order, because each step is what the next one stands on: the inviter's right in this world, a hidden
// listing for a world nobody lists yet (its host only), the host putting each account on its book where the
// level would turn them away, one upload of the world's picture, then one chat message per friend. A friend
// the host turned down, or never answered for, gets no message, so nobody is sent to a door that will not
// open. Asks go in batches the size of the ask record, since a record only carries its last few. -xlinka
public static class InviteSender
{
    public static readonly TimeSpan ListWait = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WorldWait = TimeSpan.FromSeconds(5);

    // Puts the world's picture in the content store and answers its hash, null when it could not. A test
    // stands in for the store here.
    public static Func<byte[], Task<string?>> PreviewUpload { get; set; } = UploadAsync;

    public static async Task<IReadOnlyList<InviteOutcome>> SendAsync(World world, IReadOnlyList<string> userIds, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<InviteOutcome>();
        var ids = new List<string>();
        foreach (var id in userIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id))
                ids.Add(id);
        }
        if (ids.Count == 0)
            return outcomes;

        var social = Engine.Current?.Social;
        var start = await OnWorld(world, () => (
            Right: SessionInvites.RightOf(world, world.LocalUser),
            SessionId: world.Session?.SessionId,
            Preview: world.PreviewThumbnailBase64,
            Listed: world.Session?.IsListed == true)).ConfigureAwait(false);
        if (social == null || !start.Ok || !start.Value.Right.Allowed || string.IsNullOrEmpty(start.Value.SessionId))
            return All(ids, InviteProblem.Bar);
        var (right, sessionId, preview, listed) = start.Value;

        if (right.NeedsListing && !listed)
        {
            await OnWorld(world, () => { world.Session?.ListForInvites(); return true; }).ConfigureAwait(false);
            if (!await Until(() => world.Session?.IsListed == true, ListWait, cancellationToken).ConfigureAwait(false))
                return All(ids, InviteProblem.NotListed);
        }

        var cleared = new List<string>();
        if (right.Way == InviteWay.Grant)
        {
            for (int at = 0; at < ids.Count; at += SessionInvites.AskDepth)
            {
                var batch = ids.GetRange(at, System.Math.Min(SessionInvites.AskDepth, ids.Count - at));
                var asked = await OnWorld(world, () =>
                {
                    var numbers = new int[batch.Count];
                    for (int i = 0; i < batch.Count; i++)
                        numbers[i] = SessionInvites.Ask(world, batch[i]);
                    return numbers;
                }).ConfigureAwait(false);
                if (!asked.Ok)
                {
                    outcomes.AddRange(All(batch, InviteProblem.NoAnswer));
                    continue;
                }
                var answers = new InviteAnswer[batch.Count];
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    var read = await OnWorld(world, () =>
                    {
                        for (int i = 0; i < batch.Count; i++)
                            answers[i] = asked.Value[i] == 0 ? InviteAnswer.Refused : SessionInvites.AnswerFor(world, asked.Value[i]);
                        return Array.IndexOf(answers, InviteAnswer.Pending) < 0;
                    }).ConfigureAwait(false);
                    if ((read.Ok && read.Value) || waited.Elapsed >= AnswerWait)
                        break;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                for (int i = 0; i < batch.Count; i++)
                {
                    if (answers[i] == InviteAnswer.Granted)
                        cleared.Add(batch[i]);
                    else
                        outcomes.Add(new InviteOutcome(batch[i], answers[i] == InviteAnswer.Refused ? InviteProblem.Refused : InviteProblem.NoAnswer));
                }
            }
        }
        else
        {
            cleared.AddRange(ids);
        }
        if (cleared.Count == 0)
            return outcomes;

        string? picture = null;
        if (!string.IsNullOrEmpty(preview))
        {
            try
            {
                picture = await PreviewUpload(Convert.FromBase64String(preview)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"InviteSender: the world's picture did not upload - {ex.Message}");
            }
        }

        foreach (var id in cleared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var answer = await social.SendInviteAsync(id, sessionId!, picture).ConfigureAwait(false);
                outcomes.Add(answer.Success ? new InviteOutcome(id, InviteProblem.None) : new InviteOutcome(id, InviteProblem.Service, answer.Message));
            }
            catch (Exception ex)
            {
                outcomes.Add(new InviteOutcome(id, InviteProblem.Service, ex.Message));
            }
        }
        return outcomes;
    }

    private static List<InviteOutcome> All(List<string> ids, InviteProblem problem)
    {
        var list = new List<InviteOutcome>(ids.Count);
        foreach (var id in ids)
            list.Add(new InviteOutcome(id, problem));
        return list;
    }

    private static async Task<string?> UploadAsync(byte[] picture)
    {
        var client = Engine.Current?.CDNClient;
        // Held until the email is verified, the invite goes without its picture rather than not at all.
        if (client == null || !client.IsAuthenticated || picture.Length == 0 || AccountEmail.SavesHeld)
            return null;
        string hash = Convert.ToHexString(SHA256.HashData(picture)).ToLowerInvariant();
        var answer = await client.UploadContent(picture, hash, "jpg", "image/jpeg").ConfigureAwait(false);
        return answer.Success ? hash : null;
    }

    // Runs on the world's own update and hands the answer back; Ok false when the world never ran it.
    private static async Task<(bool Ok, T Value)> OnWorld<T>(World world, Func<T> read)
    {
        if (world == null || world.IsDisposed || world.IsDestroyed)
            return (false, default!);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunSynchronously(() =>
        {
            try
            {
                done.TrySetResult(read());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        var finished = await Task.WhenAny(done.Task, Task.Delay(WorldWait)).ConfigureAwait(false);
        if (finished != done.Task || !done.Task.IsCompletedSuccessfully)
            return (false, default!);
        return (true, done.Task.Result);
    }

    private static async Task<bool> Until(Func<bool> done, TimeSpan within, CancellationToken cancellationToken)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (done())
                return true;
            if (waited.Elapsed >= within)
                return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}
