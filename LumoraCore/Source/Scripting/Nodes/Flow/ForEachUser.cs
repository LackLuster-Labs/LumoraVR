// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Runs the body once for everyone in the session, the host included, with the user and their place in
// the list, then completed. The count is taken as flow arrives. -xlinka
[Node("Flow/Loops/For Each User", Capability.UsersRead, Description = "Runs the body once for everyone in the session, the host included, with the user and their place in the list, then completed.",
    Keywords = "everyone, all users, players, loop users, each person")]
public sealed class ForEachUser : FlowNode
{
    public override void Build(FlowBuilder f)
        => f.Each(EachSource.Users, f.Out("body"), f.Output<User>("user"), f.Output<int>("index"), f.Out("completed"));
}
