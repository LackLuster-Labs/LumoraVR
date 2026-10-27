// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components;

// Every undo step description in one place so the keys and the English behind them cannot drift
// apart across the dozen files that record steps. Args-taking entries are methods; the rest are
// static readonly values built once.
public static class UndoLocale
{
    public static readonly LocaleText Undo = LocaleText.Keyed("Undo.Action.Undo", "Undo");
    public static readonly LocaleText Redo = LocaleText.Keyed("Undo.Action.Redo", "Redo");

    public static LocaleText UndoNamed(string step) => LocaleText.Keyed("Undo.Action.UndoNamed", "Undo {0}", step);
    public static LocaleText RedoNamed(string step) => LocaleText.Keyed("Undo.Action.RedoNamed", "Redo {0}", step);

    public static readonly LocaleText Batch = LocaleText.Keyed("Undo.Step.Batch", "Multiple Changes");
    public static readonly LocaleText Destroy = LocaleText.Keyed("Undo.Step.Destroy", "Destroy");
    public static readonly LocaleText Duplicate = LocaleText.Keyed("Undo.Step.Duplicate", "Duplicate");
    public static readonly LocaleText AddChild = LocaleText.Keyed("Undo.Step.AddChild", "Add Child");
    public static readonly LocaleText Spawn = LocaleText.Keyed("Undo.Step.Spawn", "Spawn");
    public static readonly LocaleText SpawnItem = LocaleText.Keyed("Undo.Step.SpawnItem", "Spawn Item");
    public static readonly LocaleText SpawnLight = LocaleText.Keyed("Undo.Step.SpawnLight", "Spawn Light");
    public static readonly LocaleText SpawnShape = LocaleText.Keyed("Undo.Step.SpawnShape", "Spawn Shape");
    public static readonly LocaleText BrushStroke = LocaleText.Keyed("Undo.Step.BrushStroke", "Brush Stroke");
    public static readonly LocaleText EraseStrokes = LocaleText.Keyed("Undo.Step.EraseStrokes", "Erase Strokes");
    public static readonly LocaleText SplitConvexHulls = LocaleText.Keyed("Undo.Step.SplitConvexHulls", "Split Into Convex Hulls");
    public static readonly LocaleText ImportShader = LocaleText.Keyed("Undo.Step.ImportShader", "Import Shader");

    public static readonly LocaleText Move = LocaleText.Keyed("Undo.Step.Move", "Move");
    public static readonly LocaleText Rotate = LocaleText.Keyed("Undo.Step.Rotate", "Rotate");
    public static readonly LocaleText Scale = LocaleText.Keyed("Undo.Step.Scale", "Scale");
    public static readonly LocaleText ResetPosition = LocaleText.Keyed("Undo.Step.ResetPosition", "Reset Position");
    public static readonly LocaleText ResetRotation = LocaleText.Keyed("Undo.Step.ResetRotation", "Reset Rotation");
    public static readonly LocaleText ResetScale = LocaleText.Keyed("Undo.Step.ResetScale", "Reset Scale");
    public static readonly LocaleText ResetTransform = LocaleText.Keyed("Undo.Step.ResetTransform", "Reset All Transforms");
    public static readonly LocaleText BringTo = LocaleText.Keyed("Undo.Step.BringTo", "Bring To");
    public static readonly LocaleText InsertParent = LocaleText.Keyed("Undo.Step.InsertParent", "Insert Parent");
    public static readonly LocaleText CreatePivot = LocaleText.Keyed("Undo.Step.CreatePivot", "Create Pivot");
    public static readonly LocaleText Glue = LocaleText.Keyed("Undo.Step.Glue", "Glue");
    public static readonly LocaleText GlueGroup = LocaleText.Keyed("Undo.Step.GlueGroup", "Glue Group");
    public static readonly LocaleText ApplyMaterial = LocaleText.Keyed("Undo.Step.ApplyMaterial", "Apply Material");
    public static readonly LocaleText ConvertMaterials = LocaleText.Keyed("Undo.Step.ConvertMaterials", "Convert Materials");
    public static readonly LocaleText ExtractMaterials = LocaleText.Keyed("Undo.Step.ExtractMaterials", "Extract Materials");

    public static readonly LocaleText ListAdd = LocaleText.Keyed("Undo.Step.ListAdd", "Add List Element");
    public static readonly LocaleText ListRemove = LocaleText.Keyed("Undo.Step.ListRemove", "Remove List Element");
    public static readonly LocaleText ListReorder = LocaleText.Keyed("Undo.Step.ListReorder", "Reorder List");

    // Drive authoring records these; the link layer owns the call sites.
    public static readonly LocaleText TakeDrive = LocaleText.Keyed("Undo.Step.TakeDrive", "Take Drive");
    public static readonly LocaleText ReleaseDrive = LocaleText.Keyed("Undo.Step.ReleaseDrive", "Release Drive");
    public static readonly LocaleText BreakDrive = LocaleText.Keyed("Undo.Step.BreakDrive", "Break Drive");

    public static readonly LocaleText PreserveAssets =
        LocaleText.Keyed("Undo.Step.PreserveAssets", "Destroy Preserving Assets");

    public static LocaleText EditField(string member) => LocaleText.Keyed("Undo.Step.EditField", "Edit {0}", member);

    public static LocaleText AttachComponent(string type)
        => LocaleText.Keyed("Undo.Step.AttachComponent", "Attach {0}", type);

    public static LocaleText DestroyComponent(string type)
        => LocaleText.Keyed("Undo.Step.DestroyComponent", "Destroy {0}", type);

    public static LocaleText TypeSwap(string from, string to)
        => LocaleText.Keyed("Undo.Step.TypeSwap", "Swap {0} to {1}", from, to);

    public static LocaleText Reparent(string slot) => LocaleText.Keyed("Undo.Step.Reparent", "Reparent {0}", slot);

    public static LocaleText File(string slot) => LocaleText.Keyed("Undo.Step.File", "File {0}", slot);

    public static LocaleText SpawnNamed(string name) => LocaleText.Keyed("Undo.Step.SpawnNamed", "Spawn {0}", name);
}
