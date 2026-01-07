using System;
using Lumora.Core.Logging;
using Godot;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Input;

public partial class VRInput : Node3D, IInputProvider
{
    [Export] private XROrigin3D _origin;
    [Export] private XRController3D _leftHand;
    [Export] private XRController3D _rightHand;
    [Export] private XRController3D _hip;
    [Export] private XRController3D _leftFoot;
    [Export] private XRController3D _rightFoot;
    [Export] private XRCamera3D _head;
    private Vector3 _playspaceDelta;
    private float _rotation;

    // Laser interaction for UI
    private LaserInteractionManager _laserManager;
    private GrabManager _grabManager;

    /// <summary>
    /// Laser interaction manager for VR UI interaction.
    /// </summary>
    public LaserInteractionManager LaserManager => _laserManager;

    public override void _Ready()
    {
        try
        {
            base._Ready();

            IInputProvider.Instance = this;

            ProcessPriority = -9;
            _origin.ProcessPriority = -10;
            _head.ProcessPriority = -10;
            _leftHand.ProcessPriority = -10;
            _rightHand.ProcessPriority = -10;
            _hip.ProcessPriority = -10;
            _leftFoot.ProcessPriority = -10;
            _rightFoot.ProcessPriority = -10;

            // Create laser interaction manager for UI
            _laserManager = new LaserInteractionManager();
            _laserManager.Name = "LaserInteraction";
            AddChild(_laserManager);

            _grabManager = new GrabManager();
            _grabManager.Name = "GrabManager";
            AddChild(_grabManager);

            AquaLogger.Log("XRInput initialized successfully.");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Error during XRInput initialization: {ex.Message}");
        }
    }


    public override void _Process(double delta)
    {
        base._Process(delta);

        var deltaf = (float)delta;

        var globalInverse = GlobalTransform.Inverse();

        var playDelta = (globalInverse * _head.GlobalTransform).Origin with { Y = 0 };

        _rotation += -_rightHand.GetVector2("primary").X * deltaf * Mathf.Pi;

        _origin.Quaternion = Quaternion.FromEuler(new Vector3(0, _rotation, 0));

        var newOffset = -((globalInverse * _head.GlobalTransform).Origin with { Y = 0 });

        //var headPos = _head.Position with { Y = 0 };
        //var currentOriginOffset = _origin.Position with { Y = 0 };

        _playspaceDelta = playDelta;
        _origin.Position += newOffset;
    }
    public bool IsVR => true;
    public Vector3 GetPlayspaceMovementDelta => _playspaceDelta;
    public Vector2 GetMovementInputAxis => _leftHand.GetVector2("primary") * new Vector2(1, -1);
    public bool GetJumpInput => !InputManager.MovementLocked && ((bool)_leftHand.Get("ax_button") == true);
    public bool GetSprintInput => !InputManager.MovementLocked && ((_leftHand.GetVector2("primary") * new Vector2(1, -1)).LengthSquared() >= 0.5f) && ((_rightHand.GetVector2("primary") * new Vector2(1, -1)).LengthSquared() >= 0.5f);
    public float GetHeight => 1.8f; //TODO
    public Vector3 GetLimbPosition(IInputProvider.InputLimb limb) => _origin.Transform.TranslatedLocal(limb switch
    {
        IInputProvider.InputLimb.Head => _head.Position,
        IInputProvider.InputLimb.LeftHand => _leftHand.Position,
        IInputProvider.InputLimb.RightHand => _rightHand.Position,
        IInputProvider.InputLimb.Hip => _hip.Position,
        IInputProvider.InputLimb.LeftFoot => _leftFoot.Position,
        IInputProvider.InputLimb.RightFoot => _rightFoot.Position,
        _ => -_origin.Position,
    }).Origin;
    public Quaternion GetLimbRotation(IInputProvider.InputLimb limb) => _origin.Quaternion * limb switch
    {
        IInputProvider.InputLimb.Head => _head.Quaternion,
        IInputProvider.InputLimb.LeftHand => _leftHand.Quaternion,
        IInputProvider.InputLimb.RightHand => _rightHand.Quaternion,
        //IInputProvider.InputLimb.Hip => expr,
        //IInputProvider.InputLimb.LeftFoot => expr,
        //IInputProvider.InputLimb.RightFoot => expr,
        _ => _origin.Quaternion.Inverse(),
    };
    public void MoveTransform(Transform3D transform) => GlobalTransform = transform;

    // Controller button inputs (VR controllers)
    public bool GetLeftGripInput => (bool)_leftHand.Get("grip_click");
    public bool GetRightGripInput => (bool)_rightHand.Get("grip_click");
    public bool GetLeftTriggerInput => (bool)_leftHand.Get("trigger_click");
    public bool GetRightTriggerInput => (bool)_rightHand.Get("trigger_click");
    public bool GetLeftSecondaryInput => (bool)_leftHand.Get("ax_button"); // A/X button
    public bool GetRightSecondaryInput => (bool)_rightHand.Get("by_button"); // B/Y button
}
