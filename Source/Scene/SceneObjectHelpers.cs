using System;
using System.Collections.Generic;
using LumoraVR.Source.Scene.Assets;
using LumoraVR.Source.Scene.RootObjects;

namespace LumoraVR.Source.Scene;

public static class SceneObjectHelpers
{
    public static readonly Dictionary<RootObjectType, ChildObjectType[]> AllowedChildTypes = new()
    {
        {
            RootObjectType.Avatar,
            [ ChildObjectType.Armature, ChildObjectType.MeshRenderer, ChildObjectType.HeadAndHandsAnimator, ChildObjectType.HumanoidAnimator ]
        },
    };
    public static readonly Dictionary<ChildObjectType, ChildObjectType[]> AllowedSubChildTypes = new()
    {
        {
            ChildObjectType.Armature,
            [ ChildObjectType.MeshRenderer, ChildObjectType.HeadAndHandsAnimator, ChildObjectType.HumanoidAnimator ]
        },
        //neither of the animators should have children
        {
            ChildObjectType.HumanoidAnimator,
            []
        },
        {
            ChildObjectType.HeadAndHandsAnimator,
            []
        },
    };
    public static readonly AssetProviderType[] StaticAssetTypes =
    [
        AssetProviderType.BasicMaterialProvider,
        AssetProviderType.NoiseTextureProvider,
    ];
    public static readonly AssetProviderType[] FileAssetTypes =
    [
        AssetProviderType.MeshFileProvider,
        AssetProviderType.ImageTextureProvider,
    ];
    public static bool CanInstantiate(this RootObjectType type) =>
        type switch
        {
            RootObjectType.Avatar => true,
            _ => false,
        };
    public static Type GetCorrespondingType(this RootObjectType type) =>
        type switch
        {
            RootObjectType.Avatar => typeof(Avatar),
            RootObjectType.PlayerCharacterController => typeof(PlayerCharacterController),
            _ => null,
        };
    public static ChildObjectType[] AllowedChildObjects(this RootObjectType type) => AllowedChildTypes.TryGetValue(type, out var list) ? list : [];
    public static ChildObjectType[] AllowedChildObjects(this ChildObjectType type) => AllowedSubChildTypes.TryGetValue(type, out var list) ? list : [];
    public static bool MatchesObject(this RootObjectType type, IRootObject obj) => obj.GetType() == type.GetCorrespondingType();
}
