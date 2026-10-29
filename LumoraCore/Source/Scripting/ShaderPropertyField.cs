// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Scripting;

// A shader property as a field of its own type. The material keeps each property's value in one float4, and
// this is that float4 read and written as a float, a colour, a switch (ShaderPropertyLanes). The float4 is the
// real member: writes are judged on it, it is what a drive holds and what syncs. Its identity is the property's,
// so a node bound to one keeps it through saves and copies, and it is gone once the property is, or is no longer
// of its type. One per property and type (ShaderPropertyFields.Of), so a tween or a drive knows it again. -xlinka
internal sealed class ShaderPropertyField<T> : IField<T>, IFieldView
{
    private readonly ShaderUniformParam _param;
    private Action<IChangeable>? _changed;
    private Action<IChangeable>? _relay;

    public ShaderPropertyField(ShaderUniformParam param)
    {
        _param = param;
    }

    public ShaderUniformParam Param => _param;

    public Sync<float4> Member => _param.Value;

    SyncElement IFieldView.Member => _param.Value;

    IWorldElement IFieldView.Owner => _param;

    public T Value
    {
        get => ShaderPropertyLanes.Read<T>(_param.Value.Value, ShaderPropertyLanes.IsThreeNumbers(_param));
        set
        {
            var next = ShaderPropertyLanes.Write(_param.Value.Value, value, ShaderPropertyLanes.IsThreeNumbers(_param));
            if (next != _param.Value.Value)
                _param.Value.Value = next;
        }
    }

    public object BoxedValue
    {
        get => Value!;
        set => Value = (T)value;
    }

    public Type ValueType => typeof(T);

    public bool CanWrite => _param.Value.CanWrite;

    public event Action<IChangeable> Changed
    {
        add
        {
            _changed += value;
            if (_relay == null)
            {
                _relay = _ => _changed?.Invoke(this);
                ((IChangeable)_param.Value).Changed += _relay;
            }
        }
        remove
        {
            _changed -= value;
            if (_changed == null && _relay != null)
            {
                ((IChangeable)_param.Value).Changed -= _relay;
                _relay = null;
            }
        }
    }

    public World World => _param.World;

    public RefID ReferenceID => _param.ReferenceID;

    public bool IsInitialized => _param.IsInitialized;

    public bool IsDestroyed => _param.IsDestroyed || _param.Value.IsDestroyed || !ShaderPropertyLanes.Fits(typeof(T), _param);

    public bool IsPersistent => _param.IsPersistent;

    public bool IsLocalElement => _param.IsLocalElement;

    public string ParentHierarchyToString() => _param.ParentHierarchyToString();

    public bool IsInInitPhase => _param.Value.IsInInitPhase;

    public void EndInitPhase()
    {
    }

    public bool IsLinked => _param.Value.IsLinked;

    public bool IsDriven => _param.Value.IsDriven;

    public bool IsHooked => _param.Value.IsHooked;

    public ILinkRef? ActiveLink => _param.Value.ActiveLink;

    public ILinkRef? DirectLink => _param.Value.DirectLink;

    public ILinkRef? InheritedLink => _param.Value.InheritedLink;

    public IEnumerable<ILinkable>? LinkableChildren => null;

    // A drive or a hook goes on the property's own float4 (ScriptShaderPropertyDrive), never on this.
    public void Link(ILinkRef link) => throw new InvalidOperationException("a shader property is linked through its value");

    public void InheritLink(ILinkRef link) => throw new InvalidOperationException("a shader property is linked through its value");

    public void ReleaseLink(ILinkRef link)
    {
    }

    public void ReleaseInheritedLink(ILinkRef link)
    {
    }

    public override string ToString() => _param.Name.Value ?? "property";
}
