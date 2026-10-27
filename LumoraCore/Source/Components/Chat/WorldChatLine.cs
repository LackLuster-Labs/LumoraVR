// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Chat;

// One line of a world's chat as this machine saw it arrive. The name, colour and account are taken when it
// lands, so the history still says who it was after they leave.
public sealed class WorldChatLine
{
    public WorldChatLine(User? user, string accountId, string name, in color ink, string text, int seq, double time, bool own)
    {
        User = user;
        AccountId = accountId;
        Name = name;
        Ink = ink;
        Text = text;
        Seq = seq;
        Time = time;
        Own = own;
    }

    public User? User { get; }
    public string AccountId { get; }
    public string Name { get; }
    public color Ink { get; }
    public string Text { get; }
    public int Seq { get; }

    // World.Time.TotalTime of the world it arrived in.
    public double Time { get; }

    // Sent from this machine.
    public bool Own { get; }

    // Someone this machine's account blocked, then or since: their lines are not shown here.
    public bool IsBlocked => !Own && !string.IsNullOrEmpty(AccountId) && Engine.Current?.Social?.IsBlocked(AccountId) == true;
}
