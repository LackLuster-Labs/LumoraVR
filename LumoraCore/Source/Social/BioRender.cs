// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Social;

// A bio after BioMarkdown: the rich text a Text with RichText on draws, the characters it shows (for sizing),
// and the web addresses it links to, each in full, for the link pills under it.
public sealed class BioRender
{
    public static readonly BioRender Empty = new();

    public string Rich { get; init; } = string.Empty;
    public string Plain { get; init; } = string.Empty;
    public IReadOnlyList<Uri> Links { get; init; } = Array.Empty<Uri>();
}
