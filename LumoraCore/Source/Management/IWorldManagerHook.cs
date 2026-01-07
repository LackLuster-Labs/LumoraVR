namespace Lumora.Core.Management;

/// <summary>
/// Interface for WorldManager hooks (platform-specific world container).
/// Platform hook interface for world management.
/// </summary>
public interface IWorldManagerHook
{
    WorldManager Owner { get; }

    void Initialize(WorldManager owner, object sceneRoot);
    void Destroy();
}
