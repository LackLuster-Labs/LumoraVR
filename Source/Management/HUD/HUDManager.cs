using LumoraVR.Source.Input;
using LumoraVR.Source.Logging;
using Godot;

namespace LumoraVR.Source.Management.HUD;

public partial class HUDManager : Control
{
    public static HUDManager Instance;

    [Export] public Control DebugOverlay;

    [Export] public Control MainMenu;

    public override void _Ready()
    {
        base._Ready();
        Instance = this;
        Logger.Log("HUD initialized.");
    }

    public override void _Input(InputEvent @event)
    {
        base._Input(@event);

        if (@event.IsActionPressed("ToggleDebugUI"))
        {
            InputManager.MovementLocked = !InputManager.MovementLocked;
            DebugOverlay.Visible = InputManager.MovementLocked;
        }

        if (@event.IsActionPressed("ui_cancel"))
        {
            InputManager.MovementLocked = !InputManager.MovementLocked;
            MainMenu.Visible = InputManager.MovementLocked;
        }

        if (@event.IsActionPressed("ui_home"))
        {
            Logger.Log($"Sample log message.");
            Logger.Warn($"Sample warn message.");
            Logger.Error($"Sample error message.");
            Logger.Debug($"Sample debug message.");
        }
    }
}
