using Godot;
using Lumora.Core.Components;
using Lumora.Core.Components.Audio;
using Lumora.Core.External.Audio.Godot;
using Lumora.Core.Math;

namespace Lumora.Godot.Hooks;

/// <summary>
/// Hook for Camera component → Godot Camera3D.
/// Platform camera hook for Godot.
/// </summary>
public class AudioPlayer3DHook : ComponentHook<AudioPlayer3D>
{
    private AudioStreamPlayer3D audioPlayer3D = null!;
    public override void ApplyChanges()
    {
        if(Owner.Steam is null) return;
        if(audioPlayer3D is null) return;
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