namespace Lumora.Core.Assets;

/// <summary>
/// Material property block - per-instance material property overrides.
/// Allows per-instance overrides without creating duplicate materials.
/// </summary>
public class MaterialPropertyBlock : SharedMaterialBase<IMaterialPropertyBlockHook>
{
    // MaterialPropertyBlock inherits all property-setting methods from SharedMaterialBase
    // No additional functionality needed - it's a lighter-weight version of Material
    // that doesn't support shader changes, render queue, or tags.
}
