using System;
using System.Diagnostics.CodeAnalysis;
namespace Lumora.Core.External.Audio.Godot;
#nullable enable
public interface IAudioCaputreProvider
{
    public bool TryGetCapureEffect([NotNullWhen(true)] out IAudioCapureEffect? effect);
    public void InitializeInput();
}
