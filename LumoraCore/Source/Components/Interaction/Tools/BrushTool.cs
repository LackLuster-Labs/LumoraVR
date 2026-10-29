// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// Draws strokes in the air. Hold primary and move; let go and the stroke is a thing of its own you can grab,
// save, undo or rub out. The tip decides what a stroke becomes, and the ring changes it.
//
// In a headset the stroke comes off the brush's own tip, the trigger's pull is the pressure and the beam stays
// off so it never clicks a panel across the room mid-stroke. On a desktop the stroke rides the pointer: pressed
// on something it follows that surface, lifted half a stroke off it, and pressed on nothing it is drawn on a
// pane facing you at arm's length, the way you would draw on glass.
//
// Points are laid by distance, never per frame, after the hand is smoothed: a slow careful line and a fast
// flick come out with the same density, and a shaky hand does not leave a point every millimetre of jitter.
// They go out to everyone else in batches a few times a second, appended to the shared stroke, so a long
// stroke costs the network its new points and not its whole length on every tick. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class BrushTool : EquippableTool
{
    public const float MinWidth = 0.002f;
    public const float MaxWidth = 0.12f;
    public const float MinEraser = 0.01f;
    public const float MaxEraser = 0.25f;
    public const float DefaultWidth = 0.012f;
    public const float DefaultSmoothing = 0.4f;
    public const float DefaultEraser = 0.04f;

    private const double FlushSeconds = 1.0 / 15.0;
    private const int FlushPoints = 24;
    private const float MaxSmoothingSeconds = 0.16f;
    private const float PressureSeconds = 0.05f;
    private const float AirDepth = 1.2f;
    private const float SurfaceStartReach = 3f;

    public readonly Sync<BrushTip> Kind = new();
    public readonly Sync<colorHDR> Color = new();
    // at a full pull, in metres at the user's own size
    public readonly Sync<float> Width = new();
    // 0 draws the hand as it is; 1 irons out a lot of shake for a little lag
    public readonly Sync<float> Smoothing = new();
    public readonly Sync<float> EraserSize = new();

    private static readonly colorHDR EraserTint = new(1f, 0.46f, 0.64f, 1f);

    private static readonly colorHDR[] Swatches =
    {
        new(0.96f, 0.96f, 0.98f, 1f),
        new(0.10f, 0.10f, 0.12f, 1f),
        new(0.95f, 0.26f, 0.30f, 1f),
        new(1.00f, 0.62f, 0.18f, 1f),
        new(1.00f, 0.88f, 0.25f, 1f),
        new(0.36f, 0.86f, 0.42f, 1f),
        new(0.22f, 0.80f, 0.96f, 1f),
        new(0.55f, 0.49f, 0.96f, 1f),
        new(1.00f, 0.45f, 0.78f, 1f),
    };

    // The stroke being drawn and everything about it, on the drawing machine only.
    private Slot? _stroke;
    private StrokeMesh? _mesh;
    private BrushTip _strokeTip;
    private readonly List<float4> _pending = new();
    private readonly List<float3> _pendingFacing = new();
    private readonly List<float4> _tail = new();
    private readonly List<float3> _tailFacing = new();
    private float3 _smoothed;
    private float3 _lastPoint;
    private float3 _facing;
    private float _pressure;
    private double _lastFlush;

    private bool _onSurface;
    private float _depth;
    private float3 _planePoint;
    private float3 _planeNormal;
    private Slot[]? _exclude;

    private bool _erasing;
    private bool _toldNotMine;
    private readonly List<Slot> _erased = new();
    private readonly List<StrokeMesh> _scan = new();

    protected override colorHDR VisualTint => Kind.Value == BrushTip.Eraser ? EraserTint : Color.Value;

    public override bool UsesLaser => !InHeadset;

    public bool IsDrawing => _stroke != null;

    public bool IsErasing => _erasing;

    public StrokeMesh? CurrentStroke => _mesh;

    private static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    private float UserScale
    {
        get
        {
            float scale = Slot?.ActiveUserRoot?.GlobalScale ?? 1f;
            return float.IsFinite(scale) && scale > 1e-4f ? scale : 1f;
        }
    }

    public override void OnInit()
    {
        base.OnInit();
        EquipName.Value = "Brush";
        Kind.Value = BrushTip.Tube;
        Color.Value = Swatches[7];
        Width.Value = DefaultWidth;
        Smoothing.Value = DefaultSmoothing;
        EraserSize.Value = DefaultEraser;
    }

    // The family as it is handed out: one brush per tip, each in a colour that shows the tip off. Any of them
    // changes tip from its ring; the name is only what it is called on the shelf.
    public static string PresetName(BrushTip tip) => tip == BrushTip.Eraser ? "Eraser" : tip + " Brush";

    public void UsePreset(BrushTip tip)
    {
        Kind.Value = tip;
        EquipName.Value = PresetName(tip);
        Color.Value = tip switch
        {
            BrushTip.Ribbon => Swatches[3],
            BrushTip.Tube => Swatches[7],
            BrushTip.Neon => Swatches[6],
            BrushTip.Spline => Swatches[0],
            BrushTip.Confetti => Swatches[8],
            BrushTip.Fill => Swatches[5],
            _ => Color.Value,
        };
    }

    public override void OnStart()
    {
        base.OnStart();
        if (MayShape())
            UpdateNib();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (!MayShape())
            return;
        ApplyVisualTint(VisualTint);
        UpdateNib();
    }

    public override void OnDequipped()
    {
        base.OnDequipped();
        EndAll();
    }

    public override void OnDestroy()
    {
        EndAll();
        base.OnDestroy();
    }

    private void EndAll()
    {
        if (_stroke != null)
            FinishStroke();
        if (_erasing)
            EndErase();
    }

    // The machines whose writes the brush's own look is theirs to make: whoever made it, whoever holds it, and
    // the authority while nobody does.
    private bool MayShape()
        => World != null && Slot != null && (IsMintedHere || Slot.IsUnderLocalUser || (World.IsAuthority && Slot.ActiveUserRoot == null));

    // VISUAL

    // A bead on the tip in the brush's colour and as wide as the stroke it will make; for the eraser, a
    // see-through ball as big as what it rubs out.
    protected override void BuildVisualExtras(Slot visual)
    {
        var nib = EnsureBead(visual, "Nib", float3.Up * 0.0255f, 1f, VisualTint);
        var material = nib?.GetComponent<UnlitMaterial>();
        if (material != null)
            material.BlendMode.Value = BlendMode.Alpha;
    }

    private void UpdateNib()
    {
        var nib = VisualSlot?.FindChild("Nib", recursive: false);
        if (nib == null || nib.IsDestroyed)
            return;
        bool eraser = Kind.Value == BrushTip.Eraser;
        float radius = eraser
            ? System.Math.Clamp(EraserSize.Value, MinEraser, MaxEraser)
            : System.Math.Clamp(Width.Value * 0.5f, 0.0025f, 0.03f);
        var scale = float3.One * radius;
        if (nib.LocalScale.Value != scale)
            nib.LocalScale.Value = scale;
        var tint = VisualTint;
        SetBeadTint(nib, eraser ? new colorHDR(tint.r, tint.g, tint.b, 0.3f) : tint);
    }

    // PRESSES

    public override bool OnPrimaryPress()
    {
        if (_stroke != null || _erasing)
            return true;
        if (!InHeadset && PointerOnPanel())
            return false;
        if (EditingBlocked)
            return false;
        if (Kind.Value == BrushTip.Eraser)
            return BeginErase();
        if (SpawnBlocked)
            return false;
        return BeginStroke();
    }

    public override bool OnPrimaryHold()
    {
        if (_erasing)
        {
            EraseSweep();
            return true;
        }
        if (_stroke == null)
            return false;
        if (_stroke.IsDestroyed || _mesh == null || _mesh.IsDestroyed)
        {
            Forget();
            return false;
        }
        Draw(World?.Time.Delta ?? 0f);
        return true;
    }

    public override bool OnPrimaryRelease()
    {
        if (_erasing)
        {
            EndErase();
            return true;
        }
        if (_stroke == null)
            return false;
        FinishStroke();
        return true;
    }

    // Secondary rubs out the stroke the brush is on, whatever the tip.
    public override bool OnSecondaryPress()
    {
        if (_stroke != null || _erasing || EditingBlocked)
            return false;
        var stroke = StrokeUnderBrush();
        if (stroke == null)
            return false;
        if (!MayErase(stroke.Slot))
        {
            ToolNotice.Say(World, EquipName.Value ?? "Brush", BrushLocale.Mine.Resolve());
            return true;
        }
        RecordUndo(SlotExistenceUndoBatch.Destroy(World, new[] { stroke.Slot }, UndoLocale.EraseStrokes));
        return true;
    }

    // A click on a panel, a button or a touch target is that thing's click, not the start of a stroke on it.
    private bool PointerOnPanel()
    {
        var laser = ActiveTool?.Laser;
        return laser != null && (laser.CurrentPointerTarget != null || laser.CurrentRayTarget != null);
    }

    // DRAWING

    private bool BeginStroke()
    {
        var world = World;
        var root = world?.RootSlot;
        if (world == null || root == null)
            return false;
        if (!SampleBrush(starting: true, out var start, out var facing))
            return false;

        var tip = Kind.Value;
        var colour = Color.Value;
        Slot? slot;
        StrokeMesh? mesh = null;
        using (world.EnterSpawnAllocation())
        {
            slot = TryAddSlot(root, tip + " Stroke");
            if (slot == null)
                return false;
            float scale = UserScale;
            int seed = HashCode.Combine(slot.ReferenceID.GetHashCode(), Environment.TickCount);
            bool built = Guarded(() =>
            {
                slot.GlobalPosition = start;
                slot.GlobalScale = float3.One * scale;
                mesh = slot.AttachComponent<StrokeMesh>();
                mesh.Tip.Value = tip;
                mesh.Tint.Value = colour;
                mesh.Seed.Value = seed;
                var renderer = slot.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = mesh;
                renderer.ShadowCastMode.Value = ShadowCastMode.Off;
                StrokeLook.Apply(slot, renderer, tip, colour);
            });
            if (!built || mesh == null)
            {
                Guarded(() => slot.Destroy());
                return false;
            }
        }

        _stroke = slot;
        _mesh = mesh;
        _strokeTip = tip;
        _pending.Clear();
        _pendingFacing.Clear();
        _smoothed = start;
        _facing = facing;
        _pressure = ReadPressure();
        AddPoint(start, facing);
        Flush();
        ShowTail();
        return true;
    }

    private void Draw(float delta)
    {
        if (!SampleBrush(starting: false, out var raw, out var facing))
            return;
        float tau = MaxSmoothingSeconds * Smoothing.Value * Smoothing.Value;
        float k = tau <= 1e-4f || delta <= 0f ? 1f : 1f - MathF.Exp(-delta / tau);
        _smoothed = float3.Lerp(_smoothed, raw, k);
        _facing = float3.Lerp(_facing, facing, k);
        float p = PressureSeconds <= 0f || delta <= 0f ? 1f : 1f - MathF.Exp(-delta / PressureSeconds);
        _pressure += (ReadPressure() - _pressure) * p;

        float spacing = Spacing() * UserScale;
        float distance = float3.Distance(_smoothed, _lastPoint);
        if (distance >= spacing)
        {
            if (_strokeTip == BrushTip.Confetti)
            {
                // A flick still scatters evenly: the gap is filled in at the spacing, not jumped.
                var direction = (_smoothed - _lastPoint) / distance;
                var from = _lastPoint;
                int steps = System.Math.Min((int)(distance / spacing), 64);
                for (int i = 1; i <= steps; i++)
                    AddPoint(from + direction * (spacing * i), _facing);
            }
            else
            {
                AddPoint(_smoothed, _facing);
            }
        }

        double now = World?.Time.TotalTime ?? 0;
        if (_pending.Count >= FlushPoints || now - _lastFlush >= FlushSeconds)
            Flush();
        ShowTail();
    }

    private void FinishStroke()
    {
        var slot = _stroke;
        var mesh = _mesh;
        if (slot == null || mesh == null || slot.IsDestroyed || mesh.IsDestroyed)
        {
            Forget();
            return;
        }

        // The end goes where the brush let go, not a smoothing lag behind it.
        if (_strokeTip != BrushTip.Confetti && SampleBrush(starting: false, out var end, out var facing)
            && float3.Distance(end, _lastPoint) > Spacing() * UserScale * 0.25f)
            AddPoint(end, facing);
        Flush();
        _tail.Clear();
        _tailFacing.Clear();
        mesh.SetLiveTail(_tail, _tailFacing);

        int count = mesh.Points.Count;
        bool keep = _strokeTip switch
        {
            BrushTip.Ribbon => count >= 2,
            BrushTip.Fill => count >= 3,
            _ => count >= 1,
        };
        if (!keep)
        {
            Guarded(() => slot.Destroy());
            Forget();
            return;
        }

        bool done;
        using (World!.EnterSpawnAllocation())
        {
            done = Guarded(() =>
            {
                mesh.Finished.Value = true;
                var collider = slot.AttachComponent<MeshCollider>();
                collider.Mesh.Target = mesh;
                // Something to grab and stand things on, never a wall in anyone's way.
                collider.CharacterCollider.Value = false;
                slot.AttachComponent<Grabbable>();
            });
        }
        mesh.RefreshNow();
        if (done)
            RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { slot }, UndoLocale.BrushStroke));
        Forget();
    }

    private void Forget()
    {
        _stroke = null;
        _mesh = null;
        _pending.Clear();
        _pendingFacing.Clear();
        _tail.Clear();
        _tailFacing.Clear();
    }

    private void AddPoint(float3 world, float3 facing)
    {
        if (_stroke == null)
            return;
        var local = _stroke.GlobalPointToLocal(world);
        _pending.Add(new float4(local, StrokeWidth()));
        if (_strokeTip == BrushTip.Ribbon)
        {
            var f = _stroke.GlobalDirectionToLocal(facing);
            _pendingFacing.Add(f.LengthSquared > 1e-10f ? f.Normalized : float3.Forward);
        }
        _lastPoint = world;
    }

    private void Flush()
    {
        _lastFlush = World?.Time.TotalTime ?? 0;
        var mesh = _mesh;
        if (_pending.Count == 0 || mesh == null || mesh.IsDestroyed)
            return;
        Guarded(() =>
        {
            mesh.Points.AddRange(_pending);
            if (_pendingFacing.Count > 0)
                mesh.Facing.AddRange(_pendingFacing);
        });
        _pending.Clear();
        _pendingFacing.Clear();
    }

    // What this machine shows past the shared points: the ones not sent yet and the brush itself, so the
    // stroke stays on the tip. Confetti leaves the brush out, or its chips would swim along behind it.
    private void ShowTail()
    {
        var mesh = _mesh;
        if (mesh == null || _stroke == null)
            return;
        _tail.Clear();
        _tailFacing.Clear();
        _tail.AddRange(_pending);
        _tailFacing.AddRange(_pendingFacing);
        if (_strokeTip != BrushTip.Confetti && float3.Distance(_smoothed, _lastPoint) > Spacing() * UserScale * 0.15f)
        {
            _tail.Add(new float4(_stroke.GlobalPointToLocal(_smoothed), StrokeWidth()));
            if (_strokeTip == BrushTip.Ribbon)
            {
                var f = _stroke.GlobalDirectionToLocal(_facing);
                _tailFacing.Add(f.LengthSquared > 1e-10f ? f.Normalized : float3.Forward);
            }
        }
        mesh.SetLiveTail(_tail, _tailFacing);
        mesh.RefreshNow();
    }

    // How far apart points go, in the user's metres. Wide strokes need fewer; the spline lays them far apart
    // on purpose and lets the curve through them do the rest.
    private float Spacing() => _strokeTip switch
    {
        BrushTip.Spline => MathF.Max(0.025f, Width.Value * 3f),
        BrushTip.Confetti => MathF.Max(0.006f, Width.Value * 0.6f),
        BrushTip.Fill => 0.015f,
        _ => MathF.Max(0.003f, Width.Value * 0.5f),
    };

    private float StrokeWidth()
    {
        float width = System.Math.Clamp(Width.Value, MinWidth, MaxWidth);
        return _strokeTip is BrushTip.Spline or BrushTip.Fill ? width : width * _pressure;
    }

    // How hard the trigger is pulled, past the point that starts a stroke. A mouse button is all or nothing.
    private float ReadPressure()
    {
        if (!InHeadset)
            return 1f;
        var input = Engine.Current?.InputInterface;
        var side = (ActiveTool as HandTool)?.Side.Value ?? Chirality.Right;
        var controller = side == Chirality.Left ? input?.LeftController : input?.RightController;
        float trigger = controller?.TriggerValue ?? 1f;
        return System.Math.Clamp((trigger - 0.3f) / 0.7f, 0.12f, 1f);
    }

    // Where the stroke is now and which way its face looks, in world space.
    private bool SampleBrush(bool starting, out float3 point, out float3 facing)
    {
        if (InHeadset || !TryPointerRay(out var origin, out var direction))
        {
            point = Tip;
            facing = -TipForward;
            return true;
        }

        if (direction.LengthSquared <= 1e-8f)
        {
            point = Tip;
            facing = -TipForward;
            return true;
        }
        direction = direction.Normalized;
        float scale = UserScale;

        if (starting)
        {
            // Only a surface within reach is drawn on. The floor twenty metres off is under the pointer whenever
            // you look a little down, and a stroke started there runs off to the horizon.
            if (TryGetAim(out var aim) && aim.HitSlot != null && float3.Distance(origin, aim.Point) <= SurfaceStartReach * scale)
            {
                _onSurface = true;
                _planePoint = aim.Point;
                _planeNormal = aim.Normal;
                _depth = float3.Distance(origin, aim.Point);
            }
            else
            {
                _onSurface = false;
                _depth = AirDepth * scale;
                _planePoint = origin + direction * _depth;
                _planeNormal = -direction;
            }
        }

        // Following a surface only looks as far as a little past where the stroke already is, so sliding off a
        // table's edge carries on over its plane instead of dropping to the floor beyond it.
        float follow = _depth * 1.5f + 0.25f * scale;
        bool landed = false;
        if (_onSurface && !starting && World?.Physics.Raycast(origin, direction, Exclude(), follow) is { } hit
            && hit.Normal.LengthSquared > 1e-8f)
        {
            _planePoint = hit.Point;
            _planeNormal = hit.Normal.Normalized;
            landed = true;
        }

        // The pane or the surface's plane, where the pointer crosses it. Where it does not cross it usefully
        // (behind you, edge on, or far beyond where the stroke is), the stroke stays at the depth it was at.
        float denominator = float3.Dot(direction, _planeNormal);
        float t = MathF.Abs(denominator) > 0.05f ? float3.Dot(_planePoint - origin, _planeNormal) / denominator : -1f;
        bool crosses = landed || (t > 0.1f * scale && t < follow);
        if (!crosses)
            t = _depth;
        _depth = t;
        point = origin + direction * t;
        if (_onSurface && crosses)
            point += _planeNormal * (StrokeLift() * scale);
        facing = _onSurface ? _planeNormal : -direction;
        return true;
    }

    // Half the stroke off the surface, so a tube sits on it and a ribbon lies on it without sinking in.
    private float StrokeLift()
    {
        var tip = _stroke != null ? _strokeTip : Kind.Value;
        if (tip == BrushTip.Fill)
            return 0.002f;
        float width = System.Math.Clamp(Width.Value, MinWidth, MaxWidth);
        return tip == BrushTip.Neon ? width * 0.25f + 0.001f : width * 0.5f + 0.001f;
    }

    private Slot[]? Exclude()
    {
        var own = Slot?.ActiveUserRoot?.Slot;
        if (own == null)
            return null;
        if (_exclude == null || !ReferenceEquals(_exclude[0], own))
            _exclude = new[] { own };
        return _exclude;
    }

    // ERASING

    private bool BeginErase()
    {
        _erasing = true;
        _toldNotMine = false;
        _erased.Clear();
        EraseSweep();
        return true;
    }

    // Strokes are put out of sight as the eraser passes and only taken away when it lets go, so one sweep is
    // one step to undo however many it went through.
    private void EraseSweep()
    {
        if (World == null)
            return;
        float radius = System.Math.Clamp(EraserSize.Value, MinEraser, MaxEraser) * UserScale;
        bool haveCentre = TryEraserCentre(out var centre);

        _scan.Clear();
        foreach (var stroke in StrokeMesh.In(World))
            _scan.Add(stroke);
        foreach (var stroke in _scan)
        {
            if (stroke.IsDestroyed || stroke.Slot == null || !stroke.Slot.ActiveSelf.Value || !stroke.Finished.Value)
                continue;
            if (!haveCentre || !stroke.Touches(centre, radius))
                continue;
            TakeForErase(stroke.Slot);
        }

        if (!InHeadset && StrokeUnderBrush() is { } pointed && pointed.Finished.Value && pointed.Slot.ActiveSelf.Value)
            TakeForErase(pointed.Slot);
    }

    private void TakeForErase(Slot slot)
    {
        if (_erased.Contains(slot))
            return;
        if (!MayErase(slot))
        {
            if (!_toldNotMine)
            {
                _toldNotMine = true;
                ToolNotice.Say(World, EquipName.Value ?? "Brush", BrushLocale.Mine.Resolve());
            }
            return;
        }
        if (Guarded(() => slot.ActiveSelf.Value = false))
            _erased.Add(slot);
    }

    private void EndErase()
    {
        _erasing = false;
        if (_erased.Count == 0)
            return;
        var taken = new List<Slot>();
        foreach (var slot in _erased)
        {
            if (slot.IsDestroyed)
                continue;
            // Back on before the park, which remembers whether each one was showing.
            Guarded(() => slot.ActiveSelf.Value = true);
            taken.Add(slot);
        }
        _erased.Clear();
        var batch = SlotExistenceUndoBatch.Destroy(World, taken, UndoLocale.EraseStrokes);
        if (batch != null)
        {
            RecordUndo(batch);
            return;
        }
        foreach (var slot in taken)
        {
            if (!slot.IsDestroyed)
                Guarded(() => slot.Destroy());
        }
    }

    // The pointer's ray on a desktop: the laser's, or the aim a test hands every tool.
    private bool TryPointerRay(out float3 origin, out float3 direction)
    {
        if (AimOverride is { } aim)
        {
            origin = aim.Origin;
            direction = aim.Direction;
            return true;
        }
        var laser = ActiveTool?.Laser;
        origin = laser?.RayOrigin ?? float3.Zero;
        direction = laser?.RayDirection ?? float3.Zero;
        return laser != null;
    }

    private bool TryPointerHit(out Slot? slot, out float3 point)
    {
        if (AimOverride is { } aim)
        {
            slot = aim.HitSlot;
            point = aim.Point;
            return slot != null;
        }
        var laser = ActiveTool?.Laser;
        slot = laser?.CurrentHitSlot;
        point = laser?.CurrentHitPoint ?? float3.Zero;
        if (slot == null && laser?.CurrentColliderHitSlot is { } surface)
        {
            slot = surface;
            point = laser.CurrentColliderHitPoint;
        }
        return slot != null;
    }

    private bool TryEraserCentre(out float3 centre)
    {
        if (InHeadset)
        {
            centre = Tip;
            return true;
        }
        return TryPointerHit(out _, out centre);
    }

    // The stroke the beam is on, or in a headset the nearest one the tip is touching.
    private StrokeMesh? StrokeUnderBrush()
    {
        if (!InHeadset)
        {
            TryPointerHit(out var hit, out _);
            var stroke = hit?.GetComponentInParents<StrokeMesh>();
            return stroke is { IsDestroyed: false } ? stroke : null;
        }

        float radius = MathF.Max(EraserSize.Value, 0.03f) * UserScale;
        var tip = Tip;
        StrokeMesh? best = null;
        float bestDistance = float.MaxValue;
        foreach (var stroke in StrokeMesh.In(World))
        {
            if (stroke.IsDestroyed || stroke.Slot == null || !stroke.Slot.ActiveSelf.Value || !stroke.Finished.Value)
                continue;
            if (!stroke.Touches(tip, radius))
                continue;
            float d = stroke.DistanceTo(tip);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = stroke;
            }
        }
        return best;
    }

    // Your own strokes only, and only where the world lets you take things away at all.
    private static bool MayErase(Slot slot) => !slot.IsDestroyed && slot.IsMintedHere && slot.MayDestroy();

    // MENU

    public override void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
        var current = Kind.Value;

        var tips = new ContextMenuPage(BrushLocale.Tip.Resolve());
        foreach (var tip in Enum.GetValues<BrushTip>())
        {
            var pick = tip;
            tips.AddItem(new ContextMenuItem
            {
                Label = BrushLocale.TipName(tip).Resolve(),
                Detail = BrushLocale.TipDetail(tip).Resolve(),
                FillColor = tip == current ? ActiveFill : ItemFill,
                OnPressed = _ => { if (!IsDestroyed) Guarded(() => Kind.Value = pick); },
            });
        }
        page.AddItem(new ContextMenuItem
        {
            Label = BrushLocale.TipName(current).Resolve(),
            Detail = BrushLocale.Tip.Resolve(),
            FillColor = ActiveFill,
            SubPage = tips,
        });

        if (current == BrushTip.Eraser)
        {
            page.AddItem(DialItem(EraserSize, MinEraser, MaxEraser, DefaultEraser, BrushLocale.Size));
            return;
        }

        var colours = new ContextMenuPage(BrushLocale.Colour.Resolve());
        foreach (var swatch in Swatches)
        {
            var pick = swatch;
            colours.AddItem(new ContextMenuItem
            {
                Label = "",
                Detail = Hex(swatch),
                FillColor = Rgba(swatch),
                OnPressed = _ => { if (!IsDestroyed) Guarded(() => Color.Value = pick); },
            });
        }
        colours.AddItem(new ContextMenuItem
        {
            Label = BrushLocale.Picker.Resolve(),
            FillColor = ItemFill,
            OnPressed = _ => OpenPicker(),
        });
        page.AddItem(new ContextMenuItem
        {
            Label = BrushLocale.Colour.Resolve(),
            Detail = Hex(Color.Value),
            FillColor = Rgba(Color.Value),
            SubPage = colours,
        });

        page.AddItem(DialItem(Width, MinWidth, MaxWidth, DefaultWidth, BrushLocale.Width));

        if (current != BrushTip.Fill)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = SmoothLabel(),
                FillColor = ItemFill,
                ReadValue = () => IsDestroyed ? 0f : Smoothing.Value,
                WriteValue = v => { if (!IsDestroyed) Guarded(() => Smoothing.Value = System.Math.Clamp(v, 0f, 1f)); },
                DefaultValue = DefaultSmoothing,
                Refresh = item =>
                {
                    var label = SmoothLabel();
                    if (item.Label == label)
                        return false;
                    item.Label = label;
                    return true;
                },
            });
        }
    }

    // A size turned on a dial runs on a log scale: a millimetre matters at the thin end and not at the thick.
    private ContextMenuItem DialItem(Sync<float> field, float min, float max, float initial, Localization.LocaleText text)
    {
        string Label() => text.WithArgs(Centimetres(field.Value)).Resolve();
        return new ContextMenuItem
        {
            Label = Label(),
            FillColor = ItemFill,
            ReadValue = () => IsDestroyed ? 0f : ToDial(field.Value, min, max),
            WriteValue = v => { if (!IsDestroyed) Guarded(() => field.Value = FromDial(v, min, max)); },
            DefaultValue = ToDial(initial, min, max),
            Refresh = item =>
            {
                var label = Label();
                if (item.Label == label)
                    return false;
                item.Label = label;
                return true;
            },
        };
    }

    public static float ToDial(float value, float min, float max)
        => System.Math.Clamp(MathF.Log(System.Math.Clamp(value, min, max) / min) / MathF.Log(max / min), 0f, 1f);

    public static float FromDial(float dial, float min, float max)
        => min * MathF.Pow(max / min, System.Math.Clamp(dial, 0f, 1f));

    private static string Centimetres(float metres)
        => metres < 0.01f ? $"{metres * 1000f:0} mm" : $"{metres * 100f:0.#} cm";

    private string SmoothLabel() => BrushLocale.Smooth.WithArgs($"{Smoothing.Value * 100f:0}%").Resolve();

    private static float[] Rgba(colorHDR c)
        => new[] { System.Math.Clamp(c.r, 0f, 1f), System.Math.Clamp(c.g, 0f, 1f), System.Math.Clamp(c.b, 0f, 1f), 0.95f };

    private static string Hex(colorHDR c)
    {
        int r = (int)MathF.Round(System.Math.Clamp(c.r, 0f, 1f) * 255f);
        int g = (int)MathF.Round(System.Math.Clamp(c.g, 0f, 1f) * 255f);
        int b = (int)MathF.Round(System.Math.Clamp(c.b, 0f, 1f) * 255f);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void OpenPicker()
    {
        var world = World;
        if (IsDestroyed || world == null)
            return;
        var head = world.LocalUser?.Root?.HeadSlot;
        var position = head != null
            ? head.GlobalPosition + head.GlobalRotation * float3.Backward * (0.6f * UserScale) - float3.Up * (0.12f * UserScale)
            : Slot.GlobalPosition;
        ColorPickerPanel.Spawn(world, Color, "", position);
    }
}
