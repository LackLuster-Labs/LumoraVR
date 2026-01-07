using Lumora.Core.HelioUI.Rendering;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Interface for HelioUI components that generate visual mesh data.
/// </summary>
public interface IHelioUIRenderer
{
    /// <summary>
    /// Generate mesh data for this UI element.
    /// </summary>
    /// <param name="mesh">The mesh to populate with vertex/index data.</param>
    void Generate(HelioUIMesh mesh);

    /// <summary>
    /// Whether this renderer needs to regenerate its mesh.
    /// </summary>
    bool IsDirty { get; }

    /// <summary>
    /// Mark this renderer as needing regeneration.
    /// </summary>
    void SetDirty();
}
