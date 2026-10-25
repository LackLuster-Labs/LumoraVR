// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumora.Core.Components.Utility;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Package nodes first seen in worlds: users, worlds, time and dates, strings and casts. -xlinka
internal static partial class PackageNodeTable
{
    private static void WorldB()
    {
        // An account id, or none: ours reads empty for a guest where the package reads null, which a fallback
        // after it tells apart.
        On("UserUserID", s =>
        {
            string id = s.Add("UserIdentity");
            s.In("User", id, "user");
            string empty = s.Add("IsSame", "string");
            s.Wire(id, "id", empty, "a");
            s.Set(empty, "b", string.Empty);
            string none = s.Add("Conditional", "string");
            s.Wire(empty, "same", none, "condition");
            s.Wire(id, "id", none, "whenFalse");
            s.Out("", none, "result");
        });

        // Ours never hands out the machine's own id: a key made of it for this session only.
        On("UserMachineID", s =>
        {
            string id = s.Add("UserMachineId");
            s.In("User", id, "user");
            s.Out("", id, "id");
        });

        // Left users reach the host only, unless asked otherwise. Who left is kept while the flow runs and
        // cleared after it, so a flow from elsewhere reading it reads nobody, as in the package.
        On("UserLeft", s =>
        {
            if (!s.Wired("OnLeft"))
            {
                s.OutConstant("LeftUser", null);
                return;
            }
            string left = s.Add("OnUserLeft");
            string after = left;
            string afterExit = "next";
            bool? onlyHost = Flag(s, "OnlyHost", true);
            if (onlyHost != false)
            {
                string host = s.Add("IsSessionHost");
                s.Wire(s.Add("MachineUser"), "user", host, "user");
                string gate = s.Add("If");
                s.Flow(left, "next", gate);
                if (onlyHost == true)
                    s.Wire(host, "host", gate, "condition");
                else
                {
                    string anyone = s.Add("Not", "bool");
                    s.In("OnlyHost", anyone, "value");
                    string either = s.Add("Or", "bool,bool");
                    s.Wire(host, "host", either, "a");
                    s.Wire(anyone, "result", either, "b");
                    s.Wire(either, "result", gate, "condition");
                }
                after = gate;
                afterExit = "true";
            }
            if (!s.IsRead("LeftUser"))
            {
                s.Exit("OnLeft", after, afterExit);
                return;
            }
            string kept = s.Add("StoredValue", "user");
            string keep = s.Add("WriteValue", "user");
            s.Set(keep, "var", kept);
            s.Wire(left, "user", keep, "value");
            s.Flow(after, afterExit, keep);
            string both = s.Add("Sequence");
            s.Flow(keep, "next", both);
            s.Exit("OnLeft", both, "out0");
            string clear = s.Add("WriteValue", "user");
            s.Set(clear, "var", kept);
            s.Flow(both, "out1", clear);
            s.Out("LeftUser", kept, "value");
        });
        Same(_ => { }, "UserLeft+Proxy", "Stopwatch+Proxy");

        // On the updating user's machine (the host's when none is set, unless told to skip then), any frame the
        // interval has passed since it last went on, by this machine's world clock. The package stops listening
        // while it is not that machine's; ours asks each frame, there being no event for the user changing. -xlinka
        On("SecondsTimer", s =>
        {
            if (!s.Wired("OnUpdate"))
                return;
            string tick = s.Add("OnUpdate");
            string mine = s.Add("IsMachineUser");
            bool? skip = Flag(s, "SkipIfNull", false);
            if (skip == true)
                s.In("UpdatingUser", mine, "user");
            else
            {
                string who = s.Add("Fallback", "user");
                s.In("UpdatingUser", who, "value");
                s.Wire(s.Add("SessionHostUser"), "user", who, "fallback");
                if (skip == false)
                    s.Wire(who, "result", mine, "user");
                else
                {
                    string pick = s.Add("Conditional", "user");
                    s.In("SkipIfNull", pick, "condition");
                    s.In("UpdatingUser", pick, "whenTrue");
                    s.Wire(who, "result", pick, "whenFalse");
                    s.Wire(pick, "result", mine, "user");
                }
            }
            string gate = s.Add("If");
            s.Flow(tick, "next", gate);
            s.Wire(mine, "local", gate, "condition");

            string last = s.Add("StoredValue", "double");
            string clock = s.Add("WorldTime");
            string since = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", since, "a");
            s.Wire(last, "value", since, "b");
            string due = s.Add("GreaterOrEqual", "double,double");
            s.Wire(since, "result", due, "a");
            if (s.Wired("Interval"))
            {
                string wide = s.Add("ToDouble", "float");
                s.In("Interval", wide, "value", 0d);
                s.Wire(wide, "result", due, "b");
            }
            else
                s.Set(due, "b", 0d);
            string check = s.Add("If");
            s.Flow(gate, "true", check);
            s.Wire(due, "result", check, "condition");
            string mark = s.Add("WriteValue", "double");
            s.Set(mark, "var", last);
            s.Wire(clock, "seconds", mark, "value");
            s.Flow(check, "true", mark);
            s.Exit("OnUpdate", mark, "next");
        });

        // A synced, saved stopwatch: whether it runs, the session instant it read zero at while it runs, and
        // what it read when it stopped, each kept on the script's slot and seeded from the package. The
        // session clock is wall time every machine agrees on, so one saved running goes on counting through
        // the time the world was closed, where the package picks up from where it was saved. -xlinka
        On("Stopwatch", s =>
        {
            var saved = s.Helper("Stopwatch+Proxy")?.Stored("Stopwatch") as DataTreeDictionary;
            bool play = (saved?.TryGetNode("Play") as DataTreeValue)?.Value is IConvertible p && Convert.ToBoolean(p, CultureInfo.InvariantCulture);
            double position = (saved?.TryGetNode("RawPosition") as DataTreeValue)?.Value is IConvertible r
                ? Convert.ToDouble(r, CultureInfo.InvariantCulture) : 0d;
            if (!double.IsFinite(position))
                position = 0d;
            var runningHolder = s.Home.AttachComponent<ValueHolder<bool>>();
            var startHolder = s.Home.AttachComponent<ValueHolder<double>>();
            var heldHolder = s.Home.AttachComponent<ValueHolder<double>>();
            runningHolder.Value.Value = play;
            heldHolder.Value.Value = position;
            startHolder.Value.Value = play ? s.Home.World.SessionSeconds - position : 0d;
            var running = PackageNodeIndex.MemberOf(runningHolder, "Value", -1)!;
            var start = PackageNodeIndex.MemberOf(startHolder, "Value", -1)!;
            var held = PackageNodeIndex.MemberOf(heldHolder, "Value", -1)!;

            string isRunning = s.Add("GetField", "bool");
            s.Bind(isRunning, "field", running, "field<bool>");
            string startAt = s.Add("GetField", "double");
            s.Bind(startAt, "field", start, "field<double>");
            string heldAt = s.Add("GetField", "double");
            s.Bind(heldAt, "field", held, "field<double>");
            string clock = s.Add("SessionTime");
            string since = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", since, "a");
            s.Wire(startAt, "result", since, "b");

            s.Out("IsRunning", isRunning, "result");
            if (s.IsRead("Time"))
            {
                string now = s.Add("Conditional", "double");
                s.Wire(isRunning, "result", now, "condition");
                s.Wire(since, "result", now, "whenTrue");
                s.Wire(heldAt, "result", now, "whenFalse");
                string time = s.Add("ToFloat", "double");
                s.Wire(now, "result", time, "value");
                s.Out("Time", time, "result");
            }

            string SetDouble(IWorldElement field, string from, string port)
            {
                string write = s.Add("SetField", "double");
                s.Bind(write, "field", field, "field<double>");
                s.Wire(from, port, write, "value");
                return write;
            }
            string SetRunning(bool value)
            {
                string write = s.Add("SetField", "bool");
                s.Bind(write, "field", running, "field<bool>");
                s.Set(write, "value", value);
                return write;
            }

            // Start goes on from what it read; a running one is left alone.
            string resume = s.Add("If");
            s.Wire(isRunning, "result", resume, "condition");
            s.Op("Start", resume);
            s.Exit("OnStart", resume, "true");
            string back = s.Add("Subtract", "double,double");
            s.Wire(clock, "seconds", back, "a");
            s.Wire(heldAt, "result", back, "b");
            string anchor = SetDouble(start, back, "result");
            s.Flow(resume, "false", anchor);
            string on = SetRunning(true);
            s.Flow(anchor, "next", on);
            s.Exit("OnStart", on, "next");

            // Stop keeps what it reads now; a stopped one is left alone.
            string pause = s.Add("If");
            s.Wire(isRunning, "result", pause, "condition");
            s.Op("Stop", pause);
            s.Exit("OnStop", pause, "false");
            string keep = SetDouble(held, since, "result");
            s.Flow(pause, "true", keep);
            string off = SetRunning(false);
            s.Flow(keep, "next", off);
            s.Exit("OnStop", off, "next");

            // Reset reads zero from now, running or not.
            string zero = s.Add("SetField", "double");
            s.Bind(zero, "field", held, "field<double>");
            s.Set(zero, "value", 0d);
            s.Op("Reset", zero);
            string restart = SetDouble(start, clock, "seconds");
            s.Flow(zero, "next", restart);
            s.Exit("OnReset", restart, "next");
        });

        // UTC, or this machine's time with IsLocal. Ours holds at the first or last date there is past either
        // end, where the package gives the empty date.
        On("FromUnixMilliseconds", s =>
        {
            bool? local = Flag(s, "IsLocal", false);
            if (local == null)
            {
                s.Fail("picks UTC or local time as it runs, which is not carried");
                return;
            }
            string date = s.Add("DateFromUnixMilliseconds");
            s.In("UnixMiliseconds", date, "milliseconds");
            if (local == false)
            {
                s.Out("", date, "date");
                return;
            }
            // A machine's zone is its own: only a Local script converts, as To Local Time says.
            string here = s.Add("DateToLocal");
            s.Wire(date, "date", here, "date");
            s.Out("", here, "local");
        });

        // The space the local user's body hangs in, else the world's root.
        On("LocalUserSpace", s =>
        {
            string root = s.Add("UserBodyRoot");
            s.Wire(s.Add("MachineUser"), "user", root, "user");
            string parent = s.Add("ParentSlot");
            s.Wire(root, "slot", parent, "slot");
            string space = s.Add("Fallback", "slot");
            s.Wire(parent, "parent", space, "value");
            s.Wire(s.Add("WorldRootSlot"), "root", space, "fallback");
            s.Out("", space, "result");
        });

        // The time on the user's wall clock. No one's time zone is on record here, so every user reads UTC,
        // marked as neither UTC nor local as the package marks it; the package adds the user's own offset.
        On("UserTime", s =>
        {
            string now = s.Add("DateNow");
            string plain = s.Add("SetDateKind");
            s.Wire(now, "date", plain, "date");
            s.Set(plain, "kind", 0d);
            s.Out("", plain, "result");
        });

        // One of the entries at random, read afresh each time; nothing with none.
        On("PickRandomObject", s =>
        {
            int count = s.Node.Refs("Operands").Count;
            if (s.Arg(0) is not { } type)
            {
                s.Fail("its type has no counterpart here");
                return;
            }
            if (count == 0)
            {
                s.OutConstant("", null);
                return;
            }
            if (count > 16)
            {
                s.Fail("picks from more than 16 entries, which is not carried");
                return;
            }
            string roll = s.Add("RandomInt");
            s.Set(roll, "min", 0d);
            s.Set(roll, "max", (double)count);
            if (count <= 4)
            {
                string pick = s.Add("Select", type);
                s.Wire(roll, "result", pick, "index");
                for (int i = 0; i < count; i++)
                    s.InAt("Operands", i, pick, Lettered(i));
                s.Out("", pick, "result");
                return;
            }
            string within = s.Add("Remainder", "int,int");
            s.Wire(roll, "result", within, "a");
            s.Set(within, "b", 4d);
            string group = s.Add("Divide", "int,int");
            s.Wire(roll, "result", group, "a");
            s.Set(group, "b", 4d);
            string outer = s.Add("Select", type);
            s.Wire(group, "result", outer, "index");
            for (int g = 0; g * 4 < count; g++)
            {
                string inner = s.Add("Select", type);
                s.Wire(within, "result", inner, "index");
                for (int i = 0; i < 4 && g * 4 + i < count; i++)
                    s.InAt("Operands", g * 4 + i, inner, Lettered(i));
                s.Wire(inner, "result", outer, Lettered(g));
            }
            s.Out("", outer, "result");
        });

        // A pattern of its own, or one of two picked as it runs, built into text piece by piece: its literal
        // parts and each value in turn, numbers to the decimals asked, dates by their pattern. Invariant
        // where the package used the machine's culture; an alignment like {0,8} is not carried. -xlinka
        On("FormatString", s =>
        {
            if (!PatternText(s, s.Node, "Format", out var single))
            {
                var choice = s.Feeder("Format");
                if (choice is not { Class: "ObjectConditional" } || !PatternText(s, choice, "OnTrue", out var whenTrue)
                    || !PatternText(s, choice, "OnFalse", out var whenFalse))
                {
                    s.Fail("formats with a pattern worked out as it runs, which is not carried");
                    return;
                }
                string pick = s.Add("Conditional", "string");
                s.InFrom(choice, "Condition", pick, "condition");
                Fill(s, whenTrue, pick, "whenTrue");
                Fill(s, whenFalse, pick, "whenFalse");
                s.Out("", pick, "result");
                return;
            }
            var (node, literal) = Pattern(s, single);
            if (node != null)
                s.Out("", node, "result");
            else
                s.OutConstant("", literal);
        });

        static void Fill(PackageNodeScope s, string? pattern, string node, string port)
        {
            var (built, literal) = Pattern(s, pattern);
            if (built != null)
                s.Wire(built, "result", node, port);
            else if (literal != null)
                s.Set(node, port, literal);
        }

        // A bool input as a constant: the default given when unwired, the value it holds, or null when it is
        // worked out as it runs.
        static bool? Flag(PackageNodeScope s, string input, bool unwired)
        {
            if (!s.Wired(input))
                return unwired;
            if (s.Feeder(input) is { Class: "ValueInput" or "GlobalValue" } constant)
                return constant.Value("Value") is IConvertible value && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return null;
        }

        // A pattern input that is a constant, through relays: false when it is worked out as it runs.
        static bool PatternText(PackageNodeScope s, PackageNode node, string input, out string? text)
        {
            text = null;
            string? id = node.Ref(input);
            for (int depth = 0; depth < 32 && id != null; depth++)
            {
                if (!s.Index.TryNode(id, out var feeder, out _))
                    return false;
                if (feeder.Class is "ValueObjectInput" or "GlobalValue")
                {
                    text = feeder.Value("Value") as string;
                    return true;
                }
                if (feeder.Class is not ("ObjectRelay" or "ValueRelay" or "ObjectCast"))
                    return false;
                id = feeder.Ref("Input");
            }
            return id == null;
        }

        // Our Concat building a constant pattern, or the text itself when there is no value in it. The package
        // gives an empty or missing pattern back as it is.
        static (string? Node, string? Literal) Pattern(PackageNodeScope s, string? format)
        {
            if (string.IsNullOrEmpty(format))
                return (null, format);
            var pieces = new List<Action<string, string>>();
            var literal = new StringBuilder();
            bool values = false;
            void Flush()
            {
                if (literal.Length == 0)
                    return;
                string text = literal.ToString();
                literal.Clear();
                pieces.Add((node, port) => s.Set(node, port, text));
            }
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if ((c == '{' || c == '}') && i + 1 < format.Length && format[i + 1] == c)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }
                int close = c == '{' ? format.IndexOf('}', i + 1) : -1;
                if (close < 0)
                {
                    literal.Append(c);
                    continue;
                }
                string inside = format[(i + 1)..close];
                int colon = inside.IndexOf(':');
                string head = colon >= 0 ? inside[..colon] : inside;
                int comma = head.IndexOf(',');
                if (!int.TryParse(comma >= 0 ? head[..comma] : head, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    literal.Append(c);
                    continue;
                }
                string spec = colon >= 0 ? inside[(colon + 1)..] : string.Empty;
                Flush();
                values = true;
                pieces.Add((node, port) => Piece(s, index, spec, node, port));
                i = close;
            }
            if (!values)
                return (null, literal.ToString());
            Flush();
            if (pieces.Count == 1)
                pieces.Add((node, port) => s.Set(node, port, string.Empty));
            var parts = new string[pieces.Count];
            Array.Fill(parts, "string");
            string joined = s.Add("Concat", string.Join(",", parts));
            for (int i = 0; i < pieces.Count; i++)
                pieces[i](joined, Lettered(i));
            return (joined, null);
        }

        // One value of a pattern, as text, into our port.
        static void Piece(PackageNodeScope s, int index, string spec, string node, string port)
        {
            string? type = ParameterType(s, index);
            if (type is null or "string")
            {
                s.InAt("Parameters", index, node, port);
                return;
            }
            if (type == "datetime")
            {
                string date = s.Add("FormatDate");
                s.InAt("Parameters", index, date, "date");
                s.Set(date, "format", spec.Length > 0 ? spec : "G");
                s.Wire(date, "text", node, port);
                return;
            }
            // A span by its own pattern, the package's default being the same as ours.
            if (type == "timespan")
            {
                string duration = s.Add("FormatDuration", "timespan,string");
                s.InAt("Parameters", index, duration, "span");
                s.Set(duration, "format", spec);
                s.Wire(duration, "text", node, port);
                return;
            }
            if (Decimals(spec) is { } decimals && type is "float" or "double")
            {
                string number = s.Add("FormatNumber", $"{type},int");
                s.InAt("Parameters", index, number, "value");
                s.Set(number, "decimals", (double)decimals);
                s.Wire(number, "result", node, port);
                return;
            }
            string text = s.Add("ToText", type);
            s.InAt("Parameters", index, text, "value");
            s.Wire(text, "result", node, port);
        }

        // The type of value entry i of the parameters holds: the type it was cast from on its way in, else what
        // the node feeding it gives.
        static string? ParameterType(PackageNodeScope s, int index)
        {
            var ids = s.Node.Refs("Parameters");
            string? id = index < ids.Count ? ids[index] : null;
            string? cast = null;
            for (int depth = 0; depth < 32 && s.Index.TryNode(id, out var node, out _); depth++)
            {
                if (node.Class is "ValueToObjectCast" or "ObjectCast")
                    cast ??= node.Arg(0);
                else if (node.Class is not ("ValueRelay" or "ObjectRelay" or "ContinuouslyChangingValueRelay" or "ContinuouslyChangingObjectRelay"))
                    return cast ?? ValueTypeOf(node);
                id = node.Ref("Input");
            }
            return cast;
        }
    }
}
