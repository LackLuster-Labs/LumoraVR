// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Net;
using System.Threading.Tasks;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Web;
using Lumora.Script;
using Lumora.Warden;

namespace Lumora.Core.Scripting;

// Asset providers a script puts on a slot from an address, under the one set of rules every attach node
// shares. Only the provider kinds those nodes make, and only where the script may already change things: a
// world script on world content, a person's script on that person's own things, a Local script on what it
// made on its own machine; never the world root, a protected slot, or anything in someone's body or hands.
// The gate is asked as the author, like the same attach made by hand, and a person's provider is minted in
// the range the host keeps for their scripts, so it is theirs.
//
// A local:// address is always allowed, and so is a lumora:/// one, a stored file by its hash, which is how a
// cloud saved object names its files. An http or https address goes to the approval the web nodes use:
// the user at the machine running the script decides for its host, once, remembered or asked, a local
// address or this machine included. An address no socket can open is refused before anyone is asked, and so
// is any other kind of address. Once attached, every machine in the session loads the file the way it loads any
// address set by hand. Nothing is capped; a script attaching more than WarnPerMinute a minute is warned.
// -xlinka
internal static class ScriptAttach
{
    public const int WarnPerMinute = 60;

    public static void Start<T>(ScriptContext ctx, AwaitTicket<T> ticket, Slot? slot, string? url, Func<Slot, Uri, T> attach,
        Func<string, T> failed) where T : struct
    {
        if (!TryAddress(url, out var address, out var host, out var why))
        {
            ticket.Answer("failed", failed(why));
            return;
        }
        if (!ScriptReads.Visible(ctx, slot))
        {
            ticket.Answer("failed", failed("there is no slot the script can see to attach to"));
            return;
        }
        try
        {
            CheckPlace(ctx, slot!);
        }
        catch (UnauthorizedAccessException refused)
        {
            ticket.Answer("failed", failed(refused.Message));
            throw;
        }
        catch
        {
            ticket.Abandon();
            throw;
        }
        ScriptRates.Count(ctx, ScriptWarning.Attaches, WarnPerMinute, 60.0, "assets attached");

        if (host == null)
        {
            AttachNow(ticket, slot!, address, attach, failed);
            return;
        }
        var access = ScriptWebClient.Shared.Access;
        if (access.TryGetDecision(host, out bool allowed))
        {
            if (allowed)
                AttachNow(ticket, slot!, address, attach, failed);
            else
                ticket.Answer("denied", failed($"web access to {host} was denied"));
            return;
        }
        string reason = "WebAccess.Reason.Asset".AsLocale("A script wants to load a file from here into the world.").Resolve();
        var asking = access.AskAsync(host, ctx.Principal.DisplayName ?? "a script", ctx.World.Name, reason, ticket.Cancelled);
        ctx.World.StartCoroutine(AfterAnswer(ctx, ticket, slot!, address, host, asking, attach, failed));
    }

    // Inside the node's own call: a refusal is counted like any write's, and answers failed the way one after
    // asking does, so the flow waiting on it carries on. -xlinka
    private static void AttachNow<T>(AwaitTicket<T> ticket, Slot slot, Uri address, Func<Slot, Uri, T> attach,
        Func<string, T> failed) where T : struct
    {
        T result;
        try
        {
            result = attach(slot, address);
        }
        catch (UnauthorizedAccessException refused)
        {
            ticket.Answer("failed", failed(refused.Message));
            throw;
        }
        catch
        {
            ticket.Abandon();
            throw;
        }
        ticket.Answer("attached", result);
    }

    // What an address is: local:// or lumora:/// with no host to ask about, or http or https with the host to
    // ask about. Anything else, or an address no socket can open, is refused with why.
    public static bool TryAddress(string? text, out Uri address, out string? host, out string why)
    {
        address = null!;
        host = null;
        why = string.Empty;
        var trimmed = text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            why = "not an address";
            return false;
        }
        address = parsed;
        if (parsed.Scheme == "local")
            return true;
        // A stored file by its hash, fetched from our own content service like any cloud asset a provider
        // names, so there is no host to ask about.
        if (parsed.Scheme == Persistence.CloudAssetPacker.CloudScheme)
        {
            if (Persistence.CloudAssetPacker.HashOf(parsed.OriginalString) != null)
                return true;
            why = "a lumora address names a stored file by its hash";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            why = "only local, lumora, http and https addresses can be attached";
            return false;
        }
        host = WebAccess.HostKey(parsed);
        if (host == null)
        {
            why = "the address has no host";
            return false;
        }
        if (IPAddress.TryParse(host, out var literal) && !WebAddressFilter.IsReachable(literal))
        {
            why = $"{host} is not an address anything can be fetched from";
            return false;
        }
        return true;
    }

    // Where a script may attach, asked of the gate as its author.
    public static void CheckPlace(ScriptContext ctx, Slot slot)
    {
        if (ctx.IsLocal)
        {
            if (!slot.IsLocalElement)
                throw new UnauthorizedAccessException("a Local script attaches only to what it made on this machine");
            return;
        }
        if (slot.IsRootSlot || slot.IsProtected)
            throw new UnauthorizedAccessException("scripts cannot attach to the world root or a protected slot");
        if (slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts cannot attach to anything in someone's body or hands");
        byte owner = slot.ReferenceID.GetUserByte();
        if (ctx.Principal.IsWorldContent ? owner != RefIDConstants.AUTHORITY_BYTE : owner != ctx.Principal.AllocationByte)
            throw new UnauthorizedAccessException("a world script attaches to world content, a person's script to their own things");
        var world = ctx.World;
        var request = new DataModelPermissionRequest(world, null, slot, slot.Parent, slot,
            DataModelPermissionSurface.Component, DataModelPermissionAction.Create, isNetwork: false);
        if (world.DataModelPermissions is { } permissions && !permissions.Authorize(request, out var reason))
            throw new UnauthorizedAccessException(reason ?? "the permission gate refused the attach");
    }

    // Attaches under the principal's own range, and takes the component away again if its address did not
    // take.
    public static TProvider Attach<TProvider>(ScriptContext ctx, Slot slot, Func<TProvider> make, Func<TProvider, bool> took)
        where TProvider : Component
    {
        TProvider made;
        using (ctx.IsLocal ? null : ScriptWrites.RangeOf(ctx.World, ctx.Principal))
            made = make();
        if (!took(made))
        {
            made.Destroy();
            throw new UnauthorizedAccessException("the permission gate refused the address");
        }
        return made;
    }

    private static IEnumerator AfterAnswer<T>(ScriptContext ctx, AwaitTicket<T> ticket, Slot slot, Uri address, string host,
        Task<WebAccessAnswer> asking, Func<Slot, Uri, T> attach, Func<string, T> failed) where T : struct
    {
        yield return asking;
        if (ticket.Cancelled.IsCancellationRequested || asking.IsCanceled)
        {
            ticket.Abandon();
            yield break;
        }
        var answer = asking.IsFaulted ? WebAccessAnswer.Denied : asking.Result;
        if (answer != WebAccessAnswer.Allowed)
        {
            ticket.Answer("denied", failed(answer == WebAccessAnswer.Denied
                ? $"web access to {host} was denied" : $"web access to {host} was not answered"));
            yield break;
        }
        var world = ctx.World;
        if (world.IsDisposed || world.IsDestroyed || PermissionEngine.IsGateOpenOnThisThread || WorldConfigEdit.IsOpen
            || !ctx.Principal.IsStillAuthorIn(world))
        {
            ticket.Abandon();
            yield break;
        }
        Finish(ctx, ticket, slot, address, attach, failed);
    }

    private static void Finish<T>(ScriptContext ctx, AwaitTicket<T> ticket, Slot slot, Uri address, Func<Slot, Uri, T> attach,
        Func<string, T> failed) where T : struct
    {
        using (ctx.World.DataModelPermissions?.EnterActor(ctx.Principal))
        {
            try
            {
                if (!ScriptReads.Visible(ctx, slot))
                    throw new UnauthorizedAccessException("the slot is gone");
                CheckPlace(ctx, slot);
                ticket.Answer("attached", attach(slot, address));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                ticket.Answer("failed", failed(ex.Message));
            }
        }
    }
}
