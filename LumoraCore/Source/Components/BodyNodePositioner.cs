using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Logging;

namespace Lumora.Core.Components;

/// <summary>
/// Positions a slot based on VR tracking data from a specific body node.
/// </summary>
[ComponentCategory("Tracking")]
[DefaultUpdateOrder(-10000)] // Update before everything else
public class BodyNodePositioner : Component
{
    /// <summary>
    /// Target user to get tracking from.
    /// </summary>
    public SyncRef<User> User { get; private set; }

    /// <summary>
    /// Which body node to track.
    /// </summary>
    public Sync<BodyNode> Node { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        User = new SyncRef<User>(this, null);
        Node = new Sync<BodyNode>(this, BodyNode.Head);

        // Initialize sync members created in OnAwake
        InitializeNewSyncMembers();

        Logger.Log($"BodyNodePositioner: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();
        Logger.Log($"BodyNodePositioner: Started tracking {Node.Value} for user '{User.Target?.UserName.Value ?? "none"}'");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var targetUser = User.Target;
        if (targetUser != null && targetUser == World?.LocalUser)
        {
            // Get tracking data from input interface
            var inputInterface = Engine.Current?.InputInterface;
            if (inputInterface != null)
            {
                var trackedDevice = inputInterface.GetBodyNode(Node.Value);
                if (trackedDevice != null)
                {
                    // Update slot position and rotation from tracking
                    Slot.LocalPosition.Value = trackedDevice.Position;
                    Slot.LocalRotation.Value = trackedDevice.Rotation;
                }
            }
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        Logger.Log($"BodyNodePositioner: Destroyed on slot '{Slot?.SlotName.Value}'");
    }
}
