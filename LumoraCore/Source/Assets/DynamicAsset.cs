using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for dynamic (runtime-generated) assets.
/// Dynamic assets are created procedurally and don't have external URLs.
/// They exist only in memory during the session.
/// </summary>
public abstract class DynamicAsset : Asset
{
    public override int ActiveRequestCount => 0; // Dynamic assets don't track requests

    public override void Unload()
    {
        // Dynamic assets typically don't need explicit unloading
        // Override in derived classes if cleanup is needed
    }
}
