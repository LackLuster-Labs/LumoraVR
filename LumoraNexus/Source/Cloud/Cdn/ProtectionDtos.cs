// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Lumora.Nexus.Cloud.Cdn;

// The Avatar Protection routes, as the accounts service sends them. The signing keys stay on the
// accounts service; a game only ever sees the public halves and the seals made with them.

// What a seal keeps people other than its owner from doing. The game asks for All.
public static class ProtectionRules
{
    public const int Equip = 1;
    public const int SaveCopy = 2;
    public const int Export = 4;
    public const int Inspect = 8;
    public const int All = Equip | SaveCopy | Export | Inspect;
}

// GET /api/users/{id}/protection-keys. Retired keys stay listed so seals made with them still check out.
public record ProtectionKeySet
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("keys")] public List<ProtectionKey> Keys { get; init; } = new();
}

public record ProtectionKey
{
    [JsonPropertyName("keyId")] public string KeyId { get; init; } = "";
    // SPKI DER, base64.
    [JsonPropertyName("publicKey")] public string PublicKey { get; init; } = "";
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; init; }
    [JsonPropertyName("retiredAt")] public DateTime? RetiredAt { get; init; }
}

// POST /api/protection/seal: the signed seal the game writes onto the item.
public record SealGrant
{
    [JsonPropertyName("ownerId")] public string OwnerId { get; init; } = "";
    [JsonPropertyName("keyId")] public string KeyId { get; init; } = "";
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    [JsonPropertyName("rules")] public int Rules { get; init; }
    [JsonPropertyName("issuedAt")] public long IssuedAt { get; init; }
    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
}
