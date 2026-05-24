using Lumora.Core.Components.Audio;

namespace Lumora.Core.External.Audio.Godot;

public class GodotAudioPlayer3D : AudioPlayer3D
{
    public override void OnInit()
    {
        base.OnInit();
        AttenuationMode.Value = AttenuationModel.ATTENUATION_INVERSE_SQUARE_DISTANCE;
    }
    // no idea why this is a long but godot says it is so it is.
    public enum AttenuationModel : long
    {
        ATTENUATION_INVERSE_DISTANCE = 0,
        ATTENUATION_INVERSE_SQUARE_DISTANCE = 1,
        ATTENUATION_LOGARITHMIC = 2,
        ATTENUATION_DISABLED = 3
    }
    public Sync<AttenuationModel> AttenuationMode = new();
}