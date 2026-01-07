using System.IO;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components.Assets;

/// <summary>
/// Synchronized shader uniform parameter value.
/// </summary>
public sealed class ShaderUniformParam : SyncElement
{
    public override SyncMemberType MemberType => SyncMemberType.Object;
    public readonly Sync<string> Name = new();
    public readonly Sync<ShaderUniformType> Type = new();
    public readonly Sync<float4> Value = new();
    public readonly Sync<float2> Range = new();
    public readonly Sync<bool> HasRange = new();
    public readonly Sync<bool> IsColor = new();
    public readonly AssetRef<TextureAsset> Texture = new();

    public override void Initialize(World world, IWorldElement parent)
    {
        base.Initialize(world, parent);
        SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        // Shader uniform parameters sync their fields as separate SyncElements.
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        // No payload for the container element.
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        // Shader uniform parameters sync their fields as separate SyncElements.
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        // No payload for the container element.
    }

    protected override void InternalClearDirty()
    {
        // No internal dirty state to clear.
    }

    public override object? GetValueAsObject() => Name.Value;
}
