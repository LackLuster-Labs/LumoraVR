using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;

namespace Lumora.Core.Components;

/// <summary>
/// Drives slot transforms from tracking streams and publishes local transforms to streams.
/// </summary>
[ComponentCategory("Users")]
[DefaultUpdateOrder(-900000)]
public class TransformStreamDriver : Component
{
    private const float PositionThresholdSq = 0.0001f * 0.0001f;
    private const float RotationThreshold = 0.0001f;

    public StreamRef<Float3ValueStream> PositionStream { get; private set; } = new();
    public StreamRef<FloatQValueStream> RotationStream { get; private set; } = new();

    private bool _hasSent;

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var positionStream = PositionStream?.Target;
        var rotationStream = RotationStream?.Target;
        if (positionStream == null && rotationStream == null)
            return;

        var owner = positionStream?.Owner ?? rotationStream?.Owner;
        if (owner == null)
            return;

        if (owner.IsLocal)
        {
            UpdateLocalStreams(positionStream, rotationStream);
        }
        else
        {
            ApplyRemoteStreams(positionStream, rotationStream);
        }
    }

    private void UpdateLocalStreams(Float3ValueStream positionStream, FloatQValueStream rotationStream)
    {
        var position = Slot.LocalPosition.Value;
        var rotation = Slot.LocalRotation.Value;

        bool sent = false;

        if (positionStream != null)
        {
            if (!_hasSent || (position - positionStream.Value).LengthSquared > PositionThresholdSq)
            {
                positionStream.Value = position;
                positionStream.ForceUpdate();
                sent = true;
            }
        }

        if (rotationStream != null)
        {
            var current = rotationStream.Value;
            float dot = floatQ.Dot(rotation, current);
            float delta = 1.0f - (dot < 0 ? -dot : dot);

            if (!_hasSent || delta > RotationThreshold)
            {
                rotationStream.Value = rotation;
                rotationStream.ForceUpdate();
                sent = true;
            }
        }

        if (sent)
        {
            _hasSent = true;
        }
    }

    private void ApplyRemoteStreams(Float3ValueStream positionStream, FloatQValueStream rotationStream)
    {
        if (positionStream != null && positionStream.HasValidData)
        {
            var position = positionStream.Value;
            var current = Slot.LocalPosition.Value;
            if ((position - current).LengthSquared > PositionThresholdSq)
            {
                Slot.LocalPosition.SetValueSilently(position);
            }
        }

        if (rotationStream != null && rotationStream.HasValidData)
        {
            var rotation = rotationStream.Value;
            var current = Slot.LocalRotation.Value;
            float dot = floatQ.Dot(rotation, current);
            float delta = 1.0f - (dot < 0 ? -dot : dot);
            if (delta > RotationThreshold)
            {
                Slot.LocalRotation.SetValueSilently(rotation);
            }
        }
    }
}
