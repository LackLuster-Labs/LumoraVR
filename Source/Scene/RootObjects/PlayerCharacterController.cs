using System;
using System.Collections.Generic;
using LumoraVR.Source.Input;
using LumoraVR.Source.Logging;
using LumoraVR.Source.Management;
using LumoraVR.Source.Scene.Assets;
using LumoraVR.Source.Scene.UI;
using Godot;

namespace LumoraVR.Source.Scene.RootObjects;

public partial class PlayerCharacterController : CharacterBody3D, ICharacterController
{
    public Node Self => this;
    //public bool Dirty { get; set; }
    public IDictionary<ushort, IChildObject> ChildObjects { get; } = new Dictionary<ushort, IChildObject>();
    public IDictionary<ushort, IAssetProvider> AssetProviders { get; } = new Dictionary<ushort, IAssetProvider>();

    public static readonly PackedScene PackedScene = ResourceLoader.Load<PackedScene>("res://Scenes/Objects/RootObjects/PlayerCharacterController.tscn");

    public bool Debug = true;

    public Avatar Avatar;
    public string AvatarPrefab
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateAvatar();
        }
    } = "builtin://Assets/Prefabs/johnlumoravrhumanoid.prefab";

    // We'll use CustomPlayerSync instead of these
    // [Export] public MultiplayerSynchronizer ClientSync;
    // [Export] public MultiplayerSynchronizer ServerSync;

    [Export] public float UserHeight;
    [Export] public Vector2 MovementInput;
    [Export] public byte MovementButtons; // 0 = jump, 1 = sprint

    public bool JumpInput => (MovementButtons & (1 << 0)) > 0;
    public bool SprintInput => (MovementButtons & (1 << 1)) > 0;

    [Export] public Vector3 HeadPosition;
    [Export] public Quaternion HeadRotation = Quaternion.Identity;

    [Export] public Vector3 LeftHandPosition;
    [Export] public Quaternion LeftHandRotation = Quaternion.Identity;

    [Export] public Vector3 RightHandPosition;
    [Export] public Quaternion RightHandRotation = Quaternion.Identity;

    [Export] public Vector3 HipPosition;
    [Export] public Quaternion HipRotation = Quaternion.Identity;

    [Export] public Vector3 LeftFootPosition;
    [Export] public Quaternion LeftFootRotation = Quaternion.Identity;

    [Export] public Vector3 RightFootPosition;
    [Export] public Quaternion RightFootRotation = Quaternion.Identity;
    [Export]
    public Vector3 SyncVelocity
    {
        get => Velocity;
        set => Velocity = value;
    }
    [Export]
    public int Authority
    {
        get;
        set
        {
            field = value;
        }
    }

    [Export] public float CrouchRatio = 0.5f;
    [Export] public float CrouchSpeed = 2f;
    [Export] public float WalkSpeed = 5f;
    [Export] public float RunSpeed = 8f;
    [Export] public float JumpHeight = 1f;
    private Vector3 targetVelocity;
    private float moveSpeed;

    [Export] public Node3D Nametag;
    [Export] private Node3D _head;
    [Export] private Node3D _leftHand;
    [Export] private Node3D _rightHand;
    [Export] private Node3D _hip;
    [Export] private Node3D _leftFoot;
    [Export] private Node3D _rightFoot;
    [Export] public string PlayerName { get; set; } = "John LumoraVR";

    private void UpdateAvatar()
    {
        if (Avatar is not null)
        {
            RemoveChild(Avatar);
            Avatar.QueueFree();
            Avatar = null;
        }
        Avatar = new Avatar();
        AddChild(Avatar);
        Avatar.Parent = this;
        Prefab.SpawnObject(Avatar, AvatarPrefab);
    }

    public override void _Ready()
    {
        base._Ready();

        if (Authority == Multiplayer.GetUniqueId())
        {
            PlayerName = System.Environment.MachineName;
            (Nametag as Nameplate)?.SetText(PlayerName);
        }

        UpdateAvatar();

        Logger.Log("PlayerCharacterController initialized.");
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        var deltaf = (float)delta;
        var authority = Authority == Multiplayer.GetUniqueId();

        if (authority)
        {
            (HeadPosition, HeadRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.Head);
            (LeftHandPosition, LeftHandRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.LeftHand);
            (RightHandPosition, RightHandRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.RightHand);
            (HipPosition, HipRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.Hip);
            (LeftFootPosition, LeftFootRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.LeftFoot);
            (RightFootPosition, RightFootRotation) = IInputProvider.LimbTransform(IInputProvider.InputLimb.RightFoot);
            UserHeight = IInputProvider.Height;
            MovementButtons = (byte)(((IInputProvider.JumpInput ? 1 : 0) << 0) | ((IInputProvider.SprintInput ? 1 : 0) << 1));
            MovementInput = IInputProvider.MovementInputAxis;
            Position += IInputProvider.PlayspaceMovementDelta;
        }

        if (Avatar is null)
        {
            if (MultiplayerScene.Instance.Prefabs.TryGetValue(AvatarPrefab, out var prefab) && prefab.Type == RootObjectType.Avatar && prefab.Valid())
            {
                Avatar = prefab.Instantiate() as Avatar;
                AddChild(Avatar);
            }
        }

        var yVel = IsOnFloor() ? 0 : Velocity.Y - (9.8f * deltaf);

        var headRotationFlat = ((HeadRotation * Vector3.Right) * new Vector3(1, 0, 1)).Normalized();
        var headRotation = new Vector2(headRotationFlat.X, headRotationFlat.Z).Angle();
        var movementRotated = MovementInput.Rotated(headRotation);

        var targetSpeed = WalkSpeed;
        if (HeadPosition.Y < (UserHeight * CrouchRatio)) targetSpeed = CrouchSpeed;
        else if (SprintInput) targetSpeed = RunSpeed;

        moveSpeed = Mathf.Lerp(moveSpeed, targetSpeed, deltaf * 10);

        if (IsOnFloor())
        {
            if (movementRotated.Length() > 0)
            {
                targetVelocity = new Vector3(movementRotated.X, 0, movementRotated.Y) * moveSpeed;
            }
            else
            {
                targetVelocity.X = Mathf.Lerp(targetVelocity.X, movementRotated.X * moveSpeed, deltaf * 10);
                targetVelocity.Z = Mathf.Lerp(targetVelocity.Z, movementRotated.Y * moveSpeed, deltaf * 10);
            }
        }
        else
        {
            if (movementRotated.Length() > 0)
            {
                targetVelocity.X = Mathf.Lerp(targetVelocity.X, movementRotated.X * moveSpeed, deltaf * 10);
                targetVelocity.Z = Mathf.Lerp(targetVelocity.Z, movementRotated.Y * moveSpeed, deltaf * 10);
            }
        }

        Velocity = targetVelocity;
        Velocity += new Vector3(0, yVel, 0);

        if (IsOnFloor() && JumpInput)
        {
            // Math to make the player jump a prcise height in this case JumpHeight
            Velocity += new Vector3(0, Mathf.Sqrt(2f * 9.8f * JumpHeight), 0);
        }

        MoveAndSlide();

        if (authority)
        {
            IInputProvider.Move(GlobalTransform);
            _head.Scale = Vector3.Zero;
        }
        else
        {
            _head.Transform = new Transform3D(new Basis(HeadRotation), HeadPosition);
            _head.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;
        }

        Nametag.Position = HeadPosition + new Vector3(0, 0.5f, 0);
        Nametag.SetVisible(true);

        if (Position.Y < -100)
        {
            Position = Vector3.Zero;
        }

        // Debug limb position code
        // TODO: Don't remove this is John LumoraVRs debug visuals, if you must remove it, please replace the top section with the code below
        // DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(HeadRotation), HeadPosition).Origin, 0.025f, Colors.White);
        // we want to keep debug code just incase something happens or as a fallback if we end up keeping the library in the release build.
        if (ClientManager.ShowDebug)
        {
            // John LumoraVRs head and eyes
            var headPos = GlobalTransform * new Transform3D(new Basis(HeadRotation), HeadPosition);
            if (!authority)
            {
                DebugDraw3D.DrawPosition(headPos);
                DebugDraw3D.DrawSphere(headPos.Origin + (HeadRotation * new Vector3(0.085f, 0.0f, -0.175f)), 0.0125f, Colors.Black);
                DebugDraw3D.DrawSphere(headPos.Origin + (HeadRotation * new Vector3(0.085f, 0.0f, -0.125f)), 0.05f, Colors.White);
                DebugDraw3D.DrawSphere(headPos.Origin + (HeadRotation * new Vector3(-0.085f, 0.0f, -0.175f)), 0.0125f, Colors.Black);
                DebugDraw3D.DrawSphere(headPos.Origin + (HeadRotation * new Vector3(-0.085f, 0.0f, -0.125f)), 0.05f, Colors.White);
            }

            // Limb debug point visuals
            DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(LeftHandRotation), LeftHandPosition).Origin, 0.025f, Colors.White);
            DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(RightHandRotation), RightHandPosition).Origin, 0.025f, Colors.White);
            DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(HipRotation), HipPosition).Origin, 0.025f, Colors.White);
            DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(LeftFootRotation), LeftFootPosition).Origin, 0.025f, Colors.White);
            DebugDraw3D.DrawSphere(GlobalTransform * new Transform3D(new Basis(RightFootRotation), RightFootPosition).Origin, 0.025f, Colors.White);
        }

        // Temp proxy code
        // TODO: Please remove this once avatars are properly implemented, this was a temp solution to have a "Head and Hands" avatar.
        _leftHand.Transform = new Transform3D(new Basis(LeftHandRotation), LeftHandPosition);
        _leftHand.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;

        _rightHand.Transform = new Transform3D(new Basis(RightHandRotation), RightHandPosition);
        _rightHand.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;

        _hip.Transform = new Transform3D(new Basis(HipRotation), HipPosition);
        _hip.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;

        _leftFoot.Transform = new Transform3D(new Basis(LeftFootRotation), LeftFootPosition);
        _leftFoot.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;

        _rightFoot.Transform = new Transform3D(new Basis(RightFootRotation), RightFootPosition);
        _rightFoot.Scale = ClientManager.ShowDebug ? Vector3.Zero : Vector3.One;
    }
    public override void _Input(InputEvent @event)
    {
        base._Input(@event);

        if (Authority != Multiplayer.GetUniqueId())
        {
            return;
        }

        if (@event.IsActionPressed("Respawn"))
        {
            Respawn();
        }
    }
    public void Respawn()
    {
        Position = Vector3.Zero;
    }
    public Vector3 GetLimbPosition(IInputProvider.InputLimb limb) =>
        limb switch
        {
            IInputProvider.InputLimb.Head => HeadPosition,
            IInputProvider.InputLimb.LeftHand => LeftHandPosition,
            IInputProvider.InputLimb.RightHand => RightHandPosition,
            IInputProvider.InputLimb.Hip => HipPosition,
            IInputProvider.InputLimb.LeftFoot => LeftFootPosition,
            IInputProvider.InputLimb.RightFoot => RightFootPosition,
            _ => Vector3.Zero,
        };
    public Quaternion GetLimbRotation(IInputProvider.InputLimb limb) =>
        limb switch
        {
            IInputProvider.InputLimb.Head => HeadRotation,
            IInputProvider.InputLimb.LeftHand => LeftHandRotation,
            IInputProvider.InputLimb.RightHand => RightHandRotation,
            IInputProvider.InputLimb.Hip => HipRotation,
            IInputProvider.InputLimb.LeftFoot => LeftFootRotation,
            IInputProvider.InputLimb.RightFoot => RightFootRotation,
            _ => Quaternion.Identity,
        };
    public void SetPlayerAuthority(int id) => Authority = id;
    public void Initialize(Godot.Collections.Dictionary<string, Variant> data)
    {
        throw new NotImplementedException();
    }
    public void AddChildObject(ISceneObject obj)
    {
        throw new NotImplementedException();
    }
}
