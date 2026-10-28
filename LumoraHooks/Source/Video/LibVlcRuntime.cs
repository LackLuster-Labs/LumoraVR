// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

#if GODOT_PC
using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;
using LibVLCSharp.Shared;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks.Video;

// One process-wide libVLC. Core.Initialize loads the native library and must happen exactly once
// before any LibVLCSharp type is touched, so everything goes through here and a failure is recorded
// as a reason string instead of an exception - a machine without libVLC still has to run, it just
// cannot play anything but Theora. -xlinka
public static class LibVlcRuntime
{
    private static readonly object _sync = new();
    private static bool _attempted;
    private static LibVLC? _instance;

    public static string? LogFilePath { get; private set; }

    public static string? UnavailableReason { get; private set; }

    public static bool IsAvailable => Instance != null;

    // Called at boot so the plugin scan and library load happen on a worker before anyone pastes a
    // link; Instance stays lazy and thread-safe for everything else.
    public static void WarmUp()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = Instance; }
            catch (Exception ex) { LumoraLogger.Debug($"LibVlcRuntime: warm-up failed: {ex.Message}"); }
        });
    }

    public static LibVLC? Instance
    {
        get
        {
            lock (_sync)
            {
                if (_attempted)
                    return _instance;
                _attempted = true;
                try
                {
                    // Windows takes the directory of the natives we ship beside the assemblies. Linux loads
                    // its bundle itself (LibVLCSharp refuses a directory there) and keeps nativeDir null.
                    string? nativeDir = OperatingSystem.IsWindows() ? FindWindowsNativeDirectory() : null;
                    if (OperatingSystem.IsWindows() && nativeDir == null)
                    {
                        UnavailableReason = "libvlc natives not found next to the executable";
                        LumoraLogger.Warn($"LibVlcRuntime: {UnavailableReason}");
                        return null;
                    }
                    string? linuxBundle = OperatingSystem.IsLinux() ? PrepareLinux() : null;
                    string? pluginDir = nativeDir ?? linuxBundle;

                    LibVLCSharp.Shared.Core.Initialize(nativeDir);

                    // Without plugins.dat every start scans and loads all hundred-odd plugin DLLs, which
                    // is the multi-second stall the first video paste used to pay on the main thread.
                    // libVLC only writes the cache when told to, so the first run does; later runs read it.
                    var args = new System.Collections.Generic.List<string>
                    {
                        "--intf=dummy",
                        "--no-osd",
                        "--no-video-title-show",
                        "--no-snapshot-preview",
                        "--no-stats",
                        "--verbose=1",
                    };
                    if (pluginDir != null && !File.Exists(Path.Combine(pluginDir, "plugins", "plugins.dat")))
                        args.Add("--reset-plugins-cache");

                    _instance = new LibVLC(args.ToArray());

                    // libVLC writes its own log file. Its managed Log event is not used on purpose: on
                    // Windows LibVLCSharp renders every message (all levels, before any filter) by
                    // pushing libVLC's va_list through msvcrt's vsprintf, and a format that path
                    // mishandles becomes garbage the string marshaller reads off the end of - an
                    // access violation inside the runtime, seconds into any fMP4 stream. -xlinka
                    try
                    {
                        // Beside the rest of this run's data: a run on a folder of its own (LUMORA_DATA_DIR) must not
                        // write into the everyday install's logs.
                        string logDir = Lumora.Core.Persistence.PathResolver.IsInitialized
                            ? Lumora.Core.Persistence.PathResolver.LogsPath
                            : ProjectSettings.GlobalizePath("user://logs");
                        Directory.CreateDirectory(logDir);
                        LogFilePath = Path.Combine(logDir, "libvlc.log");
                        _instance.SetLogFile(LogFilePath);
                    }
                    catch (Exception ex)
                    {
                        LumoraLogger.Debug($"LibVlcRuntime: no libVLC log file: {ex.Message}");
                    }

                    LumoraLogger.Log($"LibVlcRuntime: libVLC {_instance.Version} ready (natives: {pluginDir ?? "system"}, log: {LogFilePath ?? "none"})");
                }
                catch (Exception ex)
                {
                    UnavailableReason = ex.Message;
                    _instance = null;
                    LumoraLogger.Warn($"LibVlcRuntime: libVLC unavailable: {ex.Message}");
                }
                return _instance;
            }
        }
    }

    // VideoLAN.LibVLC.Windows drops libvlc/<rid>/ into the build output, and a Godot Windows export
    // copies that whole output into data_<name>_<arch>/ next to the .exe. Both layouts are searched
    // because an editor run and an exported run resolve the assembly directory differently. -xlinka
    private static string? FindWindowsNativeDirectory()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

        foreach (var root in CandidateRoots())
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string ridDir = Path.Combine(root, "libvlc", rid);
            if (File.Exists(Path.Combine(ridDir, "libvlc.dll")))
                return ridDir;
            string flatDir = Path.Combine(root, "libvlc");
            if (File.Exists(Path.Combine(flatDir, "libvlc.dll")))
                return flatDir;
        }
        return null;
    }

    private static nint _linuxLibvlc;
    private static bool _linuxResolverSet;

    // The Linux export carries its own libVLC in libvlc/linux-x64 (tools/libvlc_linux.py) because SteamOS
    // and most distros have none, or only libvlc.so.5 without the libvlc.so name LibVLCSharp's import
    // asks for. Nothing there can lean on LD_LIBRARY_PATH, so every library is loaded by full path in
    // lib/load-order.txt order, dependencies first; each later DT_NEEDED then matches an object already in
    // the process by SONAME. With no bundle (an editor run) the distro's libvlc.so.5 is used. Returns the
    // bundle directory, or null. -xlinka
    private static string? PrepareLinux()
    {
        string? bundle = FindLinuxBundle();
        if (bundle != null)
        {
            string lib = Path.Combine(bundle, "lib");
            int failed = 0;
            foreach (var line in File.ReadAllLines(Path.Combine(lib, "load-order.txt")))
            {
                string soname = line.Trim();
                if (soname.Length == 0)
                    continue;
                try
                {
                    nint handle = NativeLibrary.Load(Path.Combine(lib, soname));
                    if (soname == "libvlc.so.5")
                        _linuxLibvlc = handle;
                }
                catch (Exception ex)
                {
                    // Usually a desktop library the bundle expects from the system is absent; whatever
                    // depends on it is lost, the rest still plays.
                    if (++failed <= 5)
                        LumoraLogger.Warn($"LibVlcRuntime: bundled {soname} did not load: {ex.Message}");
                }
            }
            SetNativeEnvironment("VLC_PLUGIN_PATH", Path.Combine(bundle, "plugins"));
        }

        if (!_linuxResolverSet)
        {
            _linuxResolverSet = true;
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ResolveLinux);
        }
        return bundle;
    }

    private static nint ResolveLinux(string name, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "libvlc")
            return IntPtr.Zero;
        if (_linuxLibvlc == IntPtr.Zero && !NativeLibrary.TryLoad("libvlc.so.5", out _linuxLibvlc))
            NativeLibrary.TryLoad("libvlc.so", out _linuxLibvlc);
        return _linuxLibvlc;
    }

    private static string? FindLinuxBundle()
    {
        foreach (var root in CandidateRoots())
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string dir = Path.Combine(root, "libvlc", "linux-x64");
            if (File.Exists(Path.Combine(dir, "lib", "load-order.txt")))
                return dir;
        }
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetEnvFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    // Environment.SetEnvironmentVariable only changes the runtime's own copy on Unix; libVLC reads the
    // process environment with getenv, so this goes through libc.
    private static void SetNativeEnvironment(string name, string value)
    {
        if (NativeLibrary.TryLoad("libc.so.6", out var libc) && NativeLibrary.TryGetExport(libc, "setenv", out var setenv))
            Marshal.GetDelegateForFunctionPointer<SetEnvFunction>(setenv)(name, value, 1);
        else
            LumoraLogger.Warn($"LibVlcRuntime: could not set {name}; bundled plugins will not be found");
    }

    private static string?[] CandidateRoots()
    {
        string? exeDir = null;
        try { exeDir = Path.GetDirectoryName(OS.GetExecutablePath()); }
        catch (Exception ex) { LumoraLogger.Debug($"LibVlcRuntime: executable path unavailable: {ex.Message}"); }

        string? dataDir = null;
        if (!string.IsNullOrEmpty(exeDir))
        {
            try
            {
                foreach (var candidate in Directory.GetDirectories(exeDir!, "data_*"))
                {
                    dataDir = candidate;
                    break;
                }
            }
            catch (Exception ex) { LumoraLogger.Debug($"LibVlcRuntime: no data directory beside the executable: {ex.Message}"); }
        }

        return new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(LibVlcRuntime).Assembly.Location),
            dataDir,
            exeDir,
        };
    }
}
#endif
