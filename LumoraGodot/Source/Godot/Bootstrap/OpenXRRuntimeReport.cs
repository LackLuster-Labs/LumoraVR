// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;

namespace Lumora.Source.Godot.Bootstrap;

// WHICH OpenXR runtime the loader is going to pick, and which ones are installed.
//
// A PC can have several runtimes side by side, and exactly one of them is active at a time. Only the
// active one is asked whether a headset exists, so the whole of VR comes down to this one registry
// value: if Virtual Desktop's runtime is active and you are streaming over Steam Link, the game asks
// Virtual Desktop whether a headset is connected, it truthfully says no, and you land on the desktop
// wondering why. The runtime name never reaches us through Godot either - the OpenXR module prints
// it to stdout and the interface has no binding for it in this build - so when the session fails to
// start, the log says "no headset" and nothing about the reason.
//
// This reads the same two places the OpenXR loader reads, in the loader's own order, and turns them
// into something a person can act on. No registry package: the TFM is plain net9.0 so that would be
// a dependency for a diagnostic, and reg.exe is on every Windows install and costs one process at
// the moment VR has already failed. -xlinka
public static class OpenXRRuntimeReport
{
    private const string RegistryPath = @"SOFTWARE\Khronos\OpenXR\1";
    private const string ActiveValue = "ActiveRuntime";

    public enum RuntimeKind
    {
        Unknown,
        SteamVR,
        VirtualDesktop,
        Oculus,
        WindowsMixedReality,
        Varjo,
        Monado,
        Pimax,
    }

    private static bool _cached;
    private static string? _activePath;
    private static string _activeSource = "none";
    private static List<string> _availablePaths = new();

    public static string? ActivePath { get { Load(); return _activePath; } }

    public static RuntimeKind ActiveKind => Classify(ActivePath);

    public static IReadOnlyList<string> AvailablePaths { get { Load(); return _availablePaths; } }

    // Forget the cached read. The active runtime can be switched from SteamVR's settings while the
    // game sits on the desktop waiting for a headset, and re-reading is what makes that recoverable.
    public static void Invalidate() => _cached = false;

    public static string Describe(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            return "none";

        var kind = Classify(jsonPath);
        var name = kind == RuntimeKind.Unknown ? Path.GetFileNameWithoutExtension(jsonPath) : Friendly(kind);
        return File.Exists(jsonPath) ? name : name + " (missing from disk)";
    }

    public static string Friendly(RuntimeKind kind) => kind switch
    {
        RuntimeKind.SteamVR => "SteamVR",
        RuntimeKind.VirtualDesktop => "VirtualDesktopXR",
        RuntimeKind.Oculus => "Oculus / Meta",
        RuntimeKind.WindowsMixedReality => "Windows Mixed Reality",
        RuntimeKind.Varjo => "Varjo",
        RuntimeKind.Monado => "Monado",
        RuntimeKind.Pimax => "Pimax",
        _ => "unknown runtime",
    };

    public static RuntimeKind Classify(string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            return RuntimeKind.Unknown;

        var p = jsonPath.Replace('/', '\\').ToLowerInvariant();
        if (p.Contains("steamxr") || p.Contains("steamvr")) return RuntimeKind.SteamVR;
        if (p.Contains("virtualdesktop")) return RuntimeKind.VirtualDesktop;
        if (p.Contains("oculus") || p.Contains("\\meta\\") || p.Contains("metaxr")) return RuntimeKind.Oculus;
        if (p.Contains("mixedreality") || p.Contains("windowsmr")) return RuntimeKind.WindowsMixedReality;
        if (p.Contains("varjo")) return RuntimeKind.Varjo;
        if (p.Contains("monado")) return RuntimeKind.Monado;
        if (p.Contains("pimax")) return RuntimeKind.Pimax;
        return RuntimeKind.Unknown;
    }

    public static bool IsInstalled(RuntimeKind kind)
    {
        foreach (var path in AvailablePaths)
        {
            if (Classify(path) == kind)
                return true;
        }
        return false;
    }

    // Steam Link streams into SteamVR, and the OpenXR loader only ever asks the ONE active runtime. A PC that
    // also has Virtual Desktop or the Meta app installed often has theirs active, so a person sitting in a
    // headset streamed through SteamVR gets a truthful "no headset" and a desktop window. When that runtime
    // has just said no and SteamVR's server is running, this points the loader at SteamVR for this process
    // only (the loader reads XR_RUNTIME_JSON on every instance it creates); the machine's own setting is
    // left as it is. A person who set XR_RUNTIME_JSON themselves is never overridden. -xlinka
    public static bool SteerToRunningSteamVR()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON")))
            return false;
        Load();
        if (ActiveKind == RuntimeKind.SteamVR)
            return false;
        var steamVr = _availablePaths.FirstOrDefault(p => Classify(p) == RuntimeKind.SteamVR && File.Exists(p));
        if (steamVr == null)
            return false;
        var servers = Process.GetProcessesByName("vrserver");
        bool running = servers.Length > 0;
        foreach (var process in servers)
            process.Dispose();
        if (!running)
            return false;
        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", steamVr);
        return true;
    }

    // What to put in the log when OpenXR reported no headset. Names the runtime that was asked, and
    // where a different installed runtime is the likely answer, says so in the words of the thing the
    // person was trying to do.
    public static string DiagnoseNoHeadset()
    {
        Load();

        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(_activePath))
        {
            sb.Append("XR: no active OpenXR runtime is registered on this machine. ");
            sb.Append(_availablePaths.Count > 0
                ? "Installed but not active: " + string.Join(", ", DescribeAll()) + ". Pick one in its own settings (SteamVR: Settings > OpenXR > Set SteamVR as OpenXR Runtime). "
                : "Install a headset runtime, or launch with --desktop to skip the check. ");
            AppendWineCaveat(sb);
            return sb.ToString().TrimEnd();
        }

        var kind = Classify(_activePath);
        sb.Append("XR: the active OpenXR runtime is ").Append(Describe(_activePath))
          .Append(", from ").Append(_activeSource)
          .Append(", and it reports no headset. ");

        // A runtime that only ever serves its own streaming session is the single most common way to
        // end up flat: it is active because it was used last, and it has nothing to say about the
        // headset being used now.
        bool streamingRuntime = kind == RuntimeKind.VirtualDesktop;
        bool steamInstalled = IsInstalled(RuntimeKind.SteamVR);

        if (streamingRuntime && steamInstalled)
        {
            sb.Append("That runtime only serves a Virtual Desktop session. If you are streaming over ")
              .Append("Steam Link, or on a headset SteamVR drives, SteamVR has to be the active runtime: ")
              .Append("SteamVR > Settings > OpenXR > Set SteamVR as OpenXR Runtime. ");
        }
        else if (kind == RuntimeKind.SteamVR)
        {
            sb.Append("SteamVR is active, so the headset has to be connected to SteamVR before the game ")
              .Append("starts, or connect it now - VR is entered automatically when one appears. ");
        }

        if (_availablePaths.Count > 1)
            sb.Append("Installed runtimes: ").Append(string.Join(", ", DescribeAll())).Append(". ");

        AppendWineCaveat(sb);
        return sb.ToString().TrimEnd();
    }

    // WHAT WE READ IS THE PREFIX, NOT THE MACHINE.
    //
    // Under Proton the Khronos keys and the runtime JSON paths are whatever the prefix happens to
    // have, which is usually nothing: the OpenXR loader on that side is handed its runtime through
    // XR_RUNTIME_JSON instead, which is why that is read first and why an empty registry here is not
    // the finding it would be on Windows. Say so rather than telling a Linux user to go and change a
    // registry value that has no bearing on anything. -xlinka
    private static void AppendWineCaveat(StringBuilder sb)
    {
        if (!Lumora.Core.WinePlatform.IsWine)
            return;

        sb.Append("Note: running under ").Append(Lumora.Core.WinePlatform.Describe())
          .Append(", so the runtime list above is the prefix's and not the host's. On Linux the runtime ")
          .Append("normally arrives through the XR_RUNTIME_JSON environment variable")
          .Append(_activeSource == "the XR_RUNTIME_JSON environment variable" ? ", which is where this one came from." : ", which is not set here.");
    }

    private static IEnumerable<string> DescribeAll()
    {
        foreach (var path in _availablePaths)
            yield return Describe(path);
    }

    private static void Load()
    {
        if (_cached)
            return;
        _cached = true;

        _activePath = null;
        _activeSource = "none";
        _availablePaths = new List<string>();

        if (!OperatingSystem.IsWindows())
            return;

        // The loader honours this above everything else, and launchers use it to pin a runtime for
        // one process. If it is set and nobody remembers setting it, that IS the answer.
        var env = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
        if (!string.IsNullOrWhiteSpace(env))
        {
            _activePath = env.Trim();
            _activeSource = "the XR_RUNTIME_JSON environment variable";
            return;
        }

        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            var value = QueryValue($@"{hive}\{RegistryPath}", ActiveValue);
            if (string.IsNullOrWhiteSpace(value))
                continue;
            _activePath = value;
            _activeSource = hive == "HKCU" ? "the per-user registry" : "the machine registry";
            break;
        }

        foreach (var hive in new[] { "HKLM", "HKCU" })
        {
            foreach (var name in QueryValueNames($@"{hive}\{RegistryPath}\AvailableRuntimes"))
            {
                if (!_availablePaths.Contains(name, StringComparer.OrdinalIgnoreCase))
                    _availablePaths.Add(name);
            }
        }
    }

    private static string? QueryValue(string key, string valueName)
    {
        var output = RunReg($"query \"{key}\" /v {valueName}");
        if (output == null)
            return null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
                continue;
            // NAME    REG_SZ    value, split on the type token so a value with spaces survives.
            int type = trimmed.IndexOf("REG_", StringComparison.Ordinal);
            if (type < 0)
                continue;
            int space = trimmed.IndexOf(' ', type);
            if (space < 0)
                continue;
            var value = trimmed.Substring(space + 1).Trim();
            if (value.Length > 0)
                return value;
        }
        return null;
    }

    private static IEnumerable<string> QueryValueNames(string key)
    {
        var output = RunReg($"query \"{key}\"");
        if (output == null)
            yield break;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            int type = trimmed.IndexOf("    REG_", StringComparison.Ordinal);
            if (type <= 0)
                continue;
            var name = trimmed.Substring(0, type).Trim();
            if (name.Length > 0)
                yield return name;
        }
    }

    private static string? RunReg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(4000))
                return null;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
