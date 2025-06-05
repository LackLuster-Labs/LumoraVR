namespace LumoraVR.Source.Scene;

public enum ChildObjectType
{
    //these should be ordered based on which should be loaded first
    //ie, the animators have a dependency on Armature, so they should
    //be loaded later than Armature

    None,
    //Node,
    MeshRenderer,
    Armature,
    HeadAndHandsAnimator,
    HumanoidAnimator,
}

public interface IChildObject : ISceneObject
{
    public bool Dirty { get; }
    public IRootObject Root { get; set; }
    public ISceneObject Parent { get; set; }

    /*
    public void PopulateRoot()
    {
        if (Root is not null) return;
        var parent = Self.GetParent();
        while (parent is not null)
        {
            if (parent is IRootObject rootObject)
            {
                Root = rootObject;
                return;
            }
            parent = parent.GetParent();
        }
    }
    */
}
