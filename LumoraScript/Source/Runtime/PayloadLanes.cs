// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Lumora.Script;

// An event's payload as the lanes its node's outputs take, one compiled writer per payload type: the frame's
// update is raised on every script that has one, every frame, and reading each property through reflection
// boxed the payload and every value in it each time. Same lanes, same order, same handles as before; where
// nothing can be compiled the reflected way stays. -xlinka
internal static class PayloadLanes<T> where T : struct
{
    public delegate void Writer(in T payload, ulong[] lanes, ScriptContextBase context);

    public static readonly int Count = CountLanes();

    public static readonly Writer? Write = Build();

    private static int CountLanes()
    {
        int count = 0;
        foreach (var property in NodeRegistry.PayloadProperties(typeof(T)))
            count += ScriptTypes.Get(property.PropertyType).LaneCount;
        return count;
    }

    private static Writer? Build()
    {
        try
        {
            var payload = Expression.Parameter(typeof(T).MakeByRefType(), "payload");
            var lanes = Expression.Parameter(typeof(ulong[]), "lanes");
            var context = Expression.Parameter(typeof(ScriptContextBase), "context");
            var body = new List<Expression>();
            int lane = 0;
            foreach (var property in NodeRegistry.PayloadProperties(typeof(T)))
            {
                var type = ScriptTypes.Get(property.PropertyType);
                NodeInvoker.Encode(type, Expression.Property(payload, property), lanes, lane, context, body);
                lane += type.LaneCount;
            }
            body.Add(Expression.Empty());
            return Expression.Lambda<Writer>(Expression.Block(body), payload, lanes, context).Compile();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
