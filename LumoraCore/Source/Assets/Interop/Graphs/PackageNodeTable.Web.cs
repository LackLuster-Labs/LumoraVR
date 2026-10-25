// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Websockets. A package keeps a connection in a client component its graphs listen to; here the graph opens its
// own when it starts, from the machine it runs on and asked for like any web access, and opens it again a while
// after it fails or closes, as the client did: one connection a graph for each client it listens to. -xlinka
internal static partial class PackageNodeTable
{
    private static readonly ConditionalWeakTable<PackageGraphWriter, HashSet<string>> Connections = new();

    private static void Web()
    {
        On("WebsocketTextMessageReceiver", s =>
        {
            string? client = ClientOf(s);
            if (client == null || s.Index.UnattachedPart(client) is not { Type: "WebsocketClient" } part)
            {
                s.Fail("listens to a websocket client that did not come across");
                return;
            }
            if (WebAddress(part.Data) is not { } url)
            {
                s.Fail("listens to a websocket client with no ws or wss address");
                return;
            }
            string heard = s.Add("OnWebSocketMessage");
            s.Out("Data", heard, "text");
            s.Exit("OnReceived", heard, "next");
            Connect(s, client, url, RetrySeconds(part.Data));
        });

        // A request asked for like any web access, from the machine the script runs on. What runs on as it is
        // sent runs at once here; the answer, a failure and a refusal each go on their own way.
        Same(s =>
        {
            bool post = s.Node.Class == "POST_String";
            string request = s.Add("HttpRequest");
            string address = s.Add("UrlToText");
            s.In("URL", address, "url");
            s.Wire(address, "text", request, "url");
            s.Set(request, "method", post ? "POST" : "GET");
            if (post)
            {
                s.In("String", request, "body", string.Empty);
                if (!s.Wired("MediaType"))
                    s.Set(request, "headers", "Content-Type: application/json");
                else if (s.Index.ConstantText(s.Node.Ref("MediaType")) is { Length: > 0 } media)
                    s.Set(request, "headers", "Content-Type: " + media);
                else
                {
                    string header = s.Add("Concat", "string,string");
                    s.Set(header, "a", "Content-Type: ");
                    s.In("MediaType", header, "b");
                    s.Wire(header, "result", request, "headers");
                }
            }
            s.Op("", request);
            s.Exit("OnSent", request, "next");
            s.Exit("OnResponse", request, "response");
            s.Exit("OnError", request, "failed");
            s.Exit("OnDenied", request, "denied");
            s.Out("Content", request, "content");
            s.Out("StatusCode", request, "status");
        }, "GET_String", "POST_String");

        // Decisions here are per host whatever the port, and cover every kind of access.
        On("IsHostAccessAllowed", s =>
        {
            string allowed = s.Add("WebHostAllowed", "string");
            s.In("Host", allowed, "host");
            s.Out("", allowed, "allowed");
        });
        On("RequestHostAccess", s =>
        {
            string ask = s.Add("AskWebHost");
            s.In("Host", ask, "host");
            s.Op("", ask);
            s.Exit("OnGranted", ask, "allowed");
            s.Exit("OnDenied", ask, "denied");
            s.Exit("OnIgnored", ask, "ignored");
        });
    }

    private static void Connect(PackageNodeScope s, string client, Uri url, double retry)
    {
        if (!Connections.GetOrCreateValue(s.Writer).Add(client))
            return;
        string start = s.Add("OnStart");
        string open = s.Add("ConnectWebSocket");
        s.Set(open, "url", url.AbsoluteUri);
        s.Flow(start, "next", open);
        string again = s.Add("Delay");
        s.Set(again, "seconds", retry);
        s.Flow(again, "then", open);
        s.Flow(open, "failed", again);
        string closed = s.Add("OnWebSocketClosed");
        s.Flow(closed, "next", again);
    }

    // The client component a receiver names, through the global holding it.
    private static string? ClientOf(PackageNodeScope s)
    {
        string? id = s.Node.Ref("Client");
        for (int depth = 0; depth < 16 && s.Index.TryNode(id, out var node, out _); depth++)
        {
            id = node.Class switch
            {
                "GlobalReference" => node.Ref("Reference"),
                "ElementSource" or "ReferenceSource" => node.Ref("Source"),
                "ObjectRelay" => node.Ref("Input"),
                _ => null,
            };
        }
        return id;
    }

    // A stored address keeps a leading @ that marks it as one.
    private static Uri? WebAddress(DataTreeDictionary data)
    {
        if (((data.TryGetNode("URL") as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value is not string text)
            return null;
        text = text.TrimStart('@');
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "ws" or "wss" ? url : null;
    }

    private static double RetrySeconds(DataTreeDictionary data)
    {
        var stored = ((data.TryGetNode("ConnectRetryInterval") as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value;
        double seconds = stored is IConvertible n and not string and not bool ? Convert.ToDouble(n, CultureInfo.InvariantCulture) : 10d;
        return double.IsFinite(seconds) && seconds > 0d ? seconds : 10d;
    }
}
