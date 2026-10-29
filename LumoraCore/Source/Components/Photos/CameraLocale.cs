// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Components;

// The camera's route strings, kept in one place for the locale sweep.
public static class CameraLocale
{
    public static readonly LocaleText NewRoute = "Camera.Route.New".AsLocale("New route");
    public static readonly LocaleText RouteRow = "Camera.Route.Row".AsLocale("Route: {0} stops, {1} s");
    public static readonly LocaleText RouteNeedsAnchors = "Camera.Route.NeedsAnchors".AsLocale("A route needs two anchors here");
    public static readonly LocaleText RouteRefused = "Camera.Route.Refused".AsLocale("This world does not let you add a route");
    public static readonly LocaleText AnchorsAndRoutes = "Camera.Route.Caption".AsLocale("Anchors and routes here");
}
