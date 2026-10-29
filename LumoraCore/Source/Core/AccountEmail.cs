// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public enum ResendOutcome
{
    Sent,
    Wait,
    MailDown,
    AlreadyVerified,
    SignedOut,
    Failed,
}

// Whether the signed-in account confirmed its email address, and what waits on it until it does. Each service says for
// itself what it holds back: the inventory service puts saveBlock on the account's storage figures, the account service
// puts joinBlock on the account. So staff, and a gate switched off on the server, never hold anything here. Read on
// every sign in, when the inventory opens, after a resend and on a re-check (one read at a time, two seconds apart at
// the most), and told straight away by any refusal that names the code. -xlinka
public static class AccountEmail
{
    public const string NotVerifiedCode = "email_not_verified";
    public static readonly TimeSpan ResendGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MailDownGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadGap = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    // Accounts that were shown the sign-in notice this run, each once.
    private static readonly HashSet<string> Noticed = new(StringComparer.Ordinal);

    private static LumoraClient? _client;
    private static int _generation;
    private static string? _account;
    private static bool _known;
    private static bool _verified;
    private static bool _savesHeld;
    private static bool _joinsHeld;
    private static string? _address;
    private static DateTime _readAt = DateTime.MinValue;
    private static Task<bool>? _reading;
    private static DateTime _resendReady = DateTime.MinValue;
    private static bool _noticeDue;
    private static bool _freshSignIn;
    private static long _revision;

    // Raised off the main thread whenever anything here changes.
    public static event Action? Changed;

    public static bool Known { get { lock (Gate) return _known; } }
    public static bool Verified { get { lock (Gate) return _verified; } }
    public static bool SavesHeld { get { lock (Gate) return _savesHeld; } }
    public static bool JoinsHeld { get { lock (Gate) return _joinsHeld; } }
    public static bool AnyHeld { get { lock (Gate) return _savesHeld || _joinsHeld; } }
    public static string? Address { get { lock (Gate) return _address; } }
    public static string? Account { get { lock (Gate) return _account; } }
    public static long Revision { get { lock (Gate) return _revision; } }

    // Seconds until another mail may be asked for, 0 when it may now.
    public static int ResendWaitSeconds
    {
        get
        {
            lock (Gate)
            {
                double left = (_resendReady - DateTime.UtcNow).TotalSeconds;
                return left <= 0 ? 0 : (int)System.Math.Ceiling(left);
            }
        }
    }

    public static void Attach(LumoraClient client)
    {
        if (_client != null)
        {
            _client.Authenticated -= OnAuthenticated;
            _client.SignedOut -= OnSignedOut;
        }
        _client = client;
        client.Authenticated += OnAuthenticated;
        client.SignedOut += OnSignedOut;
        if (client.IsAuthenticated)
            OnAuthenticated(null!);
    }

    private static void OnAuthenticated(Session session)
    {
        lock (Gate)
        {
            _generation++;
            Forget();
            _freshSignIn = true;
        }
        Raise();
        _ = RecheckAsync(force: true);
    }

    private static void OnSignedOut()
    {
        lock (Gate)
        {
            _generation++;
            Forget();
            _freshSignIn = false;
            _noticeDue = false;
        }
        Raise();
        InventorySync.EmailCleared();
    }

    private static void Forget()
    {
        _known = false;
        _verified = false;
        _savesHeld = false;
        _joinsHeld = false;
        _address = null;
        _account = null;
        _readAt = DateTime.MinValue;
        _reading = null;
        _resendReady = DateTime.MinValue;
    }

    // The notice a sign in of an account with something held owes, once per account a run: whoever shows it takes it.
    public static bool TakeNotice()
    {
        lock (Gate)
        {
            if (!_noticeDue)
                return false;
            _noticeDue = false;
            return true;
        }
    }

    // Reads the account again now, or hands back the read already out; a read made less than two seconds ago stands
    // unless forced. True when the account answered.
    public static Task<bool> RecheckAsync(bool force = false)
    {
        lock (Gate)
        {
            if (_reading != null)
                return _reading;
            if (!force && DateTime.UtcNow - _readAt < ReadGap)
                return Task.FromResult(_known);
            int generation = _generation;
            var reading = Task.Run(() => ReadAsync(generation));
            _reading = reading;
            return reading;
        }
    }

    // Waits for the first read of this sign in, a little while at the most; offline or slow, it gives up and whatever
    // asked carries on as if nothing were held, which the services still check.
    public static async Task EnsureKnownAsync(TimeSpan wait)
    {
        if (Known || _client is not { IsAuthenticated: true } || !Connectivity.IsOnline)
            return;
        await Task.WhenAny(RecheckAsync(), Task.Delay(wait)).ConfigureAwait(false);
    }

    private static async Task<bool> ReadAsync(int generation)
    {
        var client = _client;
        bool answered = false;
        bool released = false;
        try
        {
            if (client == null || !client.IsAuthenticated)
                return false;
            var me = await client.GetCurrentUser().ConfigureAwait(false);
            var quota = me.Success ? await client.GetQuota().ConfigureAwait(false) : null;
            bool changed;
            lock (Gate)
            {
                if (generation != _generation)
                    return false;
                bool before = _savesHeld;
                var state = (_known, _verified, _savesHeld, _joinsHeld, _address, _account);
                if (me.Success && me.Data != null)
                {
                    answered = true;
                    _known = true;
                    _verified = me.Data.IsVerified;
                    _address = string.IsNullOrWhiteSpace(me.Data.Email) ? _address : me.Data.Email;
                    _account = me.Data.Id;
                    _joinsHeld = me.Data.JoinBlock == NotVerifiedCode;
                    if (quota is { Success: true, Data: { } figures })
                        _savesHeld = figures.SaveBlock == NotVerifiedCode;
                    else if (_verified)
                        _savesHeld = false;
                    _readAt = DateTime.UtcNow;
                }
                released = before && !_savesHeld;
                changed = state != (_known, _verified, _savesHeld, _joinsHeld, _address, _account);
                if (answered && _freshSignIn)
                {
                    _freshSignIn = false;
                    if ((_savesHeld || _joinsHeld) && _account != null && Noticed.Add(_account))
                        _noticeDue = true;
                }
                if (changed)
                    _revision++;
            }
            if (changed)
                Raise();
            if (released)
                InventorySync.EmailCleared();
            return answered;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Account: could not read whether the email is verified: {ex.Message}");
            return false;
        }
        finally
        {
            lock (Gate)
            {
                if (generation == _generation)
                    _reading = null;
            }
        }
    }

    // A service turned something down because the email is not confirmed: what it holds is held from now, until a read
    // says otherwise.
    public static void Refused(bool saves, bool joins)
    {
        bool changed;
        lock (Gate)
        {
            var before = (_savesHeld, _joinsHeld);
            _savesHeld |= saves;
            _joinsHeld |= joins;
            changed = before != (_savesHeld, _joinsHeld);
            if (changed)
                _revision++;
        }
        if (changed)
            Raise();
    }

    // The confirmation mail again. Never more than one a minute from here, whatever is pressed; the account service
    // keeps its own minute as well and says how long is left when asked too soon.
    public static async Task<ResendOutcome> ResendAsync()
    {
        var client = _client;
        if (client == null || !client.IsAuthenticated)
            return ResendOutcome.SignedOut;
        lock (Gate)
        {
            if (DateTime.UtcNow < _resendReady)
                return ResendOutcome.Wait;
            _resendReady = DateTime.UtcNow + ResendGap;
            _revision++;
        }
        Raise();

        ApiResponse answer;
        try
        {
            answer = await client.ResendVerificationEmail().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Account: the confirmation mail could not be asked for: {ex.Message}");
            WaitFor(MailDownGap);
            return ResendOutcome.Failed;
        }

        ResendOutcome outcome;
        if (answer.Success)
            outcome = ResendOutcome.Sent;
        else if (answer.Error == "already_verified" || (answer.Status == HttpStatusCode.BadRequest && answer.Message?.Contains("already verified", StringComparison.OrdinalIgnoreCase) == true))
            outcome = ResendOutcome.AlreadyVerified;
        else if (answer.Error == "too_soon" || answer.Status == HttpStatusCode.TooManyRequests)
        {
            WaitFor(TimeSpan.FromSeconds(RetryAfter(answer) ?? ResendGap.TotalSeconds));
            outcome = ResendOutcome.Wait;
        }
        else if (answer.Error == "mail_unavailable" || (int)answer.Status >= 500 || answer.Status == HttpStatusCode.RequestTimeout)
        {
            WaitFor(MailDownGap);
            outcome = ResendOutcome.MailDown;
        }
        else
        {
            WaitFor(TimeSpan.Zero);
            outcome = ResendOutcome.Failed;
        }
        if (outcome != ResendOutcome.Sent)
            LumoraLogger.Log($"Account: the confirmation mail was not sent ({outcome}, {(int)answer.Status}).");
        _ = RecheckAsync(force: true);
        return outcome;
    }

    private static void WaitFor(TimeSpan gap)
    {
        lock (Gate)
        {
            _resendReady = DateTime.UtcNow + gap;
            _revision++;
        }
        Raise();
    }

    private static double? RetryAfter(ApiResponse answer)
    {
        if (string.IsNullOrEmpty(answer.RawBody))
            return null;
        try
        {
            using var body = JsonDocument.Parse(answer.RawBody);
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("retryAfter", out var value)
                && value.TryGetDouble(out var seconds) && seconds > 0 ? System.Math.Min(seconds, 3600) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What a resend came to, in words for whoever pressed it.
    public static Localization.LocaleText Describe(ResendOutcome outcome) => outcome switch
    {
        ResendOutcome.Sent => AccountEmailLocale.Sent(Address ?? string.Empty),
        ResendOutcome.Wait => AccountEmailLocale.WaitSeconds(System.Math.Max(1, ResendWaitSeconds)),
        ResendOutcome.MailDown => AccountEmailLocale.MailDown,
        ResendOutcome.AlreadyVerified => AccountEmailLocale.AlreadyVerified,
        ResendOutcome.SignedOut => AccountEmailLocale.SignInFirst,
        _ => AccountEmailLocale.SendFailed,
    };

    private static void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Account: an email state listener failed: {ex.Message}");
        }
    }
}
