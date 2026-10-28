// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

// SIGNING IN OR OUT INSIDE A SESSION
//
// The same proof the join asks for, run again on a live connection: the peer names the account and login
// session it now holds, the host hands it a fresh nonce, the peer signs it with that login's account key,
// and the host checks the signature against the key the accounts service published for that login before
// it writes a single field. Signing out needs no proof, since it only ever takes a name away. Everything
// lands on the user the CONNECTION belongs to, so nobody can rename anyone but themselves. -xlinka
public partial class SessionConnectionManager
{
    private const string AccountClaimContext = "lumora-session-account-claim";
    private static readonly TimeSpan AccountClaimLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccountClaimSpacing = TimeSpan.FromSeconds(2);
    private const int MaxAccountFieldLength = 128;

    private sealed class PendingClaim
    {
        public string AccountId = string.Empty;
        public string SessionId = string.Empty;
        public byte[] Nonce = null!;
        public DateTime CreatedAt = DateTime.UtcNow;
    }

    private readonly Dictionary<IConnection, PendingClaim> _pendingClaims = new();
    // Sign ins asked for and not yet decided, from the claim until its proof is checked or turned down.
    private readonly Dictionary<IConnection, DateTime> _settling = new();
    private readonly Dictionary<IConnection, DateTime> _lastClaimAt = new();

    // Peer side: tell the host which account this machine holds now. Empty for signed out.
    public void SendAccountChange()
    {
        var world = World;
        var host = HostConnection;
        if (world == null || world.IsAuthority || host == null)
            return;

        var cdn = Engine.Current?.CDNClient;
        bool hasAccount = cdn != null && cdn.HasAccountIdentity;
        SendControl(host, ControlMessage.Message.AccountChange,
            EncodeClaim(hasAccount ? cdn!.AccountUserId! : string.Empty, hasAccount ? cdn!.AccountSessionId! : string.Empty));
        LumoraLogger.Log($"[lnl] Account change sent to host ({(hasAccount ? "signed in" : "signed out")})");
    }

    // Host side.
    internal void HandleAccountChange(IConnection connection, byte[]? payload)
    {
        if (connection == null || World == null || !World.IsAuthority)
            return;
        if (!TryGetUser(connection, out var user) || user == null || user.IsDestroyed)
            return;
        if (!TryDecodeClaim(payload, out var accountId, out var sessionId))
        {
            LumoraLogger.Warn($"[lnl] Account change from {connection.Identifier}: unreadable, ignored");
            return;
        }

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_lastClaimAt.TryGetValue(connection, out var last) && now - last < AccountClaimSpacing)
            {
                LumoraLogger.Warn($"[lnl] Account change from '{user.UserName.Value}' too soon after the last one, ignored");
                return;
            }
            _lastClaimAt[connection] = now;
            _pendingClaims.Remove(connection);
        }

        if (accountId.Length == 0)
        {
            user.ApplyAccount(null, user.MachineID.Value);
            LumoraLogger.Log($"[lnl] {connection.Identifier} signed out, now '{user.UserName.Value}'");
            return;
        }

        if (sessionId.Length == 0)
        {
            user.ApplyAccount(null, user.MachineID.Value);
            return;
        }

        var nonce = RandomNumberGenerator.GetBytes(32);
        lock (_lock)
        {
            _pendingClaims[connection] = new PendingClaim { AccountId = accountId, SessionId = sessionId, Nonce = nonce, CreatedAt = now };
            _settling[connection] = now;
        }
        SendControl(connection, ControlMessage.Message.AccountChallenge, nonce);
    }

    // Host side: whether a sign in this user asked for is still being proved, so its account id may yet change. A
    // claim nobody answers counts for twice its lifetime at most.
    internal bool IsAccountSettling(User user)
    {
        if (user == null)
            return false;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var (connection, since) in _settling)
            {
                if (now - since > AccountClaimLifetime + AccountClaimLifetime)
                    continue;
                if (TryGetUser(connection, out var claimant) && ReferenceEquals(claimant, user))
                    return true;
            }
        }
        return false;
    }

    // Peer side: sign the host's nonce with the account key this login holds.
    internal void HandleAccountChallenge(IConnection connection, byte[]? nonce)
    {
        if (World == null || World.IsAuthority || nonce == null || nonce.Length != 32)
            return;
        var cdn = Engine.Current?.CDNClient;
        if (cdn == null || !cdn.HasAccountIdentity)
            return;

        byte[]? signature;
        try
        {
            signature = cdn.SignWithAccountKey(Lumora.Core.Security.MachineIdentity.BuildJoinPayload(nonce, AccountClaimContext));
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] Account challenge: signing failed ({ex.Message})");
            return;
        }
        if (signature == null || signature.Length == 0)
            return;

        var target = connection ?? HostConnection;
        if (target != null)
            SendControl(target, ControlMessage.Message.AccountProof, signature);
    }

    // Host side. A claim that does not verify leaves a guest: whoever sent it no longer holds the account
    // the user was showing, or it would have proved that one.
    internal async Task HandleAccountProof(IConnection connection, byte[]? signature)
    {
        if (connection == null || World == null || !World.IsAuthority)
            return;

        PendingClaim? claim;
        lock (_lock)
        {
            _pendingClaims.TryGetValue(connection, out claim);
            if (claim != null)
                _pendingClaims.Remove(connection);
        }
        if (claim == null || DateTime.UtcNow - claim.CreatedAt > AccountClaimLifetime)
        {
            lock (_lock)
                _settling.Remove(connection);
            LumoraLogger.Warn($"[lnl] Account proof from {connection.Identifier} with no open claim, ignored");
            return;
        }

        (AccountCard card, bool forceSilenced)? verified = null;
        try
        {
            verified = await VerifyAccountAsync(claim.AccountId, claim.SessionId, claim.Nonce,
                signature ?? Array.Empty<byte>(), AccountClaimContext);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] Account proof: verification threw ({ex.GetBaseException().Message})");
        }

        var world = World;
        if (world == null || world.IsDestroyed)
            return;

        world.RunSynchronously(() =>
        {
            lock (_lock)
                _settling.Remove(connection);
            if (!TryGetUser(connection, out var user) || user == null || user.IsDestroyed)
                return;

            if (verified == null)
            {
                user.ApplyAccount(null, user.MachineID.Value);
                LumoraLogger.Warn($"[lnl] {connection.Identifier} claimed account '{claim.AccountId}' and could not prove it; shown as '{user.UserName.Value}'");
                return;
            }

            var (card, forceSilenced) = verified.Value;
            if (Lumora.Core.Security.BanManager.IsBanned(card.AccountId, user.MachineID.Value, world.WorldName?.Value))
            {
                DisconnectUser(user, "signed in as an account banned from this world");
                return;
            }
            var refusal = CheckGroupDoor(card.AccountId) ?? CheckFriendsDoor(card.AccountId);
            if (refusal != null)
            {
                DisconnectUser(user, refusal);
                return;
            }

            user.ApplyAccount(card, user.MachineID.Value);
            if (forceSilenced)
                user.IsSilenced.Value = true;
            LumoraLogger.Log($"[lnl] {connection.Identifier} proved account {card.AccountId}, now '{user.UserName.Value}'");
        });
    }

    private void SendControl(IConnection connection, ControlMessage.Message type, byte[] payload)
    {
        var message = new ControlMessage(type) { Payload = payload };
        var encoded = message.Encode();
        connection.Send(encoded, encoded.Length, reliable: true, background: false);
    }

    private static byte[] EncodeClaim(string accountId, string sessionId)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(accountId ?? string.Empty);
            writer.Write(sessionId ?? string.Empty);
        }
        return stream.ToArray();
    }

    private static bool TryDecodeClaim(byte[]? payload, out string accountId, out string sessionId)
    {
        accountId = string.Empty;
        sessionId = string.Empty;
        if (payload == null || payload.Length == 0)
            return false;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload));
            accountId = reader.ReadString();
            sessionId = reader.ReadString();
        }
        catch (Exception)
        {
            return false;
        }
        return accountId.Length <= MaxAccountFieldLength && sessionId.Length <= MaxAccountFieldLength;
    }
}
