// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The frequency of a MIDI note: A4 = 69 sits at the tuning, 440 Hz unless told otherwise, and each note a
// twelfth of an octave. A fraction of a note is cents: 60.5 is middle C and 50 cents.
[Node("Audio/Signal Processing/Note To Frequency", Description = "The frequency of a MIDI note: A4 = 69 sits at the tuning, 440 Hz unless told otherwise, and each note a twelfth of an octave.",
    Keywords = "midi, music note, pitch, semitone, hz")]
public sealed class NoteToFrequency : ValueNode
{
    [return: Port("hz")]
    public static float Run(float note, float tuning = 440f) => tuning * MathF.Pow(2f, (note - 69f) / 12f);

    [return: Port("hz")]
    public static float Run(int note, float tuning = 440f) => tuning * MathF.Pow(2f, (note - 69) / 12f);
}
