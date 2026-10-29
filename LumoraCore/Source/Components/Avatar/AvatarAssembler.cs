// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

// The single per-world avatar builder. BuildAvatar(userRoot) wires up a
// joining user (body-node tracking slots, collider, character controller,
// locomotion, head output, nameplate, avatar manager). Also serves as the
// AvatarEquipManager.SocketFiller, supplying default head/hand visuals when
// no custom avatar covers a body node. All behavior is gated by toggles so
// a world can opt parts in or out. - xlinka
[ComponentCategory("Users/Avatar")]
public class AvatarAssembler : Component, IAvatarAssembler, IAvatarSocketFiller
{
    public const float DefaultHeight = 1.7f;
    public const float HeadSize = 0.2f;
    public const float HandSize = 0.08f;

    public readonly Sync<bool> SetupLocomotion = new();
    public readonly Sync<bool> SetupCollider = new();
    public readonly Sync<bool> SetupHeadOutput = new();
    public readonly Sync<bool> SetupNameplate = new();
    public readonly Sync<bool> SetupHandTools = new();
    public readonly Sync<bool> FillEmptySockets = new();

    public readonly Sync<colorHDR> DefaultTint = new();
    public readonly Sync<bool> HideHeadFromLocalUser = new();

    // Grip offset from the tracked controller pose to the hand body node.
    // Hands are a separate body node from controllers so avatar hand meshes
    // equip with a correct wrist pose instead of raw controller pose.
    public readonly Sync<float3> HandGripPositionOffset = new();
    public readonly Sync<floatQ> HandGripRotationOffset = new();

    // Fired after a user's avatar setup completes. (user, avatarSlot)
    public event Action<User, Slot> OnAvatarSetupFinish = null!;

    public override void OnInit()
    {
        base.OnInit();
        SetupLocomotion.Value = true;
        SetupCollider.Value = true;
        SetupHeadOutput.Value = true;
        SetupNameplate.Value = true;
        SetupHandTools.Value = true;
        FillEmptySockets.Value = true;
        DefaultTint.Value = new colorHDR(0.8f, 0.8f, 0.8f, 1f);
        HideHeadFromLocalUser.Value = true;
        HandGripPositionOffset.Value = float3.Zero;
        HandGripRotationOffset.Value = floatQ.Identity;
    }

    // IAvatarAssembler

    // Convenience for the host's own avatar: scaffold + owner equipment in one pass (the host owns
    // everything anyway). Per-peer, the authority calls BuildSharedScaffold and each owning client calls
    // BuildOwnedEquipment separately. -xlinka
    public void BuildAvatar(UserRoot userRoot)
    {
        BuildSharedScaffold(userRoot);
        BuildOwnedEquipment(userRoot);
    }

    // Host-built, replicated to everyone: body-node tracking, root transform stream, collider, locomotion,
    // head output, avatar manager + nameplate. The hand tool rig + context menu are NOT here - those are
    // built by the OWNING peer (see BuildOwnedEquipment) so a user owns its own equipment in its own byte. -xlinka
    public void BuildSharedScaffold(UserRoot userRoot)
    {
        if (userRoot == null || userRoot.Slot == null)
        {
            LumoraLogger.Error("AvatarAssembler: BuildSharedScaffold called with null user root");
            return;
        }

        var user = userRoot.ActiveUser;
        var userSlot = userRoot.Slot;

        var bodyNodes = userSlot.AddSlot("Body Nodes");
        userRoot.TrackingSpaceRoot.Target = bodyNodes;
        var headSlot = BuildHeadNode(bodyNodes, user);
        BuildControllerNode(bodyNodes, user, Chirality.Left);
        BuildControllerNode(bodyNodes, user, Chirality.Right);

        // One slot per connected tracker, under the user root. The component refuses to run for a
        // non-local user, so which world this is answers itself.
        userSlot.AttachComponent<TrackerSlotManager>();

        // Per-user finger pose plumbing: a streamed source. The owner writes local
        // tracking into a finger stream; remote peers read it. Desktop and untracked
        // users resolve to no live data, so their hands rest in the avatar's authored
        // pose instead of getting synthesized curl. -xlinka
        var fingerInfo = userSlot.AttachComponent<UserHandPoseInfo>();
        var fingerStream = userSlot.AttachComponent<HandPoseStreamManager>();
        fingerInfo.HandPoseSource.Target = fingerStream;

        // Per-user face transport. The owner writes its head device's eye data and its mouth device's lip
        // weights into streams; remote peers read them. Inert until a tracker feeds those devices, so an
        // untracked user's eyes fall back to procedural saccade/blink and the mouth simply rests. -xlinka
        userSlot.AttachComponent<EyeStreamManager>();
        userSlot.AttachComponent<MouthStreamManager>();

        // Stream the user-root transform (the walking/locomotion translation) exactly like the body nodes.
        // Without this, root movement can only ride the permission-gated delta channel and a joined user stays
        // frozen in place for everyone. Local user: locomotion drives the root slot, this driver pushes it to
        // the (permission-free) Root stream. Remote observers: this driver pulls the stream back onto the slot. -xlinka
        if (user != null)
        {
            var rootStreamDriver = userSlot.AttachComponent<TransformStreamDriver>();
            user.GetTrackingStreams(BodyNode.Root, out var rootPosStream, out var rootRotStream);
            rootStreamDriver.PositionStream.Target = rootPosStream;
            rootStreamDriver.RotationStream.Target = rootRotStream;
        }

        if (SetupCollider.Value)
        {
            var collider = userSlot.AttachComponent<CapsuleCollider>();
            collider.Type.Value = ColliderType.CharacterController;
            collider.Height.Value = 1.8f;
            collider.Radius.Value = 0.275f;
            collider.Offset.Value = new float3(0, 0.9f, 0);
            var characterController = userSlot.AttachComponent<CharacterController>();
            // Body follows the head's ground projection so the collider stays
            // under the player in room-scale, not at the root origin.
            characterController.HeadReference.Target = headSlot;
        }

        // Head and hands as dynamic-bone colliders, so this user's body can push other people's hair
        // and tails around. Costs nothing until a chain in the world asks for player collision, and
        // carries this user's own "keep my body out of other people's bones" toggle. -xlinka
        userSlot.AttachComponent<DynamicBonePlayerColliders>();
        userSlot.AttachComponent<Lumora.Core.Components.Touch.UserContactSenders>();

        // The two-grip self-scale goes wherever locomotion does: both move the user's own body.
        if (SetupLocomotion.Value)
        {
            userSlot.AttachComponent<LocomotionController>();
            userSlot.AttachComponent<SelfScaleGesture>();
        }

        if (SetupHeadOutput.Value)
            userSlot.AttachComponent<HeadOutput>();

        var avatarSlot = userSlot.AddSlot("Avatar");
        var avatarManager = avatarSlot.AttachComponent<AvatarEquipManager>();
        avatarManager.UserRoot.Target = userRoot;
        // The nameplate is composed per peer by NameplateManager under a local slot the equip manager
        // puts up. Nothing about it replicates, so all the scaffold hands over is the toggle.
        avatarManager.AutoAddNameBadge.Value = SetupNameplate.Value;
        if (user != null)
            avatarManager.BadgeText.Value = user.UserName.Value ?? string.Empty;
        if (FillEmptySockets.Value)
        {
            avatarManager.SocketFiller.Target = this;
            avatarManager.FillEmptySockets();
        }

        OnAvatarSetupFinish?.Invoke(user!, avatarSlot!);
        LumoraLogger.Debug($"AvatarAssembler: Built scaffold for '{user?.UserName?.Value ?? "(null)"}'");
    }

    // Built by the OWNING peer under its own (replicated) scaffold, minted in the owner's RefID byte so the
    // owner owns the writes. Idempotent + safe to re-enter while the scaffold finishes syncing in or the
    // join-window ownership link is still settling. Each peer builds only its OWN equipment - a remote user's
    // tool rig + menu replicate in from that user. -xlinka
    public void BuildOwnedEquipment(UserRoot userRoot)
    {
        if (userRoot == null || userRoot.Slot == null)
            return;

        var userSlot = userRoot.Slot;
        var bodyNodes = userSlot.FindChild("Body Nodes", recursive: false);
        if (bodyNodes == null)
            return; // scaffold not present/synced yet - caller retries

        if (SetupHandTools.Value)
        {
            var leftController = bodyNodes.FindChild("LeftController", recursive: false);
            var rightController = bodyNodes.FindChild("RightController", recursive: false);

            EnsureHandTool(leftController, Chirality.Left);
            EnsureHandTool(rightController, Chirality.Right);

            EnsureTouchProbes(leftController, userRoot, Chirality.Left);
            EnsureTouchProbes(rightController, userRoot, Chirality.Right);
        }

        // Per-user radial context menu + the avatar equip/dequip actions it offers when pointing at an avatar.
        if (userSlot.FindChild("Context Menu", recursive: false) == null)
        {
            var menuSlot = userSlot.AddSlot("Context Menu");
            menuSlot.AttachComponent<UI.ContextMenuSystem>();
            menuSlot.AttachComponent<AvatarContextActions>();
            menuSlot.AttachComponent<UserContextActions>();
            menuSlot.AttachComponent<LocomotionContextActions>();
            menuSlot.AttachComponent<InspectorContextActions>();
            menuSlot.AttachComponent<Interaction.ToolContextActions>();
            menuSlot.AttachComponent<Interaction.GrabbedObjectContextActions>();
            menuSlot.AttachComponent<Interaction.ItemRemovalContextActions>();
            menuSlot.AttachComponent<UserScaleContextActions>();
            menuSlot.AttachComponent<BodyCalibrationContextActions>();
            menuSlot.AttachComponent<HandViewfinder>();
            menuSlot.AttachComponent<UndoManager>();
            menuSlot.AttachComponent<UndoContextActions>();
        }
    }

    // Attach a hand tool under the given controller body node, once. Skips if one is already there (a retry,
    // or the replicated rig already synced). -xlinka
    private void EnsureHandTool(Slot controller, Chirality side)
    {
        if (controller == null)
            return;
        if (controller.FindChild("HandTool", recursive: false) != null)
            return;

        var toolSlot = controller.AddSlot("HandTool");
        var tool = toolSlot.AttachComponent<HandTool>();
        tool.Side.Value = side;
    }

    // Touch probes for one hand: an index fingertip that presses physical controls, and a beam probe that
    // lets the same controls be worked from across the room (and at all on desktop, where there is no
    // fingertip to put through anything).
    //
    // Built into a LOCAL slot on purpose. A probe only ever runs on the machine of the user driving it,
    // and everything it produces reaches other peers as the TARGET's synced state - so replicating the
    // probe itself would cost bandwidth and RefID space to tell everyone about a component none of them
    // will ever run. Local also means it never lands in a save. -xlinka
    private static void EnsureTouchProbes(Slot controller, UserRoot userRoot, Chirality side)
    {
        if (controller == null || controller.IsDestroyed)
            return;
        if (controller.FindChild(TouchProbeSlotName, recursive: false) != null)
            return;

        var user = userRoot?.ActiveUser;
        var probeSlot = controller.AddLocalSlot(TouchProbeSlotName);
        probeSlot.Persistent.Value = false;

        var fingertip = probeSlot.AttachComponent<Touch.FingertipProbe>();
        fingertip.Hand.Value = side;
        fingertip.Finger.Value = FingerType.Index;
        if (user != null)
            fingertip.Owner.Target = user;

        var remote = probeSlot.AttachComponent<Touch.RemoteProbe>();
        remote.Hand.Value = side;
        if (user != null)
            remote.Owner.Target = user;

        // The same fingertip against Helio panels: hover, press and release on whatever key or button
        // the finger goes through, off the probe's tip above.
        var canvasTouch = probeSlot.AttachComponent<Touch.CanvasTouchSource>();
        canvasTouch.Hand.Value = side;
        canvasTouch.HandRoot.Target = controller;
        if (user != null)
            canvasTouch.Owner.Target = user;
    }

    private const string TouchProbeSlotName = "Touch Probes";

    private Slot BuildHeadNode(Slot bodyNodes, User user)
    {
        var headSlot = bodyNodes.AddSlot("Head");
        headSlot.LocalPosition.Value = new float3(0f, DefaultHeight, 0f);

        var positioner = headSlot.AttachComponent<TrackedDevicePositioner>();
        positioner.AutoBodyNode.Value = BodyNode.Head;

        if (user != null)
        {
            var stream = headSlot.AttachComponent<TransformStreamDriver>();
            user.GetTrackingStreams(BodyNode.Head, out var posStream, out var rotStream);
            stream.PositionStream.Target = posStream;
            stream.RotationStream.Target = rotStream;
        }

        return headSlot;
    }

    // Controllers and hands are distinct body nodes. The controller slot is
    // device-tracked (positioner + streams) and carries the interaction tool.
    // The hand slot rides under it with a grip offset and owns its own
    // AvatarSocket, so avatar hand meshes equip a wrist pose - and a
    // full-hand piece can declare the controller node mutually exclusive to
    // suppress controller visuals. - xlinka
    private void BuildControllerNode(Slot bodyNodes, User user, Chirality side)
    {
        bool isLeft = side == Chirality.Left;
        var controllerNode = isLeft ? BodyNode.LeftController : BodyNode.RightController;
        var handNode = isLeft ? BodyNode.LeftHand : BodyNode.RightHand;

        var controller = bodyNodes.AddSlot(isLeft ? "LeftController" : "RightController");
        controller.LocalPosition.Value = new float3(isLeft ? -0.25f : 0.25f, 1.0f, 0f);
        controller.LocalRotation.Value = isLeft
            ? floatQ.Euler(MathF.PI / 2f, -MathF.PI / 2f, 0f)
            : floatQ.Euler(-MathF.PI / 2f, -MathF.PI / 2f, 0f);

        var positioner = controller.AttachComponent<TrackedDevicePositioner>();
        positioner.AutoBodyNode.Value = controllerNode;

        if (user != null)
        {
            var stream = controller.AttachComponent<TransformStreamDriver>();
            user.GetTrackingStreams(controllerNode, out var posStream, out var rotStream);
            stream.PositionStream.Target = posStream;
            stream.RotationStream.Target = rotStream;
        }

        var hand = controller.AddSlot(isLeft ? "LeftHand" : "RightHand");
        hand.LocalPosition.Value = HandGripPositionOffset.Value;
        hand.LocalRotation.Value = HandGripRotationOffset.Value;
        var handObjectSlot = hand.AttachComponent<AvatarSocket>();
        handObjectSlot.Node.Value = handNode;

        var handTracking = hand.AttachComponent<HandTrackingDriver>();
        handTracking.Side.Value = side;
        handTracking.PositionOffset.Value = HandGripPositionOffset.Value;
        handTracking.RotationOffset.Value = HandGripRotationOffset.Value;
        if (user != null)
        {
            var handStream = hand.AttachComponent<TransformStreamDriver>();
            user.GetTrackingStreams(handNode, out var handPosStream, out var handRotStream);
            handStream.PositionStream.Target = handPosStream;
            handStream.RotationStream.Target = handRotStream;
        }

        // The HandTool rig is NOT built here anymore - it's owner-built in BuildOwnedEquipment so each user
        // owns its own equipment in its own byte. -xlinka
    }

    // IAvatarSocketFiller

    // Default pieces are EQUIPPED avatar objects (not permanent fixtures) so
    // equipping a real avatar dequips them and DiscardOnDequip removes
    // the visuals automatically. - xlinka
    public Task FillSocket(BodyNode node, AvatarEquipManager manager, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || manager == null || manager.IsDestroyed)
            return Task.CompletedTask;

        Slot piece;
        switch (node)
        {
            case BodyNode.Head:
                piece = BuildDefaultHead(manager);
                break;
            case BodyNode.LeftHand:
            case BodyNode.RightHand:
                piece = BuildDefaultHand(manager, node);
                break;
            default:
                return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            piece.Destroy();
            return Task.CompletedTask;
        }

        if (!manager.Equip(piece, isManualEquip: false, forceDestroyOld: false, isFillingEmptySlot: true))
        {
            LumoraLogger.Warn($"AvatarAssembler: default {node} fill equip failed");
            piece.Destroy();
        }
        return Task.CompletedTask;
    }

    // Pieces are built OUTSIDE the user root: CanEquip rejects objects that
    // already sit under a user, and Equip reparents the piece in afterwards.
    private Slot BuildDefaultHead(AvatarEquipManager manager)
    {
        var piece = manager.World.RootSlot.AddSlot("BasicHead");
        piece.Persistent.Value = false;

        // The default pieces are the usual answer to "what is this grey blob following me", and until
        // now nothing logged when one was built - only when one equipped. A piece that is built and
        // fails to socket still carries an AvatarPoseDriver, so it rides the body node anyway and looks
        // exactly like a bug with no line in the log to explain it. -xlinka
        LumoraLogger.Debug("AvatarAssembler: built default BasicHead (grey sphere) to fill an empty Head socket");

        piece.AttachComponent<AvatarPoseDriver>().Node.Value = BodyNode.Head;
        piece.AttachComponent<DiscardOnDequip>();

        var visual = piece.AddSlot("Visual");
        var mesh = visual.AttachComponent<SphereMesh>();
        mesh.Radius.Value = HeadSize * 0.5f;
        mesh.Segments.Value = 24;
        mesh.Rings.Value = 16;
        mesh.DualSided.Value = true;

        var renderer = visual.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var material = visual.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = DefaultTint.Value;
        renderer.Material.Target = material;

        if (HideHeadFromLocalUser.Value)
        {
            var localView = visual.AttachComponent<LocalViewOverride>();
            localView.Context.Value = ViewContext.UserView;
            localView.HasScaleOverride.Value = true;
            localView.ScaleOverride.Value = float3.Zero;
        }

        return piece;
    }

    private static Slot BuildDefaultHand(AvatarEquipManager manager, BodyNode node)
    {
        LumoraLogger.Debug($"AvatarAssembler: built default {node} placeholder to fill an empty socket");
        bool isLeft = node == BodyNode.LeftHand;
        var piece = manager.World.RootSlot.AddSlot(isLeft ? "BasicLeftHand" : "BasicRightHand");
        piece.Persistent.Value = false;

        piece.AttachComponent<AvatarPoseDriver>().Node.Value = node;
        piece.AttachComponent<DiscardOnDequip>();
        piece.AttachComponent<ControllerHandVisual>().HandSide.Value =
            isLeft ? Chirality.Left : Chirality.Right;

        return piece;
    }
}
