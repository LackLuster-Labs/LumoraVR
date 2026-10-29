// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Social;

// The colours a rendered bio paints links and code in, as the palette stores them (linear). Rich text
// colours land in the same per-character list as the text's own colour, so they go in unconverted.
public readonly struct BioInks
{
    public readonly color Link;
    public readonly color Code;
    public readonly color CodeMark;

    public BioInks(color link, color code, color codeMark)
    {
        Link = link;
        Code = code;
        CodeMark = codeMark;
    }
}
