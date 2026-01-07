using Aquamarine.Kinetix.Core;
using Aquamarine.Kinetix.Solvers;
using Godot;
using System.Collections.Generic;

namespace Aquamarine.Kinetix.Components;

[GlobalClass, Tool]
public partial class KinetixDigitigradeIK : Node3D
{
    [Export] public Skeleton3D Skeleton { get; set; }
    [Export] public Node3D Target { get; set; }
    [Export] public Node3D PoleTarget { get; set; }
    [Export] public string TipBoneName { get; set; } = "";
    [Export(PropertyHint.Range, "0,1,0.1")]
    public float FootBendRatio { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,360,1,radians_as_degree")]
    public float PoleTwist { get; set; } = 0f;
    [Export] public bool AutoSolve { get; set; } = true;
    [Export] public int ProcessPriority { get; set; } = 1;
    [Export] public float Tolerance { get; set; } = 0.001f;
    [Export] public int MaxIterations { get; set; } = 10;

    private IKChain _chain;
    private DigitigradeIKSolver _solver;
    private bool _isValid = false;

    public override void _Ready()
    {
        base._Ready();
        CallDeferred(MethodName.Initialize);
    }

    private void Initialize()
    {
        if (Skeleton == null || Target == null || string.IsNullOrEmpty(TipBoneName))
        {
            if (!Engine.IsEditorHint())
                GD.PushWarning("KinetixDigitigradeIK: Missing props");
            return;
        }

        _chain = IKChain.FromTipBone(Skeleton, TipBoneName, 4);
        if (_chain == null || _chain.Bones.Length != 4)
        {
            GD.PushError($"KinetixDigitigradeIK: Failed to create chain from '{TipBoneName}'");
            return;
        }

        _solver = new DigitigradeIKSolver
        {
            Chain = _chain,
            Target = new IKTarget(),
            FootBendRatio = FootBendRatio,
            Tolerance = Tolerance,
            MaxIterations = MaxIterations
        };

        _isValid = true;
        GD.Print($"Kinetix: Init digitigrade {GetChainDescription()}");
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (!_isValid || !AutoSolve) return;
        Solve();
    }

    public bool Solve()
    {
        if (!_isValid) return false;

        _solver.Target.Position = Target.GlobalPosition;
        _solver.Target.Rotation = Target.GlobalBasis.GetRotationQuaternion();
        _solver.Target.PolePosition = PoleTarget?.GlobalPosition;
        _solver.Target.PoleTwist = PoleTwist;
        _solver.FootBendRatio = FootBendRatio;

        return _solver.Solve();
    }

    public IKChain GetChain() => _chain;

    private string GetChainDescription()
    {
        if (_chain == null || !_chain.IsValid())
            return "Invalid";

        var bones = new List<string>();
        for (int i = 0; i < _chain.Bones.Length; i++)
            bones.Add(_chain.GetBoneName(i));

        return string.Join(" → ", bones);
    }

    public bool IsInitialized() => _isValid;
}
