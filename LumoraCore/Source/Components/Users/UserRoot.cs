// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using AudioListener = Lumora.Core.Components.Audio.AudioListener;
using AudioOutput = Lumora.Core.Components.Audio.AudioOutput;
using AvatarAudioOutputManager = Lumora.Core.Components.Audio.AvatarAudioOutputManager;
using AvatarVoiceInfo = Lumora.Core.Components.Audio.AvatarVoiceInfo;
using AudioTypeGroup = Lumora.Core.Audio.AudioTypeGroup;
using OpusStream = Lumora.Core.Networking.Streams.OpusStream;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Users")]
public class UserRoot : Component
{
    public enum UserNode
    {
        None,
        Root,
        Head,
        Body,
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot,
        Hips,
        Feet,
        GroundProjectedHead,
        View
    }

    // USER REFERENCE
    // so clients can identify their own
    public readonly SyncRef<User> TargetUser = null!;
    public readonly SyncRef<Slot> TrackingSpaceRoot = new();

    public Slot TrackingSpaceSlot => TrackingSpaceRoot.Target is { IsDestroyed: false } space
        ? space : Slot.FindChild("Body Nodes", recursive: false) ?? Slot;

    // Where this user's eye cameras take their clip planes from. The worn avatar's AvatarRenderSettings
    // registers here on equip and clears itself on dequip; empty means the engine defaults.
    public readonly SyncRef<Avatar.IRenderSettingsSource> RenderSettings = new();

    public Avatar.IRenderSettingsSource? ActiveRenderSettings
        => RenderSettings.Target is { IsDestroyed: false } source ? source : null;

    // Where this user sees from instead of the head, and where the user's tracked space is drawn from instead
    // of this root, while something the user holds or wears asks for it (HolderViewOverride). Only the user's own
    // machine writes either; everyone reads them, so a script on another machine asking where this user
    // looks from gets the same answer. A screen camera sits exactly on the view; a headset keeps the head,
    // since a view that does not turn with the head is not something to put on anyone's face. The root
    // moves a headset's eyes, and nothing on a screen, whose camera follows the view. -xlinka
    public readonly SyncRef<Slot> ViewOverride = new();
    public readonly SyncRef<Slot> RootOverride = new();

    public Slot? ActiveViewOverride => ViewOverride.Target is { IsDestroyed: false } view ? view : null;

    public Slot? ActiveRootOverride => RootOverride.Target is { IsDestroyed: false } space ? space : null;

    // TRACKING SPACE OFFSET
    //
    // The tracking space sits off the root by two things added together: what the worn avatar asks for (a
    // quadruped's view is not where a person's eyes are) and seated mode's lift, straight up the root. They are
    // kept apart and the slot is only ever written as base plus lift, so neither stacks on the other and a lift
    // toggled a hundred times is still one lift. A value something else wrote into the slot becomes the base.
    // Only the user's own machine lifts; the slot replicates with the rest of the body, so everyone sees it.
    // -xlinka
    private Slot? _offsetSpace;
    private float3 _offsetBase;
    private float3 _offsetWritten;
    private float _appliedLift;

    // How much of the offset is seated mode's lift, in tracking-space metres, for every peer: something that
    // reads the head against the tracking space (a quadruped's crouch) adds it back.
    public readonly Sync<float> SeatedLift = new();

    public void SetAvatarTrackingOffset(float3 offset)
    {
        var space = TrackingSpaceSlot;
        if (space != Slot)
            WriteTrackingOffset(space, offset, _appliedLift);
    }

    public void SetSeatedLift(float lift)
    {
        var space = TrackingSpaceSlot;
        if (space == Slot)
            return;
        if (!float.IsFinite(lift) || lift < 0f)
            lift = 0f;
        if (ReferenceEquals(space, _offsetSpace) && lift == _appliedLift && space.LocalPosition.Value == _offsetWritten)
            return;
        // Engine placement of the user's own body, like the walk that moves the root: the root is minted by the
        // host, and the link that makes it this user's lags a moment on join.
        using (World?.DataModelPermissions?.EnterSystemBypass())
            WriteTrackingOffset(space, null, lift);
    }

    private void WriteTrackingOffset(Slot space, float3? offset, float lift)
    {
        float3 current = space.LocalPosition.Value;
        if (!ReferenceEquals(space, _offsetSpace))
        {
            // A space not written here yet carries no lift of ours.
            _offsetSpace = space;
            _offsetBase = current;
        }
        else if (current != _offsetWritten)
        {
            _offsetBase = current;
        }
        if (offset.HasValue)
            _offsetBase = offset.Value;
        _appliedLift = lift;
        _offsetWritten = _offsetBase + float3.Up * lift;
        if (current != _offsetWritten)
            space.LocalPosition.Value = _offsetWritten;
        if (IsLocalUserRoot && SeatedLift.Value != lift)
            SeatedLift.Value = lift;
    }

    private bool _isRegistered = false;

    public User ActiveUser => (TargetUser?.Target) ?? null!;

    public bool IsLocalUserRoot => TargetUser?.Target != null && TargetUser.Target == World?.LocalUser;

    // CACHED BODY NODES
    private Slot _cachedHeadSlot = null!;
    private Slot _cachedBodySlot = null!;
    private Slot _cachedLeftHandSlot = null!;
    private Slot _cachedRightHandSlot = null!;
    private Slot _cachedLeftFootSlot = null!;
    private Slot _cachedRightFootSlot = null!;

    // BODY NODE ACCESSORS
    //
    // Resolved from the typed UserRootComponent registry rather than string-
    // named child lookups. Any TrackedDevicePositioner under the user that
    // declares AutoBodyNode == HeadNode shows up here automatically - slots
    // can be renamed or moved without breaking the lookup. - xlinka

    public Slot HeadSlot => GetBodyNodeSlot(ref _cachedHeadSlot, Input.BodyNode.Head)
                            ?? FallbackChildLookup(ref _cachedHeadSlot, ref _headMiss, "Head");

    // The first context menu under this body, as found in the body's present structure. Scripts ask whether a
    // person's menu is open every frame, and each ask walked the whole body and the avatar on it.
    private int _menuSearchedAt = -1;
    private UI.ContextMenuSystem? _menu;

    internal UI.ContextMenuSystem? ContextMenu
    {
        get
        {
            int structure = Slot.StructureEpoch;
            if (structure != _menuSearchedAt)
            {
                _menu = Slot.GetComponentInChildren<UI.ContextMenuSystem>();
                _menuSearchedAt = structure;
            }
            return _menu;
        }
    }

    public Slot LeftHandSlot => GetBodyNodeSlot(ref _cachedLeftHandSlot, Input.BodyNode.LeftHand)
                                ?? FallbackChildLookup(ref _cachedLeftHandSlot, ref _leftHandMiss, "LeftHand");

    public Slot RightHandSlot => GetBodyNodeSlot(ref _cachedRightHandSlot, Input.BodyNode.RightHand)
                                 ?? FallbackChildLookup(ref _cachedRightHandSlot, ref _rightHandMiss, "RightHand");

    public Slot LeftFootSlot => GetBodyNodeSlot(ref _cachedLeftFootSlot, Input.BodyNode.LeftFoot)
                                ?? FallbackChildLookup(ref _cachedLeftFootSlot, ref _leftFootMiss, "LeftFoot");

    public Slot RightFootSlot => GetBodyNodeSlot(ref _cachedRightFootSlot, Input.BodyNode.RightFoot)
                                 ?? FallbackChildLookup(ref _cachedRightFootSlot, ref _rightFootMiss, "RightFoot");

    // Body component (avatar torso) - not a tracked device, still needs string lookup.
    public Slot BodySlot
    {
        get
        {
            if ((_cachedBodySlot == null || _cachedBodySlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedBodySlot = Slot.FindChild("Avatar", recursive: false)?.FindChild("Body", recursive: false)!;
            }
            return _cachedBodySlot!;
        }
    }

    private Slot GetBodyNodeSlot(ref Slot cache, Input.BodyNode node)
    {
        if (IsDestroyed)
            return null!;

        var resolved = PositionerSlot(node);

        if (cache != null && !cache.IsDestroyed)
        {
            // A tracked device positioner has two relevant slots:
            // - Slot: the tracked body transform (head/controller/etc.)
            // - BodyNodeRoot: an equipment/object child that only carries offsets
            // User-space consumers need the tracked transform. Older code cached the
            // child, which double-applied desktop head height and moved nametags/IK.
            if (resolved == null || ReferenceEquals(cache, resolved))
                return cache;
        }

        // Not every body node is device-tracked: hands are AvatarSockets
        // riding under the controller positioners. Resolve through the
        // registered object slots before giving up.
        if (resolved == null)
        {
            resolved = SocketSlot(node);
        }

        if (resolved != null)
            cache = resolved;
        return resolved!;
    }

    // Plain loops over the registry: the body node accessors run several times a frame per user, and a
    // predicate capturing the node was two allocations every call.
    private Slot? PositionerSlot(Input.BodyNode node)
    {
        foreach (var positioner in GetComponentsOfType<TrackedDevicePositioner>())
        {
            if (!positioner.IsDestroyed && positioner.AutoBodyNode.Value == node)
                return positioner.Slot;
        }
        return null;
    }

    private Slot? SocketSlot(Input.BodyNode node)
    {
        foreach (var socket in GetComponentsOfType<Avatar.AvatarSocket>())
        {
            if (!socket.IsDestroyed && socket.Node.Value == node)
                return socket.Slot;
        }
        return null;
    }

    // Old string-named child lookup. Kept as a fallback for slots that exist
    // before the matching TrackedDevicePositioner registers, and for avatars
    // built by templates that don't use tracked-device positioners at all.
    // - xlinka
    //
    // A miss is remembered until a slot or a component comes or goes somewhere, or a second has passed: scripts
    // ask for every person's feet each frame, hardly anybody has tracked feet, and every ask that found nothing
    // searched three sets of children again. -xlinka
    private struct LookupMiss
    {
        public int Structure;
        public double At;
    }

    private LookupMiss _headMiss = new() { Structure = -1 };
    private LookupMiss _leftHandMiss = new() { Structure = -1 };
    private LookupMiss _rightHandMiss = new() { Structure = -1 };
    private LookupMiss _leftFootMiss = new() { Structure = -1 };
    private LookupMiss _rightFootMiss = new() { Structure = -1 };

    private Slot FallbackChildLookup(ref Slot cache, ref LookupMiss miss, string name)
    {
        if (cache != null && !cache.IsDestroyed)
            return cache;
        if (IsDestroyed)
            return null!;

        int structure = Slot.StructureEpoch;
        double now = World?.Time.TotalTime ?? 0.0;
        if (miss.Structure == structure && now >= miss.At && now - miss.At < 1.0)
            return null!;

        cache = (Slot.FindChild("Body Nodes", recursive: false)?.FindChild(name, recursive: false) ??
                 Slot.FindChild(name, recursive: false) ??
                 Slot.FindChild("Avatar", recursive: false)?.FindChild(name, recursive: false))!;
        miss = cache == null ? new LookupMiss { Structure = structure, At = now } : new LookupMiss { Structure = -1 };
        return cache!;
    }

    // AUDIO
    //
    // The ear rides the head slot, one per user and replicated with the rest of the scaffold, because
    // everyone has to agree on where a user's ears are before excluding a listener from an output means
    // anything. Only the local user's listener is ever bound to the device - see AudioListener - so the
    // remote ones cost nothing but a registration.
    //
    // Built here rather than in the avatar assembler because the head slot only resolves once the body
    // nodes have been built, which is after the assembler has run. -xlinka
    private AudioListener _primaryListener = null!;

    public AudioListener PrimaryListener
        => _primaryListener != null && !_primaryListener.IsDestroyed ? _primaryListener : null!;

    public AudioOutput? VoiceOutput => _voiceOutput is { IsDestroyed: false } ? _voiceOutput : null;

    private void EnsurePrimaryListener()
    {
        if (_primaryListener != null && !_primaryListener.IsDestroyed)
            return;
        if (IsDestroyed || World == null)
            return;

        var head = HeadSlot;
        var user = ActiveUser;
        if (head == null || head.IsDestroyed || user == null)
            return;

        // A saved world (or a scaffold that already replicated in) brings its own; adding a second ear
        // to the same head would mix the world twice.
        var existing = head.GetComponent<AudioListener>();
        if (existing != null)
        {
            _primaryListener = existing;
            if (World.IsAuthority && existing.ActiveUser.Target != user)
            {
                using (EnterScaffoldScope())
                    existing.ActiveUser.Target = user;
            }
            return;
        }

        // The scaffold is the authority's to build, and this is part of it. A client attaching its own
        // would mint one listener per peer on the same head.
        if (!World.IsAuthority)
            return;

        using (EnterScaffoldScope())
        {
            var listener = head.AttachComponent<AudioListener>();
            listener.ActiveUser.Target = user;
            _primaryListener = listener;
        }
    }

    // The ear and the voice are part of the scaffold the authority builds on every body, a guest's included,
    // so they are made past the gate like the rest of it: a Social or Event world's freeze refused them on
    // every body but the host's own and left each guest deaf and silent. Opened around each write that is
    // actually made, so a finished body costs nothing a frame, and only for a root the authority made: one a
    // visitor put on a thing it brought gets nothing built past the gate. -xlinka
    private IDisposable? EnterScaffoldScope()
        => World is { IsAuthority: true } world && ReferenceID.IsAuthorityID ? world.DataModelPermissions?.EnterSystemBypass() : null;

    // VOICE
    //
    // Same shape as the ear, for the same reason: everyone has to agree which stream is whose voice
    // and where in the room it comes out before excluding anybody from it means anything.
    //
    // The stream itself belongs to the speaker, because only a stream's owner may create one, so a
    // remote user's shows up here a frame or two after they join rather than the instant their root
    // does. The scaffold is still the authority's to build; it just fills the ref when the stream
    // arrives. -xlinka
    public const string VoiceStreamName = "Voice";

    // Above an ordinary emitter, below nothing. When the voice limit bites, the person talking to you
    // is not the thing to drop. The local user's own output goes to the top in the manager, the one
    // emitter that must never be culled.
    private const int VoicePriority = 200;

    private UserAudioStream _voiceStream = null!;
    private AudioOutput _voiceOutput = null!;
    private AvatarAudioOutputManager _voiceOutputManager = null!;
    private bool _voiceWired;

    public UserAudioStream VoiceStream
        => _voiceStream != null && !_voiceStream.IsDestroyed ? _voiceStream : null!;

    private void EnsureVoiceOutput()
    {
        if (IsDestroyed || World == null)
            return;

        if (_voiceWired)
        {
            if (_voiceStream != null && !_voiceStream.IsDestroyed
                && _voiceOutput != null && !_voiceOutput.IsDestroyed)
            {
                return;
            }
            _voiceWired = false;
        }

        var user = ActiveUser;
        var head = HeadSlot;
        if (user == null || head == null || head.IsDestroyed)
            return;

        // The speaker mints their own; nobody can do it on their behalf.
        var stream = user.IsLocal
            ? user.GetStreamOrAdd<OpusStream>(VoiceStreamName)
            : user.GetStream<OpusStream>(s => s.Name == VoiceStreamName);

        if (_voiceStream == null || _voiceStream.IsDestroyed)
            _voiceStream = Slot.GetComponent<UserAudioStream>()!;
        if (_voiceOutput == null || _voiceOutput.IsDestroyed)
            _voiceOutput = head.GetComponent<AudioOutput>()!;

        bool authority = World.IsAuthority;

        if (_voiceStream == null || _voiceStream.IsDestroyed)
        {
            if (!authority)
                return;
            using (EnterScaffoldScope())
                _voiceStream = Slot.AttachComponent<UserAudioStream>();
        }

        if (_voiceOutput == null || _voiceOutput.IsDestroyed)
        {
            if (!authority)
                return;
            using (EnterScaffoldScope())
            {
                _voiceOutput = head.AttachComponent<AudioOutput>();
                ConfigureVoiceOutput(_voiceOutput);
            }
        }

        if (_voiceOutputManager == null || _voiceOutputManager.IsDestroyed)
            _voiceOutputManager = head.GetComponent<AvatarAudioOutputManager>()!;

        if (!authority)
            return;

        if (stream != null && _voiceStream.Stream.Target != stream)
        {
            using (EnterScaffoldScope())
                _voiceStream.Stream.Target = stream;
        }

        if (_voiceOutput.Source.Target != _voiceStream)
        {
            using (EnterScaffoldScope())
                _voiceOutput.Source.Target = _voiceStream;
        }

        // Where the voice comes from, published on the root registry so a mouth visualiser or a level
        // meter finds it without guessing at a slot name.
        var info = GetRegisteredComponent<AvatarVoiceInfo>();
        if (info == null || info.AudioSource.Target != _voiceStream)
        {
            using (EnterScaffoldScope())
            {
                info ??= Slot.AttachComponent<AvatarVoiceInfo>();
                info.AudioSource.Target = _voiceStream;
            }
        }

        // Distances, rolloff, spatialization and the exclusion are the manager's from here. They used to
        // be a hard-coded 1..100 m written once at attach, which meant a voice had exactly one range and
        // whisper/shout/broadcast had nowhere to land.
        if (_voiceOutputManager == null || _voiceOutputManager.IsDestroyed || _voiceOutputManager.AudioOutput.Target != _voiceOutput)
        {
            using (EnterScaffoldScope())
            {
                if (_voiceOutputManager == null || _voiceOutputManager.IsDestroyed)
                    _voiceOutputManager = head.AttachComponent<AvatarAudioOutputManager>();
                if (_voiceOutputManager.AudioOutput.Target != _voiceOutput)
                    _voiceOutputManager.BindTo(_voiceOutput);
            }
        }

        // Wired once. A remote user's stream arrives a moment after their root, so until it does the
        // walk repeats; after that this is a pair of null checks a frame.
        _voiceWired = stream != null && _voiceStream.Stream.Target == stream;
    }

    private static void ConfigureVoiceOutput(AudioOutput output)
    {
        output.AudioTypeGroup.Value = AudioTypeGroup.Voice;
        // A head moves fast enough in VR to pitch-shift a voice audibly, and nobody wants their friend
        // to whistle past them. The manager drives this too, but the attach-time value is what a peer
        // hears in the frames before the drive links are granted.
        output.DopplerLevel.Value = 0f;
        output.Priority.Value = VoicePriority;
    }

    // POSITION ACCESSORS

    public float3 HeadPosition
    {
        get => HeadSlot?.GlobalPosition ?? Slot.GlobalPosition;
        set
        {
            if (HeadSlot != null)
            {
                var offset = value - HeadPosition;
                Slot.GlobalPosition += offset;
            }
        }
    }

    public floatQ HeadRotation
    {
        get => HeadSlot?.GlobalRotation ?? Slot.GlobalRotation;
        set
        {
            if (HeadSlot != null)
            {
                HeadSlot.GlobalRotation = value;
            }
        }
    }

    // BODY POINTS
    //
    // Where a seat, an anchor or a script places a person by. Each is read off whatever knows best: a tracker
    // on that part, else the bone of the avatar they wear, else an estimate from the head. Setting one never
    // writes a body slot, since tracking and IK rewrite those every frame and most of these points are worked
    // out rather than slots at all. It moves the whole rig by however far the point is from where it should
    // be, so the point lands there and the rest of the body keeps its place around it. -xlinka

    // A standing head sits over the toes, not the heels: the floor point and the estimated hips are taken
    // this far behind it.
    private const float HeadAheadOfFeet = 0.1f;

    // How far up from the feet to the head the estimated hips sit.
    private const float HipsUpFeetToHead = 0.6f;

    // Between the feet: tracked feet, else the worn avatar's foot bones, else the floor under the head.
    public float3 FeetPosition
    {
        get
        {
            if (TrackedPart(Input.BodyNode.LeftFoot) is { } left && TrackedPart(Input.BodyNode.RightFoot) is { } right)
                return (left.GlobalPosition + right.GlobalPosition) * 0.5f;
            var rig = WornRig();
            if (WornBone(rig, Input.BodyNode.LeftFoot) is { } leftBone && WornBone(rig, Input.BodyNode.RightFoot) is { } rightBone)
                return (leftBone.GlobalPosition + rightBone.GlobalPosition) * 0.5f;
            return Slot.LocalPointToGlobal(LocalGroundUnderHead() - LocalHeadFacing() * HeadAheadOfFeet);
        }
        set => ShiftBy(value - FeetPosition);
    }

    // Tracked feet face the way they point, turned to agree with the head; anything else faces with the head.
    public floatQ FeetRotation
    {
        get
        {
            var facing = LocalHeadFacing();
            if (TrackedPart(Input.BodyNode.LeftFoot) is { } left && TrackedPart(Input.BodyNode.RightFoot) is { } right)
            {
                var along = Slot.GlobalDirectionToLocal(left.GlobalRotation * float3.Backward + right.GlobalRotation * float3.Backward);
                var flat = new float3(along.x, 0f, along.z);
                if (flat.LengthSquared > 1e-8f)
                {
                    flat = flat.Normalized;
                    return LevelRotation(float3.Dot(flat, facing) < 0f ? -flat : flat);
                }
            }
            return LevelRotation(facing);
        }
    }

    // A hips tracker, else the worn avatar's hips bone, else most of the way up from the feet to the head and
    // a little behind it.
    public float3 HipsPosition
    {
        get
        {
            if (TrackedPart(Input.BodyNode.Hips) is { } tracked)
                return tracked.GlobalPosition;
            if (WornBone(WornRig(), Input.BodyNode.Hips) is { } bone)
                return bone.GlobalPosition;
            var feet = FeetPosition;
            return feet + (HeadPosition - feet) * HipsUpFeetToHead - Slot.LocalVectorToGlobal(LocalHeadFacing() * HeadAheadOfFeet);
        }
        set => ShiftBy(value - HipsPosition);
    }

    public floatQ HipsRotation
        => TrackedPart(Input.BodyNode.Hips) is { } tracked ? tracked.GlobalRotation : LevelHeadRotation;

    // Under the head on the user's own floor, the root's plane, so a rig tipped in a seat tips this with it.
    public float3 GroundProjectedHeadPosition
    {
        get => Slot.LocalPointToGlobal(LocalGroundUnderHead());
        set => ShiftBy(value - GroundProjectedHeadPosition);
    }

    // The head's rotation with its tilt taken out: level with the root, turned the way the head looks.
    public floatQ LevelHeadRotation => LevelRotation(LocalHeadFacing());

    // The way the head looks flattened onto the user's floor, in root space, unit length. Looking nearly
    // straight up or down, the top of the head gives the heading instead.
    private float3 LocalHeadFacing()
    {
        var head = HeadSlot;
        if (head == null || head.IsDestroyed)
            return float3.Backward;
        var look = Slot.GlobalRotationToLocal(head.GlobalRotation);
        var forward = look * float3.Backward;
        var flat = new float3(forward.x, 0f, forward.z);
        if (flat.LengthSquared < 1e-4f)
        {
            var top = look * float3.Up;
            flat = forward.y < 0f ? new float3(top.x, 0f, top.z) : new float3(-top.x, 0f, -top.z);
        }
        return flat.LengthSquared < 1e-8f ? float3.Backward : flat.Normalized;
    }

    private float3 LocalGroundUnderHead()
    {
        var head = Slot.GlobalPointToLocal(HeadPosition);
        return new float3(head.x, 0f, head.z);
    }

    // Turns only about the root's up, its forward (-Z) onto a root-space direction on the floor.
    private floatQ LevelRotation(float3 localFacing)
    {
        float yaw = MathF.Atan2(-localFacing.x, -localFacing.z);
        return (Slot.GlobalRotation * floatQ.AxisAngle(float3.Up, yaw)).Normalized;
    }

    // What a tracker on that body part carries it by, while one is tracking it.
    private Slot? TrackedPart(Input.BodyNode node)
    {
        foreach (var positioner in GetComponentsOfType<TrackedDevicePositioner>())
        {
            if (positioner.IsDestroyed || !positioner.IsTracking.Value
                || (positioner.AutoBodyNode.Value ?? positioner.CorrespondingBodyNode.Value) != node)
                continue;
            var part = positioner.BodyNodeRoot.Target;
            return part is { IsDestroyed: false } ? part : positioner.Slot;
        }
        return null;
    }

    private Avatar.HumanoidRig? WornRig()
    {
        var rig = GetRegisteredComponent<Avatar.AvatarIK>(static ik => ik.IsEquipped)?.Rig.Target;
        return rig is { IsDestroyed: false } ? rig : null;
    }

    private static Slot? WornBone(Avatar.HumanoidRig? rig, Input.BodyNode node)
        => rig?.TryGetBone(node) is { IsDestroyed: false } bone ? bone : null;

    // The user's size in the world, whatever the root hangs from: a carrier's space or a seat can be scaled.
    public float GlobalScale
    {
        get => Slot.GlobalScale.x;
        set => Slot.GlobalScale = float3.One * value;
    }

    private TransformStreamDriver _rootStreamDriver = null!;

    // A TransformStreamDriver sharing the root slot means the Root stream is the transport for the root
    // transform. Turning has to write SILENTLY then, or the same rotation goes out over the stream AND the
    // delta channel and the two arrive a frame apart. Re-checks until the driver is found (it is attached
    // during the avatar build), then caches. Same rule the character controller hook follows when it writes
    // walking motion onto this slot. -xlinka
    private bool RootIsStreamed()
    {
        if (_rootStreamDriver == null || _rootStreamDriver.IsDestroyed)
            _rootStreamDriver = Slot?.GetComponent<TransformStreamDriver>()!;
        return _rootStreamDriver != null && !_rootStreamDriver.IsDestroyed;
    }

    private void WriteRootRotation(floatQ rotation)
    {
        if (RootIsStreamed())
            Slot.SetGlobalRotationSilently(rotation);
        else
            Slot.GlobalRotation = rotation;
    }

    private void WriteRootPosition(float3 position)
    {
        if (RootIsStreamed())
            Slot.SetGlobalPositionSilently(position);
        else
            Slot.GlobalPosition = position;
    }

    // Below a hundredth of a millimetre the pivot correction is float noise, and paying it every frame
    // would put a transform write (and a hook update) on the root for every frame of mouse look.
    private const float PivotCorrectionSq = 1e-10f;

    // The whole rig moved by an offset, or turned about its own origin, the way its own movement writes it.
    public void ShiftBy(float3 offset)
    {
        if (Slot == null || offset.LengthSquared <= PivotCorrectionSq)
            return;
        WriteRootPosition(Slot.GlobalPosition + offset);
    }

    public void TurnBy(floatQ delta)
    {
        if (Slot == null)
            return;
        WriteRootRotation((delta * Slot.GlobalRotation).Normalized);
    }

    private float _scaleFrom;
    private float _scaleTarget;
    private float _scaleSeconds;
    private float _scaleElapsed;
    private bool _scaling;

    // The user's scale eased to another over some seconds, the feet held where they stand, inside the engine's
    // range and the role's. Only the machine this user is at moves the root, so anywhere else it does nothing.
    public void ScaleTo(float scale, float seconds)
    {
        if (!IsLocalUserRoot || Slot == null || !float.IsFinite(scale) || !float.IsFinite(seconds))
            return;
        var locomotion = Slot.GetComponent<LocomotionController>();
        scale = locomotion != null ? locomotion.ClampUserScale(scale)
            : World?.DataModelPermissions?.ClampScale(ActiveUser, scale) ?? scale;
        _scaleFrom = GlobalScale;
        _scaleTarget = scale;
        _scaleSeconds = MathF.Max(seconds, 0f);
        _scaleElapsed = 0f;
        _scaling = true;
        StepScale(0f);
    }

    private void StepScale(float delta)
    {
        if (Slot == null || Slot.IsDestroyed)
        {
            _scaling = false;
            return;
        }
        _scaleElapsed += delta;
        float t = _scaleSeconds <= 0f ? 1f : MathF.Min(_scaleElapsed / _scaleSeconds, 1f);
        var feet = FeetPosition;
        GlobalScale = _scaleFrom + (_scaleTarget - _scaleFrom) * t;
        var offset = feet - FeetPosition;
        if (offset.LengthSquared > PivotCorrectionSq)
            WriteRootPosition(Slot.GlobalPosition + offset);
        if (t >= 1f)
            _scaling = false;
    }

    // Rotate the WHOLE rig and keep the head where it is. Everything tracked hangs off this slot in
    // user-root space - head, controllers, hands, the avatar - so turning the root is what carries them
    // all round together, and pinning the head is what makes it read as turning in place rather than
    // being swung around a pole. This is the one rotate a turn goes through, snap, smooth or mouse.
    public void RotateAroundHead(floatQ deltaRotation)
    {
        if (Slot == null)
            return;

        // Resolved once: desktop look comes through here every frame the mouse moves, and the body-node
        // lookup behind HeadSlot is a registry walk. No head (rig still building) pivots on the root, which
        // is what a plain rotation write would have done anyway.
        var head = HeadSlot;
        var pivot = head != null ? head.GlobalPosition : Slot.GlobalPosition;

        WriteRootRotation((deltaRotation * Slot.GlobalRotation).Normalized);

        var offset = pivot - (head != null ? head.GlobalPosition : Slot.GlobalPosition);
        if (offset.LengthSquared > PivotCorrectionSq)
            WriteRootPosition(Slot.GlobalPosition + offset);
    }

    public void RotateYawAroundHead(float yawRadians)
    {
        if (System.Math.Abs(yawRadians) < 0.000001f)
            return;

        RotateAroundHead(floatQ.AxisAngle(float3.Up, yawRadians));
    }

    public bool ReceivedFirstPositionalData
    {
        get
        {
            if (HeadSlot != null)
            {
                var headPos = HeadSlot.LocalPosition.Value;
                var headRot = HeadSlot.LocalRotation.Value;
                if (headPos != float3.Zero || headRot != floatQ.Identity)
                    return true;
            }

            var headPositioner = HeadSlot?.GetComponent<TrackedDevicePositioner>();
            if (headPositioner != null)
                return headPositioner.IsTracking.Value;

            // For desktop mode, we consider it always tracked
            if (ActiveUser != null && Engine.Current?.InputInterface != null)
            {
                var headDevice = Engine.Current.InputInterface.HeadDevice;
                return headDevice == null || !headDevice.IsTracked; // Desktop mode
            }

            return false;
        }
    }

    // INITIALIZATION

    // called by SimpleUserSpawn after attaching the component
    public void Initialize(User user)
    {
        if (user == null)
        {
            LumoraLogger.Error("UserRoot: Cannot initialize with null user");
            return;
        }

        TargetUser.Target = user;

        if (World?.IsAuthority == true)
        {
            // The authority wiring a joiner's record to the root it just built them, an engine step and
            // not an edit: the social freeze refuses every write to what the writer does not own. -xlinka
            using (World.DataModelPermissions?.EnterSystemBypass())
                user.Root = this;
            LumoraLogger.Debug($"User: Registered UserRoot for authority user '{user.UserName.Value}'");
        }

        LumoraLogger.Debug($"UserRoot: Initialized for user '{user.UserName.Value}' (RefID: {user.ReferenceID})");
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Slot?.RegisterUserRoot(this);
        // A body is as big as its own user makes it, and goes under whatever its seat or carrier puts it under.
        // No drive takes either, whoever wrote it: world content copying a value onto a root would otherwise
        // shrink a visitor or hang them off its furniture, and keep doing it every frame. -xlinka
        if (Slot != null)
        {
            Slot.LocalScale.MarkNonDrivable();
            Slot.ParentSlotRef.MarkNonDrivable();
        }
        // Where a user sees from is for that user alone to point: the host turns away any remote write that
        // aims it at something unless it comes from this root's own user. Clearing is never asked about. -xlinka
        ViewOverride.CheckRemoteSets(SentByOwnUser);
        RootOverride.CheckRemoteSets(SentByOwnUser);
    }

    private bool SentByOwnUser(User? sender, RefID _, Slot? __)
        => sender != null && ReferenceEquals(sender, TargetUser?.Target);

    // Typed component cache. Every UserRootComponent in the user's slot
    // hierarchy registers here. Lookups like "find the AvatarIK attached
    // anywhere under this user" are O(1) by type instead of walking the
    // slot tree + GetComponents per slot. - xlinka
    private readonly HashSet<Component> _registeredComponents = new();
    private readonly Dictionary<Type, IList> _perTypeComponents = new();

    internal void RegisterComponent(Component component)
    {
        if (component == null || !_registeredComponents.Add(component))
            return;

        var type = component.GetType();
        foreach (var kvp in _perTypeComponents)
        {
            if (kvp.Key.IsAssignableFrom(type))
                kvp.Value.Add(component);
        }
    }

    internal void UnregisterComponent(Component component)
    {
        if (component == null || !_registeredComponents.Remove(component))
            return;

        var type = component.GetType();
        foreach (var kvp in _perTypeComponents)
        {
            if (kvp.Key.IsAssignableFrom(type))
                kvp.Value.Remove(component);
        }
    }

    private List<T> GetComponentsOfType<T>() where T : class
    {
        if (_perTypeComponents.TryGetValue(typeof(T), out var existing))
            return (List<T>)existing;

        var list = new List<T>();
        foreach (var c in _registeredComponents)
        {
            if (c is T match)
                list.Add(match);
        }
        _perTypeComponents[typeof(T)] = list;
        return list;
    }

    public T GetRegisteredComponent<T>(Predicate<T> filter = null!) where T : class
    {
        foreach (var item in GetComponentsOfType<T>())
        {
            if (item is Component c && c.IsDestroyed) continue;
            if (filter == null || filter(item))
                return item;
        }
        return null!;
    }

    public void GetRegisteredComponents<T>(List<T> output, Predicate<T> filter = null!) where T : class
    {
        foreach (var item in GetComponentsOfType<T>())
        {
            if (item is Component c && c.IsDestroyed) continue;
            if (filter == null || filter(item))
                output.Add(item);
        }
    }

    public List<T> GetRegisteredComponents<T>(Predicate<T> filter = null!) where T : class
    {
        var list = new List<T>();
        GetRegisteredComponents(list, filter);
        return list;
    }

    public void ForeachRegisteredComponent<T>(Action<T> action) where T : class
    {
        foreach (var item in GetComponentsOfType<T>())
        {
            if (item is Component c && c.IsDestroyed) continue;
            action(item);
        }
    }

    public override void OnChanges()
    {
        base.OnChanges();

        if (TargetUser.Target == World?.LocalUser && !_isRegistered)
        {
            World.LocalUser.Root = this;
            _isRegistered = true;
            LumoraLogger.Debug($"UserRoot: Registered as Root for local user '{TargetUser.Target?.UserName?.Value}'");
        }

        if (TargetUser.Target != World?.LocalUser && _isRegistered)
        {
            if (World?.LocalUser?.Root == this)
            {
                World.LocalUser.Root = null!;
            }
            _isRegistered = false;
        }
    }

    // View is where the user sees from: the head, unless something they hold or wear has moved it
    // (ViewOverride). A body slot is the torso when an avatar has one; without it the hips stand in.
    public float3 GetGlobalPosition(UserNode node)
    {
        return node switch
        {
            UserNode.None => float3.Zero,
            UserNode.Root => Slot.GlobalPosition,
            UserNode.Head => HeadPosition,
            UserNode.View => ActiveViewOverride?.GlobalPosition ?? HeadPosition,
            UserNode.Body => BodySlot?.GlobalPosition ?? HipsPosition,
            UserNode.LeftHand => LeftHandSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.RightHand => RightHandSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.LeftFoot => LeftFootSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.RightFoot => RightFootSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.Hips => HipsPosition,
            UserNode.Feet => FeetPosition,
            UserNode.GroundProjectedHead => GroundProjectedHeadPosition,
            _ => throw new ArgumentException($"Invalid UserNode: {node}")
        };
    }

    public floatQ GetGlobalRotation(UserNode node)
    {
        return node switch
        {
            UserNode.None => floatQ.Identity,
            UserNode.Root => Slot.GlobalRotation,
            UserNode.Head => HeadRotation,
            UserNode.View => ActiveViewOverride?.GlobalRotation ?? HeadRotation,
            UserNode.Body => BodySlot?.GlobalRotation ?? HipsRotation,
            UserNode.LeftHand => LeftHandSlot?.GlobalRotation ?? Slot.GlobalRotation,
            UserNode.RightHand => RightHandSlot?.GlobalRotation ?? Slot.GlobalRotation,
            UserNode.LeftFoot => LeftFootSlot?.GlobalRotation ?? Slot.GlobalRotation,
            UserNode.RightFoot => RightFootSlot?.GlobalRotation ?? Slot.GlobalRotation,
            UserNode.Hips => HipsRotation,
            UserNode.Feet => FeetRotation,
            UserNode.GroundProjectedHead => LevelHeadRotation,
            _ => throw new ArgumentException($"Invalid UserNode: {node}")
        };
    }

    // flattened for locomotion
    public float3 HeadFacingDirection
    {
        get
        {
            floatQ headRot = HeadSlot?.GlobalRotation ?? Slot.GlobalRotation;
            // Godot uses -Z as forward; align locomotion basis accordingly
            float3 forward = headRot * float3.Backward;
            forward.y = 0;
            return forward.LengthSquared < 1e-6f ? float3.Backward : forward.Normalized;
        }
    }

    public floatQ HeadFacingRotation
    {
        get
        {
            if (HeadSlot != null)
                return HeadSlot.GlobalRotation;
            return Slot.GlobalRotation;
        }
    }

    // Moves the ROOT so the node lands on the point; see BODY POINTS for why never the node itself.
    public void SetGlobalPosition(UserNode node, float3 position)
    {
        if (Slot == null || node == UserNode.None)
            return;
        if (node == UserNode.Root)
            WriteRootPosition(position);
        else
            ShiftBy(position - GetGlobalPosition(node));
    }

    // Rotates the ROOT, not the node's own slot: every body node except Root is written each frame by
    // whatever drives it (tracking positioner, IK), so a rotation stamped on the node itself survives
    // exactly until the next update. Turning the root is the only write that sticks, and pinning the
    // node's position across the turn is what stops the rig from swinging out from under the user -
    // the same pivot trick RotateAroundHead uses, generalized to any node. - xlinka
    public void SetGlobalRotation(UserNode node, floatQ rotation)
    {
        if (Slot == null || node == UserNode.None)
            return;

        var current = GetGlobalRotation(node);
        var delta = (rotation * current.Inverse).Normalized;

        var pivot = GetGlobalPosition(node);
        WriteRootRotation((delta * Slot.GlobalRotation).Normalized);
        var moved = pivot - GetGlobalPosition(node);
        if (moved.LengthSquared > PivotCorrectionSq)
            WriteRootPosition(Slot.GlobalPosition + moved);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        EnsurePrimaryListener();
        // After the ear, because the voice output has to exclude it and the list is filled once.
        EnsureVoiceOutput();

        // someone I blocked is drawn on none of my screens, plate, avatar and all; everyone else still sees them
        Slot.SetLocallyHidden(ActiveUser?.IsBlockedByMe == true);

        if (_scaling)
            StepScale(delta);

        // Userspace's pointer rig takes its devices out of the focused world's space, lift included.
        if (IsLocalUserRoot && World is { IsUserspace: false })
            SetSeatedLift(Input.SeatedMode.Lift);

        // Validate scale to prevent invalid transforms. These corrective writes run on EVERY peer (incl. an
        // observer holding a REMOTE user's root), and use the THROWING .Value/Scale setters - so a foreign-write
        // denial would throw per frame. It's engine sanitization of a body transform, not a user edit, so bypass
        // the gate. Only entered when a correction is actually needed (rare). -xlinka
        var scale = Slot.Scale;
        if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0 ||
            float.IsNaN(scale.x) || float.IsNaN(scale.y) || float.IsNaN(scale.z) ||
            float.IsInfinity(scale.x) || float.IsInfinity(scale.y) || float.IsInfinity(scale.z))
        {
            LumoraLogger.Warn($"UserRoot: Invalid scale detected ({scale}), resetting to (1,1,1)");
            using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
            Slot.LocalScale.Value = float3.One;
        }

        // Even in the world, not against the parent: a root hung from a squashed space keeps its own shape.
        var size = Slot.GlobalScale;
        if (System.Math.Abs(size.x - size.y) > 0.0001f || System.Math.Abs(size.y - size.z) > 0.0001f)
        {
            var avgScale = (size.x + size.y + size.z) / 3f;
            using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
            Slot.GlobalScale = float3.One * avgScale;
        }
    }

    public override void OnDestroy()
    {
        LumoraLogger.Debug($"UserRoot: Destroying UserRoot for user '{ActiveUser?.UserName.Value ?? "Unknown"}'");

        Slot?.UnregisterUserRootHierarchy(this);

        // During world disposal users are disposed before slots/components.
        // Their Root setter is no longer valid, and the user object is going away anyway.
        if (World?.IsDisposed != true && _isRegistered && World?.LocalUser?.Root == this)
        {
            World.LocalUser.Root = null!;
        }
        _isRegistered = false;

        _primaryListener = null!;
        _voiceStream = null!;
        _voiceOutput = null!;
        _voiceWired = false;
        _cachedHeadSlot = null!;
        _cachedBodySlot = null!;
        _cachedLeftHandSlot = null!;
        _cachedRightHandSlot = null!;
        _cachedLeftFootSlot = null!;
        _cachedRightFootSlot = null!;

        base.OnDestroy();
    }
}
