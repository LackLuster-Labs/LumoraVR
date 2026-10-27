// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Moves by taking hold of the air: close a hand with nothing in it and the world is held at that point, so moving
// the hand moves the person the other way, through walls and up into the sky alike. No gravity while it is in
// use. The other hand can take over the hold at any time; the stick still turns. Needs tracked hands, so it is
// not offered on a desktop.
//
// Visual, when set, is shown at the held point while a hold lasts. -xlinka
[ComponentCategory("Users/Locomotion")]
public class WorldGrabLocomotion : LocomotionModule
{
    public readonly TurnSubmodule Turn = new TurnSubmodule();

    public readonly SyncRef<Slot> Visual = new();

    private readonly HandHold _hold = new();
    private InputInterface? _input;
    private bool _leftWasHeld;
    private bool _rightWasHeld;

    protected override string StockName => "Grab World";

    public override bool CanActivate()
    {
        var input = Engine.Current?.InputInterface;
        return input != null && input.VR_Active
            && (input.LeftController?.IsTracked == true || input.RightController?.IsTracked == true);
    }

    protected override void OnActivated()
    {
        _input = Engine.Current?.InputInterface;
        _leftWasHeld = true;
        _rightWasHeld = true;
        _hold.End();
        Owner?.CharacterController?.SetSimulationEnabled(false);
        Turn.Activate(Owner);
    }

    protected override void OnDeactivated()
    {
        _hold.End();
        ShowVisual(false);
        Turn.Deactivate();
        Owner?.ResumeCharacter();
        _input = null;
    }

    public override void OnModuleUpdate(float delta)
    {
        var root = Owner?.UserRoot;
        if (Owner == null || root == null || root.IsDestroyed)
            return;
        if (InputBlocked || MovementBlocked)
        {
            _hold.End();
            ShowVisual(false);
            if (MovementBlocked && !InputBlocked)
                Turn.Update(LocomotionInputHelper.ReadTurnAxis(_input!), delta);
            return;
        }

        bool left = HandHold.GripHeld(_input, Chirality.Left);
        bool right = HandHold.GripHeld(_input, Chirality.Right);
        bool leftClosed = left && !_leftWasHeld;
        bool rightClosed = right && !_rightWasHeld;
        _leftWasHeld = left;
        _rightWasHeld = right;

        if (_hold.Holding && !(_hold.Side == Chirality.Left ? left : right))
            _hold.End();
        if (rightClosed)
            TakeHold(root, Chirality.Right);
        if (leftClosed)
            TakeHold(root, Chirality.Left);

        if (_hold.Holding)
            _hold.Pull(root, delta);
        ShowVisual(_hold.Holding);

        Turn.Update(LocomotionInputHelper.ReadTurnAxis(_input!), delta);
    }

    private void TakeHold(UserRoot root, Chirality side)
    {
        if (HandHold.HandSlot(root, side) is not { } hand || HandHold.HandBusy(root, side))
            return;
        _hold.Begin(side, hand.GlobalPosition);
    }

    private void ShowVisual(bool shown)
    {
        var visual = Visual.Target;
        if (visual == null || visual.IsDestroyed)
            return;
        if (shown)
            visual.GlobalPosition = _hold.Anchor;
        if (visual.ActiveSelf.Value != shown)
            visual.ActiveSelf.Value = shown;
    }
}
