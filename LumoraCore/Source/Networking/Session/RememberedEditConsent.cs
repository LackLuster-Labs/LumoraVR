// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumora.Core.Networking.Session;

// Remembered choices belong to the signed-in account on this device. Only proved session account IDs
// can match them; names and transient session IDs never grant access after a reconnect. -xlinka
public static class RememberedEditConsent
{
    private const string Prefix = "Consent.Remembered.";
    private sealed class State
    {
        public string Owner = string.Empty;
        public readonly HashSet<User> People = new();
    }
    private static readonly ConditionalWeakTable<World, State> Applied = new();
    public readonly record struct Entry(string AccountId, string Name);

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Root(User owner) => Prefix + Encode(owner.AccountId.Value) + ".";
    private static string Key(User owner, string account) => Root(owner) + Encode(account);

    public static bool CanRemember(World? world, User? person)
        => world?.LocalUser is { IsDestroyed: false } me && person is { IsDestroyed: false }
           && person.World == world && !ReferenceEquals(me, person)
           && !me.IsGuest && !person.IsGuest;

    public static bool Contains(User? owner, string account)
        => owner is { IsDestroyed: false, IsGuest: false } && !string.IsNullOrEmpty(account)
           && Settings.ReadValue(Key(owner, account), string.Empty).StartsWith(account + "\n", StringComparison.Ordinal);

    public static bool Remember(World world, User person)
    {
        if (!CanRemember(world, person))
            return false;
        Settings.WriteValue(Key(world.LocalUser!, person.AccountId.Value), person.AccountId.Value + "\n" + person.UserName.Value);
        Current(world).People.Add(person);
        return true;
    }

    public static void Forget(User? owner, string account)
    {
        if (owner is { IsGuest: false } && !string.IsNullOrEmpty(account))
            Settings.DeleteValue(Key(owner, account));
    }

    public static List<Entry> Entries(User? owner)
    {
        var result = new List<Entry>();
        if (owner is not { IsDestroyed: false, IsGuest: false })
            return result;
        foreach (string key in Settings.KeysUnder(Root(owner)))
        {
            var value = Settings.ReadValue(key, string.Empty).Split('\n', 2);
            if (value.Length == 2 && key == Key(owner, value[0]))
                result.Add(new Entry(value[0], value[1]));
        }
        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    private static State Current(World world)
    {
        var state = Applied.GetValue(world, _ => new State());
        string owner = world.LocalUser?.AccountId.Value ?? string.Empty;
        if (state.Owner != owner)
        {
            foreach (var person in state.People)
                SessionEditConsent.RevokeRememberedSession(world, person);
            state.People.Clear();
            state.Owner = owner;
        }
        return state;
    }

    internal static void Apply(World world)
    {
        var applied = Current(world).People;
        var me = world.LocalUser;
        if (me == null || me.IsGuest)
            return;
        foreach (var person in applied.ToArray())
        {
            if (person.IsDestroyed || person.World != world)
                applied.Remove(person);
            else if (!Contains(me, person.AccountId.Value))
            {
                SessionEditConsent.RevokeRememberedSession(world, person);
                applied.Remove(person);
            }
        }
        var granted = new HashSet<User>(SessionEditConsent.GrantedBy(me));
        foreach (var person in world.GetAllUsers())
        {
            if (!CanRemember(world, person) || !Contains(me, person.AccountId.Value))
                continue;
            if (!granted.Contains(person))
            {
                if (granted.Count >= SessionEditConsent.RecordDepth)
                    continue;
                if (!SessionEditConsent.Answer(world, person, true))
                    continue;
                granted.Add(person);
            }
            applied.Add(person);
        }
    }
}
