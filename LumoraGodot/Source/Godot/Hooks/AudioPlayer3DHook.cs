using System;
using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Audio;
using Lumora.Core.External.Audio.Godot;
using Lumora.Core.Math;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Hook for AudioPlayer3D component → Godot AudioStreamPlayer3D.
/// Platform AudioStreamPlayer3D hook for Godot.
/// </summary>
public class AudioPlayer3DHook : ComponentHook<AudioPlayer3D>
{
    private AudioStreamPlayer3D audioPlayer3D = null!;
    public override void ApplyChanges()
    {
        if(audioPlayer3D is null) return;
        audioPlayer3D.Bus = Enum.GetName(Owner.Category.Value);
        if(Owner is GodotAudioPlayer3D godotAudioPlayer3D)
        {
            audioPlayer3D.AttenuationModel = (AudioStreamPlayer3D.AttenuationModelEnum)godotAudioPlayer3D.AttenuationMode.Value;
        }
    }
    public override void Initialize()
    {
        base.Initialize();
        audioPlayer3D = new();
        attachedNode.AddChild(audioPlayer3D);
    }
}