// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One pour or blob as an undo step. Particles are nobody's to delete, so undo asks the host to take that pour back
// out of the cells by its number, and redo asks for the same litres again from where it began, as a new pour with a
// number of its own that the next undo then takes. Both go on the pourer's own request ring, which is the only thing
// a pourer ever writes. -xlinka
public sealed class FluidPourUndo : IUndoBatch
{
    private readonly World _world;
    private readonly ulong _system;
    private readonly float3 _point;
    private readonly float _litres;
    private int _number;

    public FluidPourUndo(World world, ulong system, float3 point, float litres, int number)
    {
        _world = world;
        _system = system;
        _point = point;
        _litres = litres;
        _number = number;
    }

    // The pour this step stands for now: the first one, or the last redo's.
    public int Number => _number;

    public float Litres => _litres;

    public string Description => FluidLocale.PourFluid.Resolve();

    public LocaleText LocalizedDescription => FluidLocale.PourFluid;

    public bool Undo()
    {
        var person = FluidTool.OwnPerson(_world, attach: false);
        return person != null && FluidTool.Ask(person, new FluidRequest(FluidRequestKind.UndoPour, _system, FluidTool.InSystem(_world, _system, _point), 0f, _litres, _number));
    }

    public bool Redo()
    {
        var person = FluidTool.OwnPerson(_world, attach: false);
        if (person == null || _world.IsDestroyed)
            return false;
        int number = person.PourNumber.Value + 1;
        try
        {
            person.PourNumber.Value = number;
        }
        catch (System.UnauthorizedAccessException)
        {
            return false;
        }
        if (!FluidTool.Ask(person, new FluidRequest(FluidRequestKind.Emit, _system, FluidTool.InSystem(_world, _system, _point), 0f, _litres, number, 0, float3.Zero)))
            return false;
        _number = number;
        return true;
    }

    public void OnEvicted()
    {
    }
}
