// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The color the eye sees in light of one wavelength in nanometres, the same power at every wavelength:
// violet near 430, blue near 470, green near 530, yellow near 570 and red from 620, dimming to black toward
// 380 and 780. Linear channels clamped to 0 to 1, since pure spectral colors lie past what a screen shows;
// To sRGB it for a light or material tint. NaN gives black.
[Node("Color/Wavelength Color", Description = "Converts a wavelength of visible light in nanometres into a colour.",
    Keywords = "spectrum, rainbow, nanometers, light")]
public sealed class WavelengthColor : ValueNode
{
    [return: Port("color")]
    public static color Run(float nanometers = 550f) => VisibleSpectrum.Wavelength(nanometers);
}
