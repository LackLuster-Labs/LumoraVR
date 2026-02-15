using Lumora.Core.Networking.Streams;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Drives slot transforms from tracking streams and publishes local transforms to streams.
/// - Uses SyncRef for stream references (synced over network)
/// - For local user: reads slot position and writes to stream
/// - For remote user: reads stream and writes to slot position
/// </summary>
[ComponentCategory("Users")]
[DefaultUpdateOrder(-10000)]
public class TransformStreamDriver : Component
{
    // Synced references to the streams - these sync over the network
    // so clients can resolve them by RefID
    // Initialized by Worker.InitializeSyncMembers() via reflection
    public readonly SyncRef<Float3ValueStream> PositionStream = null!;
    public readonly SyncRef<FloatQValueStream> RotationStream = null!;

    private int _debugCounter = 0;
    private bool _loggedStreamInfo = false;

    /// <summary>
    /// Get the user that owns these streams.
    /// </summary>
    public User? User
    {
        get
        {
            var posTarget = PositionStream?.Target;
            if (posTarget != null) return posTarget.Owner;

            var rotTarget = RotationStream?.Target;
            if (rotTarget != null) return rotTarget.Owner;

            return null;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Debug logging every ~2 seconds
        _debugCounter++;
        if (!_loggedStreamInfo && _debugCounter > 120)
        {
            _debugCounter = 0;
            var posState = PositionStream?.State.ToString() ?? "null";
            var posRefID = PositionStream?.Value.ToString() ?? "null";
            var posTarget = PositionStream?.Target;
            var user = User;
            AquaLogger.Log($"[TSD] {Slot.SlotName.Value}: PosState={posState} RefID={posRefID} Target={posTarget != null} User={user?.UserName?.Value ?? "null"} IsLocal={user?.IsLocal}");
            if (user != null)
                _loggedStreamInfo = true;
        }

        var currentUser = User;
        if (currentUser == null)
            return;

        if (currentUser.IsLocal)
        {
            // Local user: read slot transform, write to streams
            UpdateLocalStreams();
        }
        else
        {
            // Remote user: read streams, write to slot transform
            ApplyRemoteStreams();
        }
    }

    /// <summary>
    /// For local user: push slot transform to streams for network transmission.
    /// </summary>
    private void UpdateLocalStreams()
    {
        var positionStream = PositionStream?.Target;
        var rotationStream = RotationStream?.Target;

        // Write position to stream
        if (positionStream != null && positionStream.IsLocal)
        {
            var position = Slot.LocalPosition.Value;
            positionStream.Value = position;
        }

        // Write rotation to stream
        if (rotationStream != null && rotationStream.IsLocal)
        {
            var rotation = Slot.LocalRotation.Value;
            rotationStream.Value = rotation;
        }
    }

    /// <summary>
    /// For remote user: apply stream data to slot transform.
    /// </summary>
    private void ApplyRemoteStreams()
    {
        var positionStream = PositionStream?.Target;
        var rotationStream = RotationStream?.Target;

        // Apply position from stream
        if (positionStream != null && positionStream.HasValidData)
        {
            var position = positionStream.Value;

            // Skip invalid values
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
                return;

            Slot.LocalPosition.Value = position;
        }

        // Apply rotation from stream
        if (rotationStream != null && rotationStream.HasValidData)
        {
            var rotation = rotationStream.Value;

            // Skip invalid values
            if (!float.IsFinite(rotation.x) || !float.IsFinite(rotation.y) ||
                !float.IsFinite(rotation.z) || !float.IsFinite(rotation.w))
                return;

            // Skip zero-length quaternion
            float lengthSq = rotation.x * rotation.x + rotation.y * rotation.y +
                             rotation.z * rotation.z + rotation.w * rotation.w;
            if (lengthSq < 0.0001f)
                return;

            Slot.LocalRotation.Value = rotation;
        }
    }
}
