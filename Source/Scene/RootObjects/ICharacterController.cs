using LumoraVR.Source.Input;
using Godot;

namespace LumoraVR.Source.Scene.RootObjects;

public interface ICharacterController : IRootObject
{
    public Transform3D GlobalTransform { get; set; }
    public Transform3D Transform { get; set; }
    public Vector3 GetLimbPosition(IInputProvider.InputLimb limb);
    public Quaternion GetLimbRotation(IInputProvider.InputLimb limb);
    public (Vector3 pos, Quaternion rot) GetLimbTransform(IInputProvider.InputLimb limb) => (GetLimbPosition(limb), GetLimbRotation(limb));
    public Transform3D GetLimbTransform3D(IInputProvider.InputLimb limb) => new(new Basis(GetLimbRotation(limb)), GetLimbPosition(limb));
}
