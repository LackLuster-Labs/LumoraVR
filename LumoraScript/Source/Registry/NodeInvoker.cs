// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Lumora.Wasm;

namespace Lumora.Script;

// The bridge between a host call and a node's Run. Arguments arrive as raw lanes; handles are turned
// back into objects through the calling instance's context, Run is called, and the results go back out
// as lanes. The body is one compiled expression per overload, so a call costs about a delegate call.
// Without runtime code generation (a fully AOT target) the same work is done through reflection:
// slower, identical result. -xlinka
public sealed class NodeInvoker
{
    private static readonly MethodInfo ToBoolMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.ToBool))!;
    private static readonly MethodInfo ToIntMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.ToInt))!;
    private static readonly MethodInfo ToLongMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.ToLong))!;
    private static readonly MethodInfo ToFloatMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.ToFloat))!;
    private static readonly MethodInfo ToDoubleMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.ToDouble))!;
    private static readonly MethodInfo FromBoolMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.FromBool))!;
    private static readonly MethodInfo FromIntMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.FromInt))!;
    private static readonly MethodInfo FromLongMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.FromLong))!;
    private static readonly MethodInfo FromFloatMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.FromFloat))!;
    private static readonly MethodInfo FromDoubleMethod = typeof(LaneCodec).GetMethod(nameof(LaneCodec.FromDouble))!;
    private static readonly MethodInfo ResolveMethod = typeof(NodeInvoker).GetMethod(nameof(ResolveHandle), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo AddHandleMethod = typeof(ScriptContextBase).GetMethod(nameof(ScriptContextBase.AddHandle))!;
    private static readonly MethodInfo OpenTicketMethod = typeof(NodeInvoker).GetMethod(nameof(OpenTicket), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly NodeOverload _overload;
    private readonly MethodInfo _method;
    private readonly Action<ulong[], ulong[], ScriptContextBase>? _compiled;
    private readonly int _argumentLanes;
    private readonly int _resultLanes;
    private readonly Evaluation _evaluation;
    // The lane of the first handle input: the field a Field read reads.
    private readonly int _watchLane = -1;
    private readonly bool _isAction;
    // An action's handle inputs that name a thing of their own, by lane and port: one bound to something
    // since gone fails the call by name instead of reaching the node as nothing, where an empty optional
    // input means the script's own object, the world root or world space. A port of the node's own type
    // argument carries data, a list item or a reference's target, and is left as it is.
    private readonly int[]? _thingLanes;
    private readonly string[]? _thingPorts;

    public bool IsCompiled => _compiled != null;

    internal NodeInvoker(NodeOverload overload)
    {
        _overload = overload;
        _method = overload.Method ?? throw new ArgumentException($"{overload} has no method to call");
        _argumentLanes = overload.ImportType!.Parameters.Length;
        _resultLanes = overload.ImportType!.Results.Length;
        _evaluation = overload.Definition.Evaluation;
        int lane = Awaits ? 1 : 0;
        foreach (var input in overload.Inputs)
        {
            if (input.Type.IsHandle)
            {
                _watchLane = lane;
                break;
            }
            lane += input.Type.LaneCount;
        }

        _isAction = overload.Definition.Kind == NodeKind.Action;
        if (_isAction)
            FindThings(out _thingLanes, out _thingPorts);

        try
        {
            _compiled = Compile();
        }
        catch (Exception)
        {
            _compiled = null;
        }
    }

    // One callback per script instance, owning its scratch arrays, so a host call never allocates.
    public WasmHostCallback Bind(ScriptContextBase context)
    {
        var arguments = new ulong[_argumentLanes];
        var results = new ulong[_resultLanes];
        return call => Invoke(call, context, arguments, results);
    }

    private void Invoke(WasmHostCall call, ScriptContextBase context, ulong[] arguments, ulong[] results)
    {
        for (int i = 0; i < arguments.Length; i++)
            arguments[i] = call.GetRaw(i);
        Array.Clear(results);
        bool watched = context.TakeFailWatch();

        if (_thingLanes != null && GoneInput(context, arguments) is { } gone)
        {
            context.Fail(gone);
            for (int i = 0; i < results.Length; i++)
                call.SetRaw(i, 0);
            return;
        }

        try
        {
            context.TakeTurnedDown();
            using (context.EnterCall(_overload))
            {
                if (_compiled != null)
                    _compiled(arguments, results, context);
                else
                    InvokeReflected(arguments, results, context);
            }
            if (context.TakeTurnedDown() is { } reason)
            {
                Array.Clear(results);
                context.Fail(reason);
                context.OnRefused(_overload, new UnauthorizedAccessException(reason));
                if (context.Recording)
                    context.NoteUnsettled();
            }
            else if (context.Recording)
            {
                context.Note(_evaluation, _watchLane >= 0 ? context.GetLive(LaneCodec.ToInt(arguments[_watchLane])) : null);
            }
        }
        catch (UnauthorizedAccessException refusal)
        {
            Array.Clear(results);
            context.Fail(refusal.Message);
            context.OnRefused(_overload, refusal);
            if (context.Recording)
                context.NoteUnsettled();
        }
        // An input the action cannot use, or a thing it could not do, with failed wired: it leaves by failed
        // saying why. Without failed wired it stops the call as a fault, as it always has. -xlinka
        catch (Exception bad) when (watched && _isAction && bad is ArgumentException or InvalidOperationException)
        {
            Array.Clear(results);
            context.Fail(bad.Message);
        }

        for (int i = 0; i < results.Length; i++)
            call.SetRaw(i, results[i]);
    }

    private string? GoneInput(ScriptContextBase context, ulong[] arguments)
    {
        for (int i = 0; i < _thingLanes!.Length; i++)
        {
            int handle = LaneCodec.ToInt(arguments[_thingLanes[i]]);
            if (handle != 0 && context.GetHandle(handle) is { } thing && context.IsGone(thing))
                return context.GoneWords(_thingPorts![i], thing);
        }
        return null;
    }

    private void FindThings(out int[]? lanes, out string[]? ports)
    {
        lanes = null;
        ports = null;
        var open = _overload.Definition.ClrType.IsGenericTypeDefinition
            ? Array.Find(_overload.Definition.ClrType.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.MetadataToken == _method.MetadataToken)
            : _method;
        if (open == null)
            return;
        var parameters = open.GetParameters();
        var foundLanes = new List<int>();
        var foundPorts = new List<string>();
        int input = 0;
        int lane = Awaits ? 1 : 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if ((i == 0 && _overload.TakesContext) || (Awaits && i == TicketParameter) || parameters[i].IsOut)
                continue;
            var port = _overload.Inputs[input++];
            if (port.Type.IsHandle && port.Type != ScriptTypes.String && !parameters[i].ParameterType.IsGenericParameter)
            {
                foundLanes.Add(lane);
                foundPorts.Add(port.Name);
            }
            lane += port.Type.LaneCount;
        }
        if (foundLanes.Count > 0)
        {
            lanes = foundLanes.ToArray();
            ports = foundPorts.ToArray();
        }
    }

    private static T? ResolveHandle<T>(ScriptContextBase context, int handle) where T : class
        => context.GetLive(handle) as T;

    // An await's ticket, from the await number the module passes ahead of the inputs, in the call of a waiting
    // Function the module named just before, if any.
    private static AwaitTicket OpenTicket(ScriptContextBase context, int index)
        => (context.Awaits ?? throw new ScriptLimitException("this script cannot wait on anything")).Open(index, context.TakeWord());

    private bool Awaits => _overload.Definition.Kind == NodeKind.Await;

    private int TicketParameter => _overload.TakesContext ? 1 : 0;

    private Action<ulong[], ulong[], ScriptContextBase> Compile()
    {
        var arguments = Expression.Parameter(typeof(ulong[]), "arguments");
        var results = Expression.Parameter(typeof(ulong[]), "results");
        var context = Expression.Parameter(typeof(ScriptContextBase), "context");

        var parameters = _method.GetParameters();
        var callArguments = new Expression[parameters.Length];
        var outs = new List<(ParameterExpression Variable, ScriptType Type)>();
        var body = new List<Expression>();
        var locals = new List<ParameterExpression>();

        int input = 0;
        int lane = Awaits ? 1 : 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (i == 0 && _overload.TakesContext)
            {
                callArguments[i] = Expression.Convert(context, parameter.ParameterType);
            }
            else if (Awaits && i == TicketParameter)
            {
                var index = Expression.Call(ToIntMethod, Expression.ArrayIndex(arguments, Expression.Constant(0)));
                callArguments[i] = Expression.Convert(Expression.Call(OpenTicketMethod, context, index), parameter.ParameterType);
            }
            else if (parameter.IsOut)
            {
                var variable = Expression.Variable(parameter.ParameterType.GetElementType()!, parameter.Name);
                locals.Add(variable);
                callArguments[i] = variable;
                outs.Add((variable, ScriptTypes.Get(variable.Type)));
            }
            else
            {
                var type = _overload.Inputs[input++].Type;
                callArguments[i] = Decode(type, arguments, lane, context);
                lane += type.LaneCount;
            }
        }

        var call = Expression.Call(_method, callArguments);
        int resultLane = 0;
        if (_method.ReturnType == typeof(void))
        {
            body.Add(call);
        }
        else
        {
            var returned = Expression.Variable(_method.ReturnType, "returned");
            locals.Add(returned);
            body.Add(Expression.Assign(returned, call));
            var type = ScriptTypes.Get(_method.ReturnType);
            Encode(type, returned, results, resultLane, context, body);
            resultLane += type.LaneCount;
        }

        foreach (var (variable, type) in outs)
        {
            Encode(type, variable, results, resultLane, context, body);
            resultLane += type.LaneCount;
        }

        body.Add(Expression.Empty());
        var block = Expression.Block(locals, body);
        return Expression.Lambda<Action<ulong[], ulong[], ScriptContextBase>>(block, arguments, results, context).Compile();
    }

    private static Expression Decode(ScriptType type, ParameterExpression arguments, int lane, ParameterExpression context)
        => Decode(type, index => Expression.ArrayIndex(arguments, Expression.Constant(index)), arguments, lane, context);

    private static Expression Decode(ScriptType type, Func<int, Expression> laneAt, ParameterExpression? arguments, int lane,
        ParameterExpression? context)
    {
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool: return Expression.Call(ToBoolMethod, laneAt(lane));
            case ScriptTypeKind.Int: return Expression.Call(ToIntMethod, laneAt(lane));
            case ScriptTypeKind.Long: return Expression.Call(ToLongMethod, laneAt(lane));
            case ScriptTypeKind.Float: return Expression.Call(ToFloatMethod, laneAt(lane));
            case ScriptTypeKind.Double: return Expression.Call(ToDoubleMethod, laneAt(lane));
            case ScriptTypeKind.Vector:
            {
                // A local filled leaf by leaf; a nested struct's numbers are written through its field.
                var value = Expression.Variable(type.ClrType, "value");
                var steps = new List<Expression> { Expression.Assign(value, Expression.Default(type.ClrType)) };
                for (int i = 0; i < type.Leaves.Length; i++)
                {
                    var leaf = type.Leaves[i];
                    steps.Add(Expression.Assign(leaf.Read(value), Expression.Call(leaf.FromRawMethod, laneAt(lane + i))));
                }
                steps.Add(value);
                return Expression.Block(type.ClrType, new[] { value }, steps);
            }
            case ScriptTypeKind.Coded:
                return type.Codec!.Decode(arguments ?? throw new InvalidOperationException("a coded value needs its lanes"), lane);
            default:
                return Expression.Call(ResolveMethod.MakeGenericMethod(type.ClrType),
                    context ?? throw new InvalidOperationException("a handle needs its context"), Expression.Call(ToIntMethod, laneAt(lane)));
        }
    }

    // A pure overload over plain numbers, as a call native code can make straight from its lanes: one ulong per
    // input lane in, the one output lane back. No context, no handle table, no host call around it, and the same
    // conversions the adapter makes, so the same bits come out. Null for any overload that is not that.
    internal static Delegate? DirectLanes(NodeOverload overload)
    {
        if (overload.Definition.Kind != NodeKind.Value || overload.TakesContext || overload.Method is not { } method
            || overload.Outputs.Count != 1 || method.ReturnType == typeof(void))
            return null;
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.IsOut || parameter.ParameterType.IsByRef)
                return null;
        }
        var output = overload.Outputs[0].Type;
        if (output.Kind is not (ScriptTypeKind.Bool or ScriptTypeKind.Int or ScriptTypeKind.Long or ScriptTypeKind.Float or ScriptTypeKind.Double))
            return null;
        int lanes = 0;
        foreach (var input in overload.Inputs)
        {
            if (input.Type.Kind is not (ScriptTypeKind.Bool or ScriptTypeKind.Int or ScriptTypeKind.Long or ScriptTypeKind.Float
                or ScriptTypeKind.Double or ScriptTypeKind.Vector))
                return null;
            lanes += input.Type.LaneCount;
        }
        if (lanes > 16)
            return null;
        try
        {
            var parameters = new ParameterExpression[lanes];
            for (int i = 0; i < lanes; i++)
                parameters[i] = Expression.Parameter(typeof(ulong), "a" + i);
            var arguments = new Expression[overload.Inputs.Count];
            int at = 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                var type = overload.Inputs[i].Type;
                arguments[i] = Decode(type, index => parameters[index], null, at, null);
                at += type.LaneCount;
            }
            var call = Expression.Call(method, arguments);
            var encode = output.Kind switch
            {
                ScriptTypeKind.Bool => FromBoolMethod,
                ScriptTypeKind.Int => FromIntMethod,
                ScriptTypeKind.Long => FromLongMethod,
                ScriptTypeKind.Float => FromFloatMethod,
                _ => FromDoubleMethod,
            };
            // A throw becomes the trap a host function's throw becomes, so native code needs no handler of its own.
            var trap = Expression.Parameter(typeof(WasmException), "trap");
            var thrown = Expression.Parameter(typeof(Exception), "thrown");
            var named = Expression.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!,
                Expression.Property(Expression.Call(thrown, typeof(object).GetMethod(nameof(GetType))!), nameof(Type.Name)),
                Expression.Constant(": "), Expression.Property(thrown, nameof(Exception.Message)));
            var hostTrap = Expression.New(typeof(WasmTrapException).GetConstructor(new[] { typeof(WasmTrapCode), typeof(string) })!,
                Expression.Constant(WasmTrapCode.HostTrap), named);
            var body = Expression.TryCatch(Expression.Call(encode, call),
                Expression.Catch(trap, Expression.Rethrow(typeof(ulong))),
                Expression.Catch(thrown, Expression.Throw(hostTrap, typeof(ulong))));
            var types = new Type[lanes + 1];
            Array.Fill(types, typeof(ulong));
            return Expression.Lambda(Expression.GetFuncType(types), body, parameters).Compile();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void Encode(ScriptType type, Expression value, ParameterExpression results, int lane,
        ParameterExpression context, List<Expression> body)
    {
        Expression Slot(int index) => Expression.ArrayAccess(results, Expression.Constant(index));

        switch (type.Kind)
        {
            case ScriptTypeKind.Bool: body.Add(Expression.Assign(Slot(lane), Expression.Call(FromBoolMethod, value))); break;
            case ScriptTypeKind.Int: body.Add(Expression.Assign(Slot(lane), Expression.Call(FromIntMethod, value))); break;
            case ScriptTypeKind.Long: body.Add(Expression.Assign(Slot(lane), Expression.Call(FromLongMethod, value))); break;
            case ScriptTypeKind.Float: body.Add(Expression.Assign(Slot(lane), Expression.Call(FromFloatMethod, value))); break;
            case ScriptTypeKind.Double: body.Add(Expression.Assign(Slot(lane), Expression.Call(FromDoubleMethod, value))); break;
            case ScriptTypeKind.Vector:
                for (int i = 0; i < type.Leaves.Length; i++)
                    body.Add(Expression.Assign(Slot(lane + i), Expression.Call(type.Leaves[i].ToRawMethod, type.Leaves[i].Read(value))));
                break;
            case ScriptTypeKind.Coded:
                type.Codec!.Encode(value, results, lane, body);
                break;
            default:
                body.Add(Expression.Assign(Slot(lane), Expression.Call(FromIntMethod,
                    Expression.Call(context, AddHandleMethod, Expression.Convert(value, typeof(object))))));
                break;
        }
    }

    private void InvokeReflected(ulong[] arguments, ulong[] results, ScriptContextBase context)
    {
        var parameters = _method.GetParameters();
        var values = new object?[parameters.Length];
        int input = 0;
        int lane = Awaits ? 1 : 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i == 0 && _overload.TakesContext)
            {
                values[i] = context;
            }
            else if (Awaits && i == TicketParameter)
            {
                values[i] = OpenTicket(context, LaneCodec.ToInt(arguments[0]));
            }
            else if (!parameters[i].IsOut)
            {
                var type = _overload.Inputs[input++].Type;
                values[i] = LaneCodec.Read(type, arguments.AsSpan(lane, type.LaneCount), context);
                lane += type.LaneCount;
            }
        }

        object? returned;
        try
        {
            returned = _method.Invoke(null, values);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            throw;
        }

        int resultLane = 0;
        if (_method.ReturnType != typeof(void))
        {
            var type = ScriptTypes.Get(_method.ReturnType);
            LaneCodec.Write(type, returned, results.AsSpan(resultLane, type.LaneCount), context);
            resultLane += type.LaneCount;
        }
        for (int i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].IsOut)
                continue;
            var type = ScriptTypes.Get(parameters[i].ParameterType.GetElementType()!);
            LaneCodec.Write(type, values[i], results.AsSpan(resultLane, type.LaneCount), context);
            resultLane += type.LaneCount;
        }
    }
}
