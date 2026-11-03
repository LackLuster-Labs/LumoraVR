// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The color with the amount added to one part. RGB adds to red, green and blue alike; hue, saturation and
// brightness are the HSV ones. Alpha is kept unless it is the part. The sum lands in range: 0 to 1 on a
// color; on an HDR color red, green, blue and brightness from 0 up and alpha 0 to 1; hue wraps round.
[Node("Color/Channels/Add To Channel", Description = "The color with the amount added to one part.",
    Keywords = "hue, shift hue, saturation, brightness, value, alpha, opacity, tint, red, green, blue")]
public sealed class AddToColorChannel : ValueNode
{
    [return: Port("color")]
    public static color Run(color value, ColorChannel channel, float amount)
        => ColorChannelEdit.Apply(value, channel, ColorChannelEdit.Op.Add, amount);

    [return: Port("color")]
    public static colorHDR Run(colorHDR value, ColorChannel channel, float amount)
        => ColorChannelEdit.Apply(value, channel, ColorChannelEdit.Op.Add, amount);
}
