// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components.Import;

// Only includes materials Lumora actually has today. As more material providers
// land extend this enum and add the corresponding presets in ModelImportDialog. - xlinka
// Lit is first so it's the default: imported models/avatars are shaded by scene lighting
// (PBS metallic). Unlit is opt-in for flat/self-lit content. Lit and Unlit keep their numbers;
// every other kind starts as the PBS build and takes its look across (ModelImporter.ConvertMaterial).
public enum ModelMaterialType
{
    Lit,
    Unlit,
    Specular,
    DualSided,
    Triplanar,
    Toon,
    ToonOutline,
    Wireframe,
}

// How the two eyes are packed into one frame. A stereo video is one texture: half of it is the left
// eye and half is the right, and which half is which is not written down anywhere in the file, so the
// importer has to be told. Custom is for a layout the player sets up by hand afterwards. -xlinka
public enum StereoLayout
{
    None,
    Horizontal_LR,
    Vertical_LR,
    Horizontal_RL,
    Vertical_RL,
    Custom,
}

// The surface the frame is mapped onto. Flat is a screen; the two spheres are equirectangular, full
// or front half.
public enum VideoProjection
{
    Flat,
    Sphere360,
    Sphere180,
}

// What the video dialog decided, handed to whichever handler the platform registered. Stream is set
// when the source is a URL: nothing is downloaded first, the decoder pulls as it plays.
public sealed record VideoImportOptions(bool Stream, StereoLayout Layout, VideoProjection Projection);

// What an audio import brings in: the player panel, or a small orb that carries the clip and nothing else.
public enum AudioImportForm
{
    Player,
    Orb,
}

// Where an import keeps its meshes, materials and textures: on the parts that draw them, in one Assets slot
// under the model, or under the world's own Assets slot with everything else brought in that way.
public enum ModelAssetPlacement
{
    WithParts,
    UnderModel,
    World,
}

// What an imported model is solid with: nothing, its exact mesh, or a fitted box, ball or capsule per part.
public enum ModelColliderMode
{
    None,
    Mesh,
    Shapes,
}
