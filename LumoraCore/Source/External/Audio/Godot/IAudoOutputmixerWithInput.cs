using System;
using Lumora.Core.External.Audio.GenericOutputMixer;
namespace Lumora.Core.External.Audio.Godot;

public interface IAudioOutputMixerWithInput : IAudioMixer, IAudioCaputreProvider;
