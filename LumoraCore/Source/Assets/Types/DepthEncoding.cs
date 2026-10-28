// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// How a depth picture stores distance. Grayscale is white near and black far; RGBSpectrum runs the hue from
// red near round through the colours to far.
public enum DepthEncoding
{
    Grayscale,
    RGBSpectrum
}
