namespace Lumora.Core.Assets;

/// <summary>
/// Hardcoded paths to built-in Lumora assets.
/// </summary>
public static class LumAssets
{
    /// <summary>
    /// Built-in UI scene paths.
    /// </summary>
    public static class UI
    {
        // Core
        public static string Bootstrap => "res://Scenes/UI/Core/Bootstrap.tscn";
        public static string LoadingScreen => "res://Scenes/LoadingScreen.tscn";

        // Inspectors
        public static string Nameplate => "res://Scenes/UI/Inspectors/Nameplate.tscn";
        public static string UserInspector => "res://Scenes/UI/Inspectors/UserInspector.tscn";
        public static string MaterialOrbInspector => "res://Scenes/UI/Inspectors/MaterialOrbInspector.tscn";

        // Debug
        public static string EngineDebug => "res://Scenes/UI/Debug/EngineDebug.tscn";

        // Dashboard
        public static string HomeDash => "res://Scenes/UI/Dashboard/HomeDash.tscn";
        public static string WorldBrowser => "res://Scenes/UI/Dashboard/WorldBrowser.tscn";

        // Dialogs
        public static string ImportDialog => "res://Scenes/UI/Dialogs/ImportDialog.tscn";

        // Input
        public static string VRKeyboard => "res://Scenes/UI/Input/VRKeyboard.tscn";
        public static string VRKeyboardKey => "res://Scenes/UI/Input/VRKeyboardKey.tscn";

        // Components
        public static string WorldCard => "res://Scenes/UI/Components/WorldCard.tscn";
        public static string CategoryButton => "res://Scenes/UI/Components/CategoryButton.tscn";
        public static string ImportOptionButton => "res://Scenes/UI/Components/ImportOptionButton.tscn";
    }
}
