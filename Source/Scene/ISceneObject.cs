using Godot;

namespace LumoraVR.Source.Scene;

public interface ISceneObject
{
    public Node Self { get; }
    public bool EnsureValidReference(ref GodotObject obj)
    {
        if (GodotObject.IsInstanceValid(obj)) return true;
        obj = null;
        return false;
    }
    public void SetPlayerAuthority(int id);
    public void Initialize(Godot.Collections.Dictionary<string, Variant> data);
    public void AddChildObject(ISceneObject obj);
}
