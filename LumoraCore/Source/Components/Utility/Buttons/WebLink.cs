// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;

namespace Lumora.Core.Components.Utility;

// A press opens a web page in this machine's browser, after asking. Only the person who pressed is asked, on
// their own dashboard, with the address in full and the reason the world gives for it; nothing opens until they
// say so. Only http and https go anywhere. With OpenOnce a second press on the same address within a few seconds
// asks nothing, so a held trigger or a button pressed twice does not stack two dialogs. -xlinka
[ComponentCategory("Utility/Buttons")]
public sealed class WebLink : Component, IPressReceiver
{
    private const double RepeatWindow = 5.0;

    private static readonly LocaleText Title = "WebLink.Title".AsLocale("Open this link?");
    private static readonly LocaleText Open = "WebLink.Open".AsLocale("Open");

    public readonly Sync<Uri> URL;

    public readonly Sync<bool> OpenOnce;

    // Shown under the address.
    public readonly Sync<string> Reason;

    private Uri? _lastAsked;
    private double _lastAskedAt;

    public WebLink()
    {
        URL = new Sync<Uri>(this, null!);
        OpenOnce = new Sync<bool>(this, true);
        Reason = new Sync<string>(this, string.Empty);
    }

    public void OnPressed(in PressEvent press)
    {
        var world = World;
        if (world == null || (press.Actor != null && !ReferenceEquals(press.Actor, world.LocalUser)))
            return;
        Ask();
    }

    [SyncMethod]
    public bool Ask()
    {
        var url = URL.Value;
        if (url == null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return false;
        double now = World?.Time.TotalTime ?? 0.0;
        if (OpenOnce.Value && url == _lastAsked && now - _lastAskedAt < RepeatWindow)
            return false;
        _lastAsked = url;
        _lastAskedAt = now;
        return AskToOpen(url, Reason.Value ?? string.Empty);
    }

    // The same question from anywhere on this machine: a link in a bio asks it too. False when there is no
    // dashboard to ask on or the address is not http or https.
    public static bool AskToOpen(Uri? url, string reason)
    {
        if (url == null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return false;
        var dash = UserspaceDashboard.LocalInstance;
        if (dash == null || dash.IsDestroyed || dash.World == null)
            return false;
        string address = url.AbsoluteUri;
        dash.World.RunSynchronously(() =>
        {
            if (dash.IsDestroyed || dash.Dashboard is not { IsDestroyed: false } board)
                return;
            dash.Open();
            string message = reason.Length > 0 ? address + "\n\n" + reason : address;
            ModalHost.Confirm(board.Slot, Title, message, Open, () => Launch(address), destructive: false);
        });
        return true;
    }

    private static void Launch(string address)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"WebLink: could not open {address}: {ex.Message}");
        }
    }
}
