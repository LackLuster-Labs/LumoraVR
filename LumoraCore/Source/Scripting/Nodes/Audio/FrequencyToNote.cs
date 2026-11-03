// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The MIDI note nearest a frequency (A4 = 69, tuned to 440 Hz unless told otherwise; middle C is 60)
// and how far off it the frequency is, -50 to +50 cents. A frequency that is not a positive number is note
// -1.
[Node("Audio/Signal Processing/Frequency To Note", Description = "The MIDI note nearest a frequency (A4 = 69, tuned to 440 Hz unless told otherwise; middle C is 60) and how far off it the frequency is, -50 to +50 cents.",
    Keywords = "midi, music note, pitch, semitone, hz")]
public sealed class FrequencyToNote : ValueNode
{
    [return: Port("note")]
    public static int Run(float hz, out float cents, float tuning = 440f) => ScriptAudioAnalysis.NearestNote(hz, tuning, out cents);
}
