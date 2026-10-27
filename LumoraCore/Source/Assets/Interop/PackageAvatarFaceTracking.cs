// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// A packaged avatar's face tracking and eyes onto ours. The package names a shape for each expression and each
// eye channel by driving that entry of a renderer's weights, as its mouth does for visemes, and ours keeps the same
// lists: the mouth's on MouthExpressionDriver, mesh by mesh, the lids on BlinkDriver, widen, squeeze, frown and
// pupils on EyeExpressionDriver, each with the package's own ranges. An expression made of nothing our tracker
// measures is left out and listed in the log, never stood in for by something else. -xlinka
public static partial class PackageObjectImporter
{
    // The package's expressions that ours computes, by the package's name.
    private static readonly Dictionary<string, FaceExpression> PackageExpressions = new(StringComparer.Ordinal)
    {
        ["Smile"] = FaceExpression.Smile,
        ["SmileLeft"] = FaceExpression.SmileLeft,
        ["SmileRight"] = FaceExpression.SmileRight,
        ["SmileClosed"] = FaceExpression.SmileClosed,
        ["SmileClosedLeft"] = FaceExpression.SmileClosedLeft,
        ["SmileClosedRight"] = FaceExpression.SmileClosedRight,
        ["Grin"] = FaceExpression.Grin,
        ["GrinLeft"] = FaceExpression.GrinLeft,
        ["GrinRight"] = FaceExpression.GrinRight,
        ["SmirkLeft"] = FaceExpression.SmirkLeft,
        ["SmirkRight"] = FaceExpression.SmirkRight,
        ["Frown"] = FaceExpression.Frown,
        ["FrownLeft"] = FaceExpression.FrownLeft,
        ["FrownRight"] = FaceExpression.FrownRight,
        ["JawOpen"] = FaceExpression.JawOpen,
        ["JawLeft"] = FaceExpression.JawLeft,
        ["JawRight"] = FaceExpression.JawRight,
        ["JawHorizontal"] = FaceExpression.JawSideways,
        ["JawForward"] = FaceExpression.JawForward,
        // A pout, a cheek suck, a lip tucked in or rolled out: the tracker measures each as one action of both
        // sides, so either side of it is that same reading.
        ["Pout"] = FaceExpression.Pout,
        ["PoutLeft"] = FaceExpression.Pout,
        ["PoutRight"] = FaceExpression.Pout,
        ["CheekPuff"] = FaceExpression.CheekPuff,
        ["CheekPuffLeft"] = FaceExpression.CheekPuffLeft,
        ["CheekPuffRight"] = FaceExpression.CheekPuffRight,
        ["CheekSuck"] = FaceExpression.CheekSuck,
        ["CheekSuckLeft"] = FaceExpression.CheekSuck,
        ["CheekSuckRight"] = FaceExpression.CheekSuck,
        ["LipRaiseUpper"] = FaceExpression.UpperLipRaise,
        ["LipRaiseUpperLeft"] = FaceExpression.UpperLipRaiseLeft,
        ["LipRaiseUpperRight"] = FaceExpression.UpperLipRaiseRight,
        ["LipRaiseLower"] = FaceExpression.LowerLipDown,
        ["LipRaiseLowerLeft"] = FaceExpression.LowerLipDownLeft,
        ["LipRaiseLowerRight"] = FaceExpression.LowerLipDownRight,
        ["LipMoveLeftUpper"] = FaceExpression.UpperLipLeft,
        ["LipMoveRightUpper"] = FaceExpression.UpperLipRight,
        ["LipMoveHorizontalUpper"] = FaceExpression.UpperLipSideways,
        ["LipMoveLeftLower"] = FaceExpression.LowerLipLeft,
        ["LipMoveRightLower"] = FaceExpression.LowerLipRight,
        ["LipMoveHorizontalLower"] = FaceExpression.LowerLipSideways,
        ["LipTopOverturn"] = FaceExpression.UpperLipOverturn,
        ["LipTopLeftOverturn"] = FaceExpression.UpperLipOverturn,
        ["LipTopRightOverturn"] = FaceExpression.UpperLipOverturn,
        ["LipBottomOverturn"] = FaceExpression.LowerLipOverturn,
        ["LipBottomLeftOverturn"] = FaceExpression.LowerLipOverturn,
        ["LipBottomRightOverturn"] = FaceExpression.LowerLipOverturn,
        ["LipUnderlayUpper"] = FaceExpression.UpperLipTuck,
        ["LipUnderlayUpperLeft"] = FaceExpression.UpperLipTuck,
        ["LipUnderlayUpperRight"] = FaceExpression.UpperLipTuck,
        ["LipUnderlayLower"] = FaceExpression.LowerLipTuck,
        ["LipUnderlayLowerLeft"] = FaceExpression.LowerLipTuck,
        ["LipUnderlayLowerRight"] = FaceExpression.LowerLipTuck,
        ["LipOverlayLower"] = FaceExpression.LowerLipOverlay,
        ["LipOverlayLowerLeft"] = FaceExpression.LowerLipOverlay,
        ["LipOverlayLowerRight"] = FaceExpression.LowerLipOverlay,
        ["TongueOut"] = FaceExpression.TongueOut,
        ["TongueRaise"] = FaceExpression.TongueRaise,
        ["TongueExtend"] = FaceExpression.TongueExtend,
        ["TongueUp"] = FaceExpression.TongueUp,
        ["TongueDown"] = FaceExpression.TongueDown,
        ["TongueLeft"] = FaceExpression.TongueLeft,
        ["TongueRight"] = FaceExpression.TongueRight,
        ["TongueVertical"] = FaceExpression.TongueVertical,
        ["TongueHorizontal"] = FaceExpression.TongueSideways,
        ["TongueRoll"] = FaceExpression.TongueRoll,
        ["TongueUpLeft"] = FaceExpression.TongueUpLeft,
        ["TongueUpRight"] = FaceExpression.TongueUpRight,
        ["TongueDownLeft"] = FaceExpression.TongueDownLeft,
        ["TongueDownRight"] = FaceExpression.TongueDownRight,
    };

    // Readings of several channels together, which the package only shows when it may estimate.
    private static bool Reading(FaceExpression expression) => expression is FaceExpression.SmileClosed or FaceExpression.SmileClosedLeft
        or FaceExpression.SmileClosedRight or FaceExpression.Grin or FaceExpression.GrinLeft or FaceExpression.GrinRight
        or FaceExpression.SmirkLeft or FaceExpression.SmirkRight;

    private static void FinishExpressions(BuildContext context)
    {
        if (context.Expressions.Count == 0)
            return;
        var named = NamedShapeEntries(context);
        var copies = WeightCopies(context);
        foreach (var (driver, data) in context.Expressions)
        {
            if (driver.IsDestroyed)
                continue;

            int stored = 0;
            var byMesh = new List<(SkinnedMeshRenderer Mesh, List<(string Shape, FaceExpression Expression, float2 Range)> Entries)>();
            var untracked = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in StoredEntries(data, "ExpressionDrivers"))
            {
                stored++;
                var name = FaceStored(entry, "Expression") as string ?? "?";
                if (!PackageExpressions.TryGetValue(name, out var expression) || (Reading(expression) && FaceStored(entry, "EstimateIfNotTracked") is false))
                {
                    untracked.Add(name);
                    continue;
                }
                if (ReadId(entry, "Target") is not { } id)
                    continue;
                var range = new float2(ReadNumber(entry, "Min") ?? 0f, ReadNumber(entry, "Max") ?? 1f);
                foreach (var (renderer, shape) in Landing(context, named, copies, id))
                {
                    int at = byMesh.FindIndex(m => ReferenceEquals(m.Mesh, renderer));
                    if (at < 0)
                    {
                        byMesh.Add((renderer, new List<(string, FaceExpression, float2)>()));
                        at = byMesh.Count - 1;
                    }
                    byMesh[at].Entries.Add((shape, expression, range));
                }
            }

            // A driver that names nothing drives nothing there; one whose shapes could not be followed keeps
            // guessing by name rather than going still.
            if (byMesh.Count == 0)
                driver.ExactShapes.Value = stored == 0;
            for (int m = 0; m < byMesh.Count; m++)
            {
                var (mesh, entries) = byMesh[m];
                var target = driver;
                if (m > 0)
                {
                    target = driver.Slot.AttachComponent<MouthExpressionDriver>();
                    target.StrengthMultiplier.Value = driver.StrengthMultiplier.Value;
                }
                target.ShapeMesh.Target = mesh;
                target.ShapeNames.Clear();
                target.ShapeExpressions.Clear();
                target.ShapeRanges.Clear();
                foreach (var (shape, expression, range) in entries)
                {
                    target.ShapeNames.Add(shape);
                    target.ShapeExpressions.Add(expression);
                    target.ShapeRanges.Add(range);
                }
                target.ExactShapes.Value = true;
                Logger.Log($"Package import: face tracking on '{mesh.Slot?.SlotName.Value}' named from the package, {entries.Count} shape(s)");
            }
            if (untracked.Count > 0)
                Logger.Log($"Package import: face tracking on '{driver.Slot?.SlotName.Value}' left out, nothing tracked here measures: {string.Join(", ", untracked)}");
        }
    }

    // Lids, widen, squeeze, frown and pupils from the package's eye shape driver; blink timing and glances from its
    // eye manager. All on the avatar's own slot, where equip looks for its face drivers. A shape the package drives
    // through something other than a renderer's weights (a variable a script reads) is not followed.
    private static void FinishEyes(Slot root, BuildContext context)
    {
        if (context.EyeParts.Count == 0)
            return;
        var avatar = root.GetComponentInChildren<AvatarForm>()?.Slot;
        if (avatar == null || avatar.IsDestroyed)
            return;
        var named = NamedShapeEntries(context);
        var copies = WeightCopies(context);
        BlinkDriver? blink = null;
        EyeExpressionDriver? eyes = null;
        var unfollowed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (type, data) in context.EyeParts)
        {
            if (type == "EyeManager")
            {
                blink ??= avatar.GetComponent<BlinkDriver>() ?? avatar.AttachComponent<BlinkDriver>();
                ApplyEyeManager(avatar, blink, data);
                continue;
            }

            foreach (var entry in StoredEntries(data, "Eyes"))
            {
                int side = (FaceStored(entry, "Side") as string) switch { "Left" => 0, "Right" => 1, _ => -1 };
                if (ReadId(entry, "OpenCloseTarget") is { } lid)
                {
                    var hits = Landing(context, named, copies, lid);
                    if (hits.Count == 0)
                        unfollowed.Add("lid");
                    var state = new float2(ReadNumber(entry, "OpenState") ?? 0f, ReadNumber(entry, "ClosedState") ?? 1f);
                    foreach (var (renderer, shape) in hits)
                    {
                        blink ??= avatar.GetComponent<BlinkDriver>() ?? avatar.AttachComponent<BlinkDriver>();
                        blink.LidShapeMeshes.Add(renderer);
                        blink.LidShapeNames.Add(shape);
                        blink.LidShapeSides.Add(side);
                        blink.LidShapeStates.Add(state);
                    }
                }

                // Pupils are measured in millimetres there, 2 to 8; ours run 0 to 1 over the same span.
                AddEyeShape("PupilSizeTarget", 0, "PupilSize", 1f / 6f, -2f / 6f);
                AddEyeShape("WidenTarget", 1, "Widen", 1f, 0f);
                AddEyeShape("SqueezeTarget", 2, "Squeeze", 1f, 0f);
                AddEyeShape("FrownTarget", 3, "Frown", 1f, 0f);

                foreach (var other in new[] { "InnerBrowRaiseTarget", "InnerBrowLowerTarget", "OuterBrowRaiseTarget", "OuterBrowLowerTarget",
                    "LookLeft", "LookUp", "LookRight", "LookDown", "PositionOffset", "ProjectionPlanePoint" })
                {
                    if (ReadId(entry, other) != null)
                        unfollowed.Add(other);
                }

                void AddEyeShape(string member, int channel, string range, float scale, float offset)
                {
                    if (ReadId(entry, member) is not { } id)
                        return;
                    var hits = Landing(context, named, copies, id);
                    if (hits.Count == 0)
                    {
                        unfollowed.Add(member);
                        return;
                    }
                    var remap = new float4(
                        (ReadNumber(entry, "MinInput" + range) ?? 0f) * scale + offset,
                        (ReadNumber(entry, "MaxInput" + range) ?? 1f) * scale + offset,
                        ReadNumber(entry, "MinOutput" + range) ?? 0f,
                        ReadNumber(entry, "MaxOutput" + range) ?? 1f);
                    foreach (var (renderer, shape) in hits)
                    {
                        eyes ??= avatar.GetComponent<EyeExpressionDriver>() ?? avatar.AttachComponent<EyeExpressionDriver>();
                        eyes.ShapeMeshes.Add(renderer);
                        eyes.ShapeNames.Add(shape);
                        eyes.ShapeChannels.Add(channel);
                        eyes.ShapeSides.Add(side);
                        eyes.ShapeRemaps.Add(remap);
                    }
                }
            }
        }

        if (blink != null && blink.LidShapeNames.Count > 0)
            blink.ExactLids.Value = true;
        if (eyes != null && eyes.ShapeNames.Count > 0)
            eyes.ExactShapes.Value = true;
        Logger.Log($"Package import: eyes named from the package, {blink?.LidShapeNames.Count ?? 0} lid shape(s), {eyes?.ShapeNames.Count ?? 0} eye shape(s)"
                 + (unfollowed.Count > 0 ? $"; not followed: {string.Join(", ", unfollowed)}" : ""));
    }

    // Blink timing, and the package's mini-expressions, which are brief looks: a direction (its left is -X) held
    // for a while. Ours keeps them as angles toward the avatar's left and up.
    private static void ApplyEyeManager(Slot avatar, BlinkDriver blink, DataTreeDictionary data)
    {
        if (ReadNumber(data, "MinBlinkInterval") is { } shortest && ReadNumber(data, "MaxBlinkInterval") is { } longest && longest >= shortest)
        {
            blink.MinInterval.Value = shortest;
            blink.MaxInterval.Value = longest;
        }
        // The lids close at a speed in shuts per second and open at the same, so a blink lasts two of them.
        if (ReadNumber(data, "BlinkMinSpeed") is { } slow && ReadNumber(data, "BlinkMaxSpeed") is { } fast && slow + fast > 0f)
            blink.BlinkDuration.Value = 2f / ((slow + fast) * 0.5f);

        var glances = StoredEntries(data, "MiniExpressions").ToList();
        if (glances.Count == 0)
            return;
        var gaze = avatar.GetComponent<EyeGazeDriver>() ?? avatar.AttachComponent<EyeGazeDriver>();
        gaze.GlanceAngles.Clear();
        gaze.GlanceWeights.Clear();
        gaze.GlanceDurations.Clear();
        foreach (var glance in glances)
        {
            var d = PackageMemberMapper.ReadNumberArray(glance, "Direction");
            if (d == null || d.Length < 3)
                continue;
            float toLeft = MathF.Atan2(-d[0], MathF.Max(MathF.Abs(d[2]), 1e-4f)) * 180f / MathF.PI;
            float up = MathF.Atan2(d[1], MathF.Sqrt(d[0] * d[0] + d[2] * d[2])) * 180f / MathF.PI;
            gaze.GlanceAngles.Add(new float2(toLeft, up));
            gaze.GlanceWeights.Add(ReadNumber(glance, "Weight") ?? 1f);
            gaze.GlanceDurations.Add(new float2(ReadNumber(glance, "MinDuration") ?? 0.5f, ReadNumber(glance, "MaxDuration") ?? 2f));
        }
        gaze.GlanceChance.Value = ReadNumber(data, "MiniExpressionProbability") ?? 0f;
        gaze.GlanceInterval.Value = ReadNumber(data, "MiniExpressionInterval") ?? 0f;
    }

    // Every shape a drive at this id lands on: its own, and those a value copy carries it on to (a second mesh that
    // copies the face's lid weights blinks with it).
    private static List<(SkinnedMeshRenderer Renderer, string Shape)> Landing(BuildContext context,
        Dictionary<string, (string Renderer, string Shape)> named,
        Dictionary<(SkinnedMeshRenderer, int), List<(SkinnedMeshRenderer, int)>> copies, string id)
    {
        var hits = new List<(SkinnedMeshRenderer, string)>();
        if (ShapeOf(context, named, id) is not var (renderer, shape))
            return hits;
        hits.Add((renderer, shape));
        var seen = new HashSet<(SkinnedMeshRenderer, int)>();
        var queue = new Queue<(SkinnedMeshRenderer, int)>();
        int index = renderer.GetBlendShapeIndex(shape);
        if (index >= 0)
            queue.Enqueue((renderer, index));
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (!seen.Add(at) || !copies.TryGetValue(at, out var onward))
                continue;
            foreach (var (next, nextIndex) in onward)
            {
                var nextShape = next.BlendShapeName(nextIndex);
                if (!string.IsNullOrEmpty(nextShape) && !seen.Contains((next, nextIndex)))
                {
                    hits.Add((next, nextShape));
                    queue.Enqueue((next, nextIndex));
                }
            }
        }
        return hits;
    }

    // Value copies from one renderer's weight to another's, by the weight they read.
    private static Dictionary<(SkinnedMeshRenderer, int), List<(SkinnedMeshRenderer, int)>> WeightCopies(BuildContext context)
    {
        var copies = new Dictionary<(SkinnedMeshRenderer, int), List<(SkinnedMeshRenderer, int)>>();
        foreach (var element in context.ById.Values.Distinct())
        {
            if (element is not Components.Utility.CopyValue<float> copy || copy.IsDestroyed
                || WeightOf(copy.Source.Target) is not { } from || WeightOf(copy.Target.Field) is not { } to)
                continue;
            if (!copies.TryGetValue(from, out var list))
                copies[from] = list = new List<(SkinnedMeshRenderer, int)>();
            list.Add(to);
        }
        return copies;
    }

    private static (SkinnedMeshRenderer, int)? WeightOf(IWorldElement? field)
    {
        if (field is not Sync<float> weight || (weight.Parent as Networking.Sync.SyncElement)?.Parent is not SkinnedMeshRenderer renderer
            || renderer.IsDestroyed)
            return null;
        int index = renderer.BlendShapeWeights.IndexOfElement(weight);
        return index >= 0 ? (renderer, index) : null;
    }

    // The records of a stored list member.
    private static IEnumerable<DataTreeDictionary> StoredEntries(DataTreeDictionary data, string member)
    {
        if ((data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList list)
            yield break;
        foreach (var node in list)
        {
            if (node is DataTreeDictionary entry)
                yield return entry;
        }
    }

    private static object? FaceStored(DataTreeDictionary data, string member)
        => ((data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value;
}
