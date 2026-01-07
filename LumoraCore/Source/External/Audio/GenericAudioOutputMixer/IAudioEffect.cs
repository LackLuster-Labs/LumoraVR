using System;
using System.Collections.Generic;

namespace Lumora.Core.External.GenericAudioOutputMixer;

public interface IAudioEffect : IDisposable
{
    public abstract record Value;
    public record ValueI32(int value) : Value
    {
        int Value = value;
    };
    public record ValueString(string value) : Value
    {
        string Value = value;
    };
    public string Name { get; }
    public IReadOnlyDictionary<string, Value> Config { get; }
}

