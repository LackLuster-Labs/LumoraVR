// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Security.Cryptography;
using System.Text;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

// What a signed-in account shows of itself in a session. Only ever built from the accounts service's
// answer about an account that has already signed the host's challenge, so nothing a peer says about
// itself reaches a name or a colour. -xlinka
public sealed record AccountCard(
    string AccountId,
    string Username,
    color NameColor,
    bool IsSupporter,
    string SupporterTier,
    color SupporterColor,
    bool IsStaff = false,
    RepresentedGroupInfo? Group = null,
    string SupporterStyle = "",
    string AccountKind = AccountKinds.User)
{
    public const int MaxNameLength = 32;

    // Same alphabet the eye can't confuse: no 0/O or 1/I.
    private const string GuestAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static AccountCard? From(PublicUserInfo? info)
    {
        if (info == null || string.IsNullOrWhiteSpace(info.Id) || string.IsNullOrWhiteSpace(info.Username))
            return null;

        bool supporter = false;
        string tier = string.Empty;
        foreach (var tag in info.Tags)
        {
            if (tag == "supporter")
                supporter = true;
            else if (tag.StartsWith("tier:", StringComparison.Ordinal))
                tier = tag.Substring(5);
        }

        return new AccountCard(
            info.Id,
            Clip(info.Username.Trim()),
            ParseHex(info.NameColor),
            supporter,
            supporter ? Clip(tier) : string.Empty,
            supporter ? ParseHex(info.SupporterColor) : default,
            info.IsStaff,
            UsableGroup(info.RepresentedGroup),
            supporter ? Clip(info.SupporterStyle ?? string.Empty) : string.Empty,
            AccountKinds.IsBot(info.AccountKind?.Trim()) ? Clip(info.AccountKind!.Trim()) : AccountKinds.User);
    }

    public bool IsBot => AccountKinds.IsBot(AccountKind);

    // A guest is named after the machine key it proved at the door, so the same machine reads the same
    // in every session. The '#' is a character no account name may hold, which is what keeps a guest from
    // ever reading as somebody's account. -xlinka
    public static string GuestName(string? machineKey)
    {
        if (string.IsNullOrEmpty(machineKey))
            return "Guest";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineKey));
        uint bits = (uint)(hash[0] << 16 | hash[1] << 8 | hash[2]);
        Span<char> code = stackalloc char[4];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = GuestAlphabet[(int)(bits & 31)];
            bits >>= 5;
        }
        return "Guest #" + new string(code);
    }

    // "#RRGGBB" or "#RRGGBBAA" the way a picker hands it over, decoded once so everything downstream gets
    // a linear value. Anything unparseable is a clear colour, which readers take as "nothing set".
    public static color ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
            return default;
        var span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
            span = span.Slice(1);
        if (span.Length != 6 && span.Length != 8)
            return default;
        if (!int.TryParse(span.Slice(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(span.Slice(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(span.Slice(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return default;
        }
        int a = 255;
        if (span.Length == 8 && !int.TryParse(span.Slice(6, 2), System.Globalization.NumberStyles.HexNumber, null, out a))
            a = 255;
        return new color(r / 255f, g / 255f, b / 255f, a / 255f).ToLinear();
    }

    // A card with no id or nothing to show is no card.
    private static RepresentedGroupInfo? UsableGroup(RepresentedGroupInfo? group)
        => group == null || string.IsNullOrWhiteSpace(group.Id) ? null : group;

    private static string Clip(string text)
        => text.Length <= MaxNameLength ? text : text.Substring(0, MaxNameLength);
}
