// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Nexus.Cloud;

namespace Lumora.Core.Components.Network;

public class SessionListEntry
{
    public string SessionId { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string HostUsername { get; set; } = null!;

    // The host's account, as the directory stamps it from their token. A LAN announcement carries none.
    public string HostUserId { get; set; } = "";

    public int ActiveUsers { get; set; }

    public int MaxUsers { get; set; }

    public SessionVisibility Visibility { get; set; }

    public Uri JoinUrl { get; set; } = null!;

    // Every address the host advertised, JoinUrl among them. Joins hand over all of them: the first is
    // usually the host's LAN card, which only answers inside the host's own network.
    public List<Uri> JoinUrls { get; set; } = new();

    public IReadOnlyList<Uri> AllJoinUrls
        => JoinUrls.Count > 0 ? JoinUrls : JoinUrl != null ? new[] { JoinUrl } : Array.Empty<Uri>();

    public string ThumbnailUrl { get; set; } = null!;

    public string? ThumbnailBase64 { get; set; }

    public List<string> Tags { get; set; } = new();

    // The host's build identity; empty from a build that did not publish one.
    public string VersionHash { get; set; } = "";

    public SessionSource Source { get; set; } = SessionSource.Local;

    public bool HasSpace => ActiveUsers < MaxUsers;

    public override string ToString()
    {
        return $"{Name} ({ActiveUsers}/{MaxUsers}) - {HostUsername}";
    }
}
