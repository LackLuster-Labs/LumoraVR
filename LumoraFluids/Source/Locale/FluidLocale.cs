// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Localization;

namespace Lumora.Fluids;

// The module's locale keys. Their text lives in the repo's Locale tables with every other key. -xlinka
public static class FluidLocale
{
    private static readonly List<LocaleText> _all = new();

    // Every key below with its English, for the tables' completeness check.
    public static IReadOnlyList<LocaleText> All => _all;

    // TOOL

    public static readonly LocaleText ToolName = K("Tools.Fluid.Name", "Fluid Tool");
    public static readonly LocaleText Kind = K("Tools.Fluid.Kind", "Kind");
    public static readonly LocaleText Colour = K("Tools.Fluid.Colour", "Colour");
    public static readonly LocaleText Picker = K("Tools.Fluid.Picker", "Picker");
    public static readonly LocaleText Mode = K("Tools.Fluid.Mode", "Mode");
    public static readonly LocaleText Flow = K("Tools.Fluid.Flow", "Flow {0}");
    public static readonly LocaleText Thick = K("Tools.Fluid.Thick", "Thick {0}");
    public static readonly LocaleText Sticky = K("Tools.Fluid.Sticky", "Sticky {0}");
    public static readonly LocaleText CleanMine = K("Tools.Fluid.CleanMine", "Clean Mine");
    public static readonly LocaleText NotAllowed = K("Tools.Fluid.NotAllowed", "This world doesn't let you pour fluid");
    public static readonly LocaleText ShareFull = K("Tools.Fluid.ShareFull", "Your oldest fluid is drying up to make room");
    public static readonly LocaleText OnlyYours = K("Tools.Fluid.OnlyYours", "This fluid isn't yours to clean up");
    public static readonly LocaleText ViewOnlyNotice = K("Tools.Fluid.ViewOnlyNotice", "This device shows settled fluid only");
    public static readonly LocaleText Sponge = K("Tools.Fluid.Sponge", "Sponge");

    private static readonly LocaleText[] KindNames =
    {
        K("Tools.Fluid.Kind.Custom", "Custom"),
        K("Tools.Fluid.Kind.Water", "Water"),
        K("Tools.Fluid.Kind.Oil", "Oil"),
        K("Tools.Fluid.Kind.Paint", "Paint"),
        K("Tools.Fluid.Kind.Honey", "Honey"),
        K("Tools.Fluid.Kind.Slime", "Slime"),
        K("Tools.Fluid.Kind.Glue", "Glue"),
        K("Tools.Fluid.Kind.Mud", "Mud"),
    };

    // What the hub says under a kind while it is hovered.
    private static readonly LocaleText[] KindDetails =
    {
        K("Tools.Fluid.Detail.Custom", "your own mix"),
        K("Tools.Fluid.Detail.Water", "runs, splashes, pools"),
        K("Tools.Fluid.Detail.Oil", "slick, slow to settle"),
        K("Tools.Fluid.Detail.Paint", "coats, then dries to a stain"),
        K("Tools.Fluid.Detail.Honey", "thick, coils as it falls"),
        K("Tools.Fluid.Detail.Slime", "holds a mound, clings"),
        K("Tools.Fluid.Detail.Glue", "strings long, then sets"),
        K("Tools.Fluid.Detail.Mud", "heavy, slumps"),
    };

    private static readonly LocaleText[] ModeNames =
    {
        K("Tools.Fluid.Mode.Pour", "Pour"),
        K("Tools.Fluid.Mode.Blob", "Blob"),
        K("Tools.Fluid.Mode.Spray", "Spray"),
    };

    private static readonly LocaleText[] ModeDetails =
    {
        K("Tools.Fluid.Mode.Detail.Pour", "a steady stream"),
        K("Tools.Fluid.Mode.Detail.Blob", "hold to grow it, let go to throw"),
        K("Tools.Fluid.Mode.Detail.Spray", "a cone of fine drops"),
    };

    public static LocaleText KindName(FluidPreset kind) => Pick(KindNames, (int)kind);

    public static LocaleText KindDetail(FluidPreset kind) => Pick(KindDetails, (int)kind);

    public static LocaleText ModeName(FluidPourMode mode) => Pick(ModeNames, (int)mode);

    public static LocaleText ModeDetail(FluidPourMode mode) => Pick(ModeDetails, (int)mode);

    // CREATE NEW

    public static readonly LocaleText RulesTitle = K("Create.Fluid.Rules", "Fluid Rules");
    public static readonly LocaleText RulesExist = K("Create.Fluid.RulesExist", "This world already has its fluid rules, on the world root.");

    // UNDO

    public static readonly LocaleText PourFluid = K("Undo.Step.PourFluid", "Pour Fluid");

    // INSPECTOR

    public static readonly LocaleText InspectorLitres = K("Inspector.Fluid.Litres", "{0} L");
    public static readonly LocaleText InspectorCells = K("Inspector.Fluid.Cells", "{0} cells");
    public static readonly LocaleText InspectorParticles = K("Inspector.Fluid.Particles", "{0} particles here");
    public static readonly LocaleText InspectorKeeper = K("Inspector.Fluid.Keeper", "Keeper");
    public static readonly LocaleText KeeperYou = K("Inspector.Fluid.Keeper.You", "you");
    public static readonly LocaleText KeeperHost = K("Inspector.Fluid.Keeper.Host", "the host");
    public static readonly LocaleText KeeperNobody = K("Inspector.Fluid.Keeper.Nobody", "nobody: no GPU here");
    public static readonly LocaleText PathGpu = K("Inspector.Fluid.Path.Gpu", "GPU, {0}");
    public static readonly LocaleText PathViewOnly = K("Inspector.Fluid.Path.ViewOnly", "view only");
    public static readonly LocaleText GpuTime = K("Inspector.Fluid.GpuTime", "{0} ms GPU, {1} dispatches");
    public static readonly LocaleText Degrade = K("Inspector.Fluid.Degrade", "Degrade step {0}");
    public static readonly LocaleText OverHeadsetBudget = K("Inspector.Fluid.OverHeadsetBudget", "over the headset budget");
    public static readonly LocaleText GridBuilding = K("Inspector.Fluid.GridBuilding", "building the mesh grid");
    public static readonly LocaleText SlabOverflow = K("Inspector.Fluid.SlabOverflow", "surface slabs overflowed");
    public static readonly LocaleText NeighbourOverflow = K("Inspector.Fluid.NeighbourOverflow", "neighbour lists overflowed");
    public static readonly LocaleText LedgerOverBudget = K("Inspector.Fluid.LedgerOverBudget", "the ledger is over its byte budget");
    public static readonly LocaleText Monolayer = K("Inspector.Fluid.Monolayer", "one layer only: the spacing is too coarse for this volume");
    public static readonly LocaleText OverlapSplit = K("Inspector.Fluid.OverlapSplit", "a collider search came back full and was split");
    public static readonly LocaleText NotRealTime = K("Inspector.Fluid.NotRealTime", "this machine is not drawing in real time");
    public static readonly LocaleText Settle = K("Inspector.Fluid.Settle", "Settle");
    public static readonly LocaleText Clear = K("Inspector.Fluid.Clear", "Clear");
    public static readonly LocaleText ClearStains = K("Inspector.Fluid.ClearStains", "Clear stains");
    public static readonly LocaleText MeasureGpu = K("Inspector.Fluid.MeasureGpu", "Measure GPU");
    public static readonly LocaleText DebugView = K("Inspector.Fluid.DebugView", "Debug view");

    private static readonly LocaleText[] DebugViews =
    {
        K("Inspector.Fluid.DebugView.Off", "Off"),
        K("Inspector.Fluid.DebugView.Particles", "Particles"),
        K("Inspector.Fluid.DebugView.Bonds", "Bonds"),
        K("Inspector.Fluid.DebugView.Anchors", "Anchors"),
        K("Inspector.Fluid.DebugView.Sleep", "Sleep"),
        K("Inspector.Fluid.DebugView.Strands", "Strands"),
        K("Inspector.Fluid.DebugView.Proxies", "Proxies"),
        K("Inspector.Fluid.DebugView.Skin", "Skin"),
    };

    public static readonly LocaleText MachineQuality = K("Inspector.Fluid.MachineQuality", "This machine");

    // -1 Auto, 0 view only, then the tiers Standalone to Ultra: the values of Modules.Fluids.Quality, shifted by one.
    private static readonly LocaleText[] Qualities =
    {
        K("Inspector.Fluid.MachineQuality.Auto", "Auto"),
        K("Inspector.Fluid.MachineQuality.ViewOnly", "View only"),
        K("Inspector.Fluid.MachineQuality.Standalone", "Standalone"),
        K("Inspector.Fluid.MachineQuality.Low", "Low"),
        K("Inspector.Fluid.MachineQuality.Medium", "Medium"),
        K("Inspector.Fluid.MachineQuality.High", "High"),
        K("Inspector.Fluid.MachineQuality.Ultra", "Ultra"),
    };

    public static int DebugViewCount => DebugViews.Length;

    public static LocaleText DebugViewName(int view) => Pick(DebugViews, view);

    public static LocaleText QualityName(int quality) => Pick(Qualities, quality + 1);

    // SELF MENU

    public static readonly LocaleText CleanOff = K("Menu.Self.CleanOff", "Clean off");
    public static readonly LocaleText FluidStick = K("Menu.Self.FluidStick", "Goo: {0}");

    private static readonly LocaleText[] StickModes =
    {
        K("Menu.Self.FluidStick.Sticky", "Sticky"),
        K("Menu.Self.FluidStick.Wet", "Wet"),
        K("Menu.Self.FluidStick.Off", "Off"),
    };

    public static LocaleText StickModeName(FluidStickMode mode) => Pick(StickModes, (int)mode);

    // LOOM

    public static readonly LocaleText ResidueBelongsToWearer = K("Loom.Fluid.ResidueBelongsToWearer", "residue belongs to its wearer");

    private static LocaleText K(string key, string english)
    {
        var text = LocaleText.Keyed(key, english);
        _all.Add(text);
        return text;
    }

    private static LocaleText Pick(LocaleText[] list, int index)
        => (uint)index < (uint)list.Length ? list[index] : list[0];
}
