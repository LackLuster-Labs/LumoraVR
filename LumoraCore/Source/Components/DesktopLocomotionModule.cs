using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// Desktop locomotion module: WASD movement + space jump, oriented by head facing.
/// </summary>
public class DesktopLocomotionModule : ILocomotionModule
{
    private LocomotionController _owner;
    private CharacterController _characterController;
    private InputInterface _inputInterface;
    private IKeyboardDriver _keyboardDriver;
    private bool _wasJumpPressed;

    public void Activate(LocomotionController owner)
    {
        _owner = owner;
        _characterController = owner?.CharacterController;
        _inputInterface = Engine.Current?.InputInterface;
        _keyboardDriver = _inputInterface?.GetKeyboardDriver();
        // Capture mouse for desktop look by default
        LocomotionController.SetMouseCaptureRequested(true);
    }

    public void Deactivate()
    {
        // No-op for now
    }

    public void Update(float delta)
    {
        if (_owner == null || _characterController == null || !_characterController.IsReady)
            return;

        // Refresh input interface if it became available
        if (_inputInterface == null && Engine.Current?.InputInterface != null)
        {
            _inputInterface = Engine.Current.InputInterface;
            _keyboardDriver = _inputInterface.GetKeyboardDriver();
        }

        if (_keyboardDriver == null)
            return;

        // WASD input
        float2 inputDir = float2.Zero;
        if (_keyboardDriver.GetKeyState(Key.W)) inputDir.y += 1;
        if (_keyboardDriver.GetKeyState(Key.S)) inputDir.y -= 1;
        if (_keyboardDriver.GetKeyState(Key.A)) inputDir.x -= 1;
        if (_keyboardDriver.GetKeyState(Key.D)) inputDir.x += 1;
        inputDir = inputDir.Normalized;

        float3 moveDir = float3.Zero;
        if (inputDir.LengthSquared > 0.001f)
        {
            _owner.GetMovementBasis(out var forward, out var right);
            moveDir = (forward * inputDir.y + right * inputDir.x).Normalized;
        }

        _characterController.SetMovementDirection(moveDir);

        // Jump
        bool isJumpPressed = _keyboardDriver.GetKeyState(Key.Space);
        if (isJumpPressed && !_wasJumpPressed)
        {
            _characterController.RequestJump();
        }
        _wasJumpPressed = isJumpPressed;
    }

    public void Dispose()
    {
        Deactivate();
        _owner = null;
        _characterController = null;
        _inputInterface = null;
        _keyboardDriver = null;
    }
}
