// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Scripting;

namespace Lumora.Core.Components.Scripting;

// The link a running script holds a shader property with, the way ScriptFieldDrive holds a value field. The
// property's value is one float4 whatever its type, so that float4 is what the link holds, granted by the host as
// the script's author like any field, and the script's value goes into the numbers its type covers. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptShaderPropertyDrive<T> : Component, IScriptDrive
{
    public readonly SyncRef<ScriptHost> Host = new();
    public readonly Sync<string> Node = new();

    // Host-only: whether the host granted the link, for the reason ScriptFieldDrive gives.
    public readonly Sync<bool> Granted = new();
    public readonly HoldingFieldDrive<float4> Drive = new();
    public readonly SyncRef<IField<T>> Heard = new();
    public readonly SyncRef<IField<int>> HeardCount = new();

    public override void OnAwake()
    {
        base.OnAwake();
        Granted.MarkHostOnly();
        Drive.LocalValueOnly = true;
        Drive.Holds = () => Heard.Target != null;
        Drive.SetupValueSetHook(Refused);
    }

    private ShaderUniformParam? Param => Drive.Target is SyncElement { Parent: ShaderUniformParam param } ? param : null;

    private void Refused(SyncField<float4> field, float4 value)
    {
        if (Heard.Target is not { } heard || Param is not { } param)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            heard.Value = ShaderPropertyLanes.Read<T>(value, ShaderPropertyLanes.IsThreeNumbers(param));
            if (HeardCount.Target is { } count)
                count.Value = count.Value + 1;
        }
    }

    void IScriptDrive.Report(IWorldElement? heard, IWorldElement? heardCount)
    {
        // A heard field that is itself a property view has no identity of its own to keep a reference to.
        var field = heard is IFieldView ? null : heard as IField<T>;
        if (!ReferenceEquals(Heard.Target, field))
            Heard.Target = field!;
        var count = heardCount is IFieldView ? null : heardCount as IField<int>;
        if (!ReferenceEquals(HeardCount.Target, count))
            HeardCount.Target = count!;
    }

    ScriptHost? IScriptDrive.Owner
    {
        get => Host.Target;
        set => Host.Target = value!;
    }

    string IScriptDrive.NodeId
    {
        get => Node.Value ?? string.Empty;
        set => Node.Value = value;
    }

    bool IScriptDrive.IsGone => IsDestroyed;

    void IScriptDrive.Aim(IWorldElement? target)
    {
        var field = (target as ShaderPropertyField<T>)?.Member;
        if (!ReferenceEquals(Drive.Target, field))
            Drive.DriveTarget(field);
    }

    void IScriptDrive.Confirm()
    {
        if (World is not { IsAuthority: true } world || Granted.Value == Drive.IsLinkValid)
            return;
        using (world.DataModelPermissions?.EnterSystemBypass())
            Granted.Value = Drive.IsLinkValid;
    }

    bool IScriptDrive.Show(object? value)
    {
        if (!Drive.IsLinkValid || World is not { IsAuthority: true } && !Granted.Value || Param is not { } param
            || Drive.Target is not { } held)
            return false;
        Drive.SetValue(ShaderPropertyLanes.Write(held.Value, value is T typed ? typed : default!, ShaderPropertyLanes.IsThreeNumbers(param)));
        return true;
    }
}
