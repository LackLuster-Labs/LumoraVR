// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a blur processes the screen content behind it. Gaussian is the smooth frosted glass, Pixelate the block
// mosaic, Scatter an average of taps across a spread of the screen, ringed or filling a disc, that fades out
// where the surface meets what is behind it.
public enum BlurMode
{
    Gaussian,
    Pixelate,
    Scatter
}
