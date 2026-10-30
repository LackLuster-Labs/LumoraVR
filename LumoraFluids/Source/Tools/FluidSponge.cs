// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A grabbable sponge. Held, it soaks up fluid it touches, up to Capacity; the trigger squeezes it out; let go, it
// drips what it holds out over two seconds, so a sponge set down is dry.
//
// It never holds state its holder writes. Whoever holds a sponge they do not own may not edit it, and a Social world
// refuses even that much to everyone, so what it has soaked rides on the holder's own Held entry for that hand,
// tagged with the sponge, and soaking and squeezing go out as Pickup and Squeeze entries on the holder's release
// ring. Its two settings belong to whoever owns it. All of it runs on the holder's machine; the rest only read. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class FluidSponge : Component, IHeldActivatable
{
    // In FluidPerson.Held z: the entry is a sponge's, and w holds the sponge's RefID, low half.
    public const int HeldSpongeFlag = 1 << 12;
    public const float DripSeconds = 2f;

    private const double PickupSeconds = 0.25;
    private const double SqueezeSeconds = 0.1;
    private const int DripSteps = 8;
    // L/s at a full pull
    private const float SqueezeRate = 0.6f;
    // L per desktop press
    private const float DesktopSqueeze = 0.1f;
    // m from the sponge's middle that counts as touching
    private const float Reach = 0.06f;

    // L
    public readonly Sync<float> Capacity = new(0.3f);
    // L/s
    public readonly Sync<float> AbsorbRate = new(0.5f);

    // The holder's machine only.
    private FluidPerson? _person;
    private int _hand = -1;
    private bool _held;
    private float _soaked;
    private float _pickup;
    private float _squeeze;
    private double _lastPickup;
    private double _lastSqueeze;
    private double _lastLedger;
    private float _dripEach;
    private int _dripsLeft;
    private double _nextDrip;
    private int _pickups;
    private int _squeezes;
    private int _drips;

    public float Soaked => _soaked;

    public bool HeldHere => _held;

    public int PickupEntries => _pickups;

    public int SqueezeEntries => _squeezes;

    public int DripEntries => _drips;

    private static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    public override void OnAttach()
    {
        base.OnAttach();
        if (Slot.GetComponent<Grabbable>() == null)
            Slot.AttachComponent<Grabbable>();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        var world = World;
        if (world == null || Slot == null)
            return;
        var grabber = Slot.GetComponent<Grabbable>()?.Grabber;
        var local = world.LocalUser;
        bool heldHere = grabber is { IsDestroyed: false } && local != null && ReferenceEquals(grabber.OwningUser, local);
        if (heldHere)
        {
            if (!_held)
                PickUp(world, grabber!);
            Hold(world, delta);
        }
        else if (_held)
        {
            LetGo(world);
        }
        Drip(world);
    }

    public override void OnDestroy()
    {
        if (_held && World is { } world)
            LetGo(world);
        base.OnDestroy();
    }

    // Fluid this sponge took in, from whatever saw it touch: the ledger near it, a GPU contact, a wipe. Only while it
    // is in a hand on this machine; the answer is what it had room for.
    public float Soak(float litres)
    {
        if (!_held || !(litres > 0f))
            return 0f;
        float room = MathF.Max(0f, Capacity.Value - _soaked);
        float take = MathF.Min(litres, room);
        _soaked += take;
        _pickup += take;
        return take;
    }

    // A desktop press while it is held squeezes a little out. In a headset the trigger's pull does it, so the press
    // is only kept from turning into the hand's own gesture.
    public bool OnHeldActivate(Grabber grabber)
    {
        if (!_held)
            return false;
        if (!InHeadset)
            SqueezeOut(DesktopSqueeze);
        return true;
    }

    private void PickUp(World world, Grabber grabber)
    {
        _held = true;
        _person = FluidTool.OwnPerson(world, attach: true);
        _hand = HandOf(world, grabber);
        _soaked = 0f;
        _pickup = 0f;
        _squeeze = 0f;
        _dripsLeft = 0;
        _lastPickup = world.Time.TotalTime;
        _lastSqueeze = world.Time.TotalTime;
        _lastLedger = world.Time.TotalTime;
        WriteHeld();
    }

    private void Hold(World world, float delta)
    {
        double now = world.Time.TotalTime;
        // The ledger near it, four times a second, for a machine with no simulation of its own to tell it about
        // contact.
        if (!FluidGpu.CanSimulate && _soaked < Capacity.Value && now - _lastLedger >= PickupSeconds)
        {
            float elapsed = (float)System.Math.Min(now - _lastLedger, 1.0);
            _lastLedger = now;
            float near = LedgerNear(world, Slot.GlobalPosition, Reach);
            if (near > 0f)
                Soak(MathF.Min(near, AbsorbRate.Value * elapsed));
        }

        if (InHeadset && _hand >= 0)
        {
            var input = Engine.Current?.InputInterface;
            var controller = _hand == 0 ? input?.LeftController : input?.RightController;
            float pull = System.Math.Clamp(((controller?.TriggerValue ?? 0f) - FluidTool.StartPull) / (1f - FluidTool.StartPull), 0f, 1f);
            if (pull > 0f)
                SqueezeOut(SqueezeRate * pull * delta);
        }

        if (_pickup > 0f && now - _lastPickup >= PickupSeconds)
        {
            _lastPickup = now;
            if (Release(FluidReleaseKind.Pickup, _pickup))
                _pickups++;
            _pickup = 0f;
            WriteHeld();
        }
        if (_squeeze > 0f && now - _lastSqueeze >= SqueezeSeconds)
        {
            _lastSqueeze = now;
            if (Release(FluidReleaseKind.Squeeze, _squeeze))
                _squeezes++;
            _squeeze = 0f;
            WriteHeld();
        }
    }

    // Squeezed out is clamped to what it holds; nobody makes fluid out of a dry sponge.
    private void SqueezeOut(float litres)
    {
        float amount = MathF.Min(MathF.Max(litres, 0f), _soaked);
        if (amount <= 0f)
            return;
        _soaked -= amount;
        _squeeze += amount;
    }

    private void LetGo(World world)
    {
        if (_pickup > 0f && Release(FluidReleaseKind.Pickup, _pickup))
            _pickups++;
        if (_squeeze > 0f && Release(FluidReleaseKind.Squeeze, _squeeze))
            _squeezes++;
        _pickup = 0f;
        _squeeze = 0f;
        _held = false;
        if (_soaked > 0f)
        {
            _dripsLeft = DripSteps;
            _dripEach = _soaked / DripSteps;
            _nextDrip = world.Time.TotalTime;
        }
        WriteHeld();
    }

    // What a sponge set down still holds goes in eight drips over two seconds, from the ring of whoever let it go,
    // and their Held entry for that hand runs down with it.
    private void Drip(World world)
    {
        if (_dripsLeft <= 0 || world.Time.TotalTime < _nextDrip)
            return;
        _nextDrip += DripSeconds / DripSteps;
        float each = _dripsLeft == 1 ? _soaked : MathF.Min(_dripEach, _soaked);
        _dripsLeft--;
        if (each > 0f)
        {
            _soaked = MathF.Max(0f, _soaked - each);
            if (Release(FluidReleaseKind.Drip, each))
                _drips++;
        }
        if (_dripsLeft == 0)
            _soaked = 0f;
        WriteHeld();
    }

    private bool Release(FluidReleaseKind kind, float litres)
    {
        var person = _person;
        if (person == null || person.IsDestroyed || Slot == null)
            return false;
        var part = _hand == 0 ? BodyNode.LeftHand : BodyNode.RightHand;
        new FluidRelease(kind, Slot.GlobalPosition, float3.Zero, litres, part, 0, HashCode.Combine(ReferenceID.RawValue, _pickups, _squeezes, _drips))
            .Write(out var a, out var b);
        try
        {
            return FluidRings.Write(person.Releases, person.ReleaseSequence, a, b);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // x: what it holds in microlitres; z: the sponge flag; w: the sponge's RefID, low half.
    private void WriteHeld()
    {
        var person = _person;
        if (person == null || person.IsDestroyed || _hand < 0 || person.Held.Count < FluidPerson.HeldCount)
            return;
        int microlitres = (int)MathF.Round(System.Math.Clamp(_soaked, 0f, 2000f) * 1e6f);
        var entry = microlitres > 0 || _held
            ? new int4(microlitres, 0, HeldSpongeFlag, unchecked((int)(uint)ReferenceID.RawValue))
            : new int4(0, 0, 0, 0);
        if (person.Held[_hand].Equals(entry))
            return;
        try
        {
            person.Held[_hand] = entry;
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int HandOf(World world, Grabber grabber)
    {
        var root = world.LocalUser?.Root?.Slot;
        if (root == null)
            return 1;
        foreach (var hand in root.GetComponentsInChildren<HandTool>())
        {
            if (ReferenceEquals(hand.Grabber, grabber))
                return hand.Side.Value == Chirality.Left ? 0 : 1;
        }
        return 1;
    }

    private static float LedgerNear(World world, float3 point, float radius)
    {
        float litres = 0f;
        var root = world.RootSlot;
        if (root == null)
            return 0f;
        foreach (var system in root.GetComponentsInChildren<FluidSystem>())
        {
            if (!system.IsDestroyed)
                litres += system.LitresIn(point, radius);
        }
        return litres;
    }
}
