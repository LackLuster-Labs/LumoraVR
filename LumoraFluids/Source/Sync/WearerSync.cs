// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking.Sync;

namespace Lumora.Fluids;

// A field only the person whose user root holds it may write from the wire. It can never be driven either: a drive
// is a local link that would silence the wearer on one machine while every inbound write is ignored for it, so the
// member says no to the link itself (LinkBase refuses a non-drivable target). -xlinka
public class WearerSync<T> : Sync<T>
{
    public WearerSync()
    {
        Arm();
    }

    public WearerSync(T init) : base(init)
    {
        Arm();
    }

    private void Arm()
    {
        MarkNonDrivable();
        MarkInboundRules();
    }

    protected override MessageValidity ValidateInboundWrite(
        BinaryMessageBatch inboundMessage, BinaryReader reader, List<ValidationGroup.Rule> rules)
    {
        var validity = base.ValidateInboundWrite(inboundMessage, reader, rules);
        if (validity != MessageValidity.Valid)
            return validity;
        return WearerRule.CheckSender(this, inboundMessage?.SenderUser);
    }
}
