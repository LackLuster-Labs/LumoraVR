// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Cloud.Cdn;

// What Avatar Protection asks of the services. LumoraClient is the real one; a check can stand something in
// memory in for it.
public interface IProtectionRoutes
{
    // Public, no sign in. 404 for an account the service does not know.
    Task<ApiResponse<ProtectionKeySet>> GetProtectionKeys(string userId, CancellationToken ct = default);

    // Signed in. itemId is 32 lowercase hex, rules 1..15.
    Task<ApiResponse<SealGrant>> RequestSeal(string itemId, int rules, CancellationToken ct = default);

    // For the name a refusal shows.
    Task<ApiResponse<PublicUserInfo>> GetPublicUser(string userId);
}
