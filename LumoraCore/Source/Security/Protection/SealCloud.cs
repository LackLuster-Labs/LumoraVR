// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Security;

// Avatar Protection's way to the services, through the client the engine signs in with. One directory per client,
// so a sign in on a new client starts from empty caches.
public static class SealCloud
{
    private static readonly object Gate = new();
    private static IProtectionRoutes? _routes;
    private static ProtectionDirectory? _directory;

    public static IProtectionRoutes? Routes => Engine.Current?.CDNClient;

    public static ProtectionDirectory? Directory
    {
        get
        {
            var routes = Routes;
            if (routes == null)
                return null;
            lock (Gate)
            {
                if (!ReferenceEquals(routes, _routes) || _directory == null)
                {
                    _routes = routes;
                    _directory = new ProtectionDirectory(routes);
                }
                return _directory;
            }
        }
    }

    // The account signed in on this machine, as the services know it. Null when nobody is.
    public static string? SignedInAccount
    {
        get
        {
            var client = Engine.Current?.CDNClient;
            return client is { IsAuthenticated: true } && !string.IsNullOrEmpty(client.AccountUserId) ? client.AccountUserId : null;
        }
    }

    public static Task<ApiResponse<SealGrant>> RequestAsync(string itemId)
    {
        var routes = Routes;
        return routes == null
            ? Task.FromResult(ApiResponse<SealGrant>.Fail(System.Net.HttpStatusCode.ServiceUnavailable, "Offline"))
            : routes.RequestSeal(itemId, ProtectionRules.All);
    }
}
