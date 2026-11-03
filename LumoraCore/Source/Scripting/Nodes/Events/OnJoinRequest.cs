// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone asking the host to join, once they have proved who they are and passed the session's own rules.
// Heard on the host only, by a world script the host has let check joins this session. userId is their
// account, empty for a guest; machineId is their machine as Users/Machine ID gives it. The request goes to
// Allow Join, Refuse Join and Set Join Role. A refusal from any check turns them away; otherwise they come in
// with the role a check chose, or the session's usual one. A check that has run out of things to do without
// a word lets the session decide, and one still busy after 15 s turns them away. -xlinka
[Node("World/Session/On Join Request", Capability.UsersRead, Description = "Someone asking the host to join, once they have proved who they are and passed the session's own rules.")]
public sealed class OnJoinRequest : EventNode<JoinRequested>
{
}
