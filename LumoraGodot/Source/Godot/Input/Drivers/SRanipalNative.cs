// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input.Drivers;

// The SRanipal native surface: HTC's eye and lip tracking runtime, as used by the Vive Pro Eye and
// the Vive Facial Tracker.
//
// Every struct below mirrors the C++ layout field for field. Getting a layout wrong here does not
// crash, it hands back plausible-looking garbage, so the shapes are copied from the shipped SDK
// headers rather than reconstructed, and SRanipalLayout has a size assertion for each one.
//
// TWO DELIBERATE DIFFERENCES FROM THE SDK'S OWN BINDINGS, both correctness rather than taste:
//
// Every `bool` is a `byte` here. C# marshals a struct `bool` as a four-byte Win32 BOOL, while a C++
// `bool` is one byte; the offsets still line up because the following field is four-byte aligned and
// the padding absorbs it, but the VALUE is then read out of three bytes of padding the native side
// never promised to zero. A byte reads the one byte that means something. The same applies to the
// `IsViveProEye` return, where the x64 ABI only defines AL: UnmanagedType.U1 reads AL and nothing
// else, where the default four-byte BOOL reads three bytes of undefined register.
//
// No managed arrays. The SDK declares the tracking-improvement list and the 60 lip weights as
// [MarshalAs(ByValArray)] fields, which makes the whole struct non-blittable and costs a full
// marshal-and-allocate on every single call. These are polled at headset frame rate for the length
// of a session. Fixed inline storage keeps both structs blittable, so the call is a pin and a
// pointer. -xlinka
internal static class SRanipalNative
{
    private const string Library = "SRanipal";

    public const int AnipalTypeEye = 2;
    public const int AnipalTypeLip = 3;

    public enum SRError : int
    {
        RuntimeNotFound = -3,
        NotInitial = -2,
        Failed = -1,
        Work = 0,
        InvalidInput = 1,
        FileNotFound = 2,
        DataNotFound = 13,
        Undefined = 319,
        InitialFailed = 1001,
        NotImplemented = 1003,
        NullPointer = 1004,
        OverMaxLength = 1005,
        FileInvalid = 1006,
        UninstallSteam = 1007,
        MemcpyFail = 1008,
        NotMatch = 1009,
        NodeNotExist = 1010,
        UnknownModule = 1011,
        ModuleFull = 1012,
        UnknownType = 1013,
        InvalidModule = 1014,
        InvalidType = 1015,
        MemoryNotEnough = 1016,
        Busy = 1017,
        NotSupported = 1018,
        InvalidValue = 1019,
        ComingSoon = 1020,
        InvalidChange = 1021,
        Timeout = 1022,
        DeviceNotFound = 1023,
        InvalidDevice = 1024,
        NotAuthorized = 1025,
        Already = 1026,
        Internal = 1027,
        ConnectionFailed = 1028,
        AllocationFailed = 1029,
        OperationFailed = 1030,
        NotAvailable = 1031,
        CallbackInProgress = 1032,
        ServiceNotFound = 1033,
        DisabledByUser = 1034,
        EulaNotAccept = 1035,
        RuntimeNoResponse = 1036,
        OpenClNotSupport = 1037,
        NotSupportEyeTracking = 1038,
    }

    // Which fields of a SingleEyeData the runtime is standing behind this frame. Bit indices, not a
    // mask: the accessor shifts.
    public enum EyeValidity : int
    {
        GazeOrigin = 0,
        GazeDirection = 1,
        PupilDiameter = 2,
        EyeOpenness = 3,
        PupilPositionInSensorArea = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SRVector2
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SRVector3
    {
        public float X;
        public float Y;
        public float Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SingleEyeData
    {
        public ulong ValidityBitMask;
        // Right-handed, millimetres, HMD-relative: +X left, +Y up, +Z forward.
        public SRVector3 GazeOriginMm;
        public SRVector3 GazeDirectionNormalized;
        public float PupilDiameterMm;
        public float EyeOpenness;
        public SRVector2 PupilPositionInSensorArea;

        public readonly bool IsValid(EyeValidity validity)
            => (ValidityBitMask & (1UL << (int)validity)) != 0UL;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CombinedEyeData
    {
        public SingleEyeData EyeData;
        public byte ConvergenceDistanceValidity;
        private byte _pad0;
        private byte _pad1;
        private byte _pad2;
        public float ConvergenceDistanceMm;
    }

    // Hints the runtime raises when it wants the headset moved or recalibrated. Never read, but it
    // sits between the eye data and the expression data and has to occupy the same 44 bytes.
    [StructLayout(LayoutKind.Sequential)]
    public struct TrackingImprovements
    {
        public int Count;
        private int _i0, _i1, _i2, _i3, _i4, _i5, _i6, _i7, _i8, _i9;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VerboseData
    {
        public SingleEyeData Left;
        public SingleEyeData Right;
        public CombinedEyeData Combined;
        public TrackingImprovements TrackingImprovements;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SingleEyeExpression
    {
        public float EyeWide;
        public float EyeSqueeze;
        public float EyeFrown;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EyeExpression
    {
        public SingleEyeExpression Left;
        public SingleEyeExpression Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EyeDataV2
    {
        public byte NoUser;
        private byte _pad0;
        private byte _pad1;
        private byte _pad2;
        public int FrameSequence;
        public int TimestampMs;
        public VerboseData Verbose;
        public EyeExpression Expression;
    }

    // 60 floats of which the first 37 are the LipShape_v2 weights; the rest is headroom the runtime
    // reserves and always writes.
    public const int LipWeightCount = 60;
    public const int LipShapeCount = 37;

    [InlineArray(LipWeightCount)]
    public struct LipWeights
    {
        private float _element0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PredictionDataV2
    {
        public LipWeights BlendShapeWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LipDataV2
    {
        public int Frame;
        public int Time;
        public IntPtr Image;
        public PredictionDataV2 Prediction;
    }

    // Lip weight indices, from LipShape_v2. Only the ones that map onto a MouthShape are named.
    public const int LipJawRight = 0;
    public const int LipJawLeft = 1;
    public const int LipJawForward = 2;
    public const int LipJawOpen = 3;
    public const int LipMouthApeShape = 4;
    public const int LipMouthUpperRight = 5;
    public const int LipMouthUpperLeft = 6;
    public const int LipMouthLowerRight = 7;
    public const int LipMouthLowerLeft = 8;
    public const int LipMouthUpperOverturn = 9;
    public const int LipMouthLowerOverturn = 10;
    public const int LipMouthPout = 11;
    public const int LipMouthSmileRight = 12;
    public const int LipMouthSmileLeft = 13;
    public const int LipMouthSadRight = 14;
    public const int LipMouthSadLeft = 15;
    public const int LipCheekPuffRight = 16;
    public const int LipCheekPuffLeft = 17;
    public const int LipCheekSuck = 18;
    public const int LipMouthUpperUpRight = 19;
    public const int LipMouthUpperUpLeft = 20;
    public const int LipMouthLowerDownRight = 21;
    public const int LipMouthLowerDownLeft = 22;
    public const int LipMouthUpperInside = 23;
    public const int LipMouthLowerInside = 24;
    public const int LipMouthLowerOverlay = 25;
    public const int LipTongueLongStep1 = 26;
    public const int LipTongueLongStep2 = 27;
    public const int LipTongueDown = 28;
    public const int LipTongueUp = 29;
    public const int LipTongueRight = 30;
    public const int LipTongueLeft = 31;
    public const int LipTongueRoll = 32;
    public const int LipTongueUpLeft = 33;
    public const int LipTongueUpRight = 34;
    public const int LipTongueDownLeft = 35;
    public const int LipTongueDownRight = 36;

    [DllImport(Library, EntryPoint = "Initial", ExactSpelling = true)]
    public static extern SRError Initial(int anipalType, IntPtr config);

    [DllImport(Library, EntryPoint = "Release", ExactSpelling = true)]
    public static extern SRError Release(int anipalType);

    [DllImport(Library, EntryPoint = "IsViveProEye", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool IsViveProEye();

    [DllImport(Library, EntryPoint = "GetEyeData_v2", ExactSpelling = true)]
    public static extern SRError GetEyeDataV2(ref EyeDataV2 data);

    [DllImport(Library, EntryPoint = "GetLipData_v2", ExactSpelling = true)]
    public static extern SRError GetLipDataV2(ref LipDataV2 data);

    [DllImport(Library, EntryPoint = "IsUserNeedCalibration", ExactSpelling = true)]
    public static extern int IsUserNeedCalibration(ref byte need);

    // Where the DLL comes from.
    //
    // An SRanipal.dll shipped beside the executable can be a different version from the runtime
    // service on the machine. Nothing here is redistributed: the loader takes the DLL out of the
    // SRanipal installation that is actually running, so the two always match, and a machine without
    // SRanipal simply has no eye tracking rather than a stale DLL that cannot talk to anything. The
    // default probe still runs first, so dropping a DLL next to the executable keeps working for anyone
    // who wants that. -xlinka
    private static readonly string[] InstallRoots =
    {
        @"C:\Program Files\VIVE\SRanipal",
        @"C:\Program Files (x86)\VIVE\SRanipal",
    };

    private static readonly string[] DllSubPaths =
    {
        "SRanipal.dll",
        @"tools\eye_calibration\EyeCalibration_Data\Plugins\SRanipal.dll",
        @"tools\eye_calibration_dashboard\EyeCalibrationDashboard_Data\Plugins\SRanipal.dll",
    };

    private static bool _resolverInstalled;
    private static int _probeState; // 0 = unknown, 1 = usable, 2 = not usable

    public static IEnumerable<string> EnumerateInstallRoots()
    {
        foreach (var root in InstallRoots)
        {
            if (Directory.Exists(root))
                yield return root;
        }
    }

    // The runtime executable the service launches. Its presence is what counts as "this machine has
    // SRanipal", and it is a better question than "is a Vive Pro Eye plugged in right now", because
    // the facial tracker is a separate USB device that needs the same runtime.
    public static string? FindRuntimeExecutable()
    {
        foreach (var root in EnumerateInstallRoots())
        {
            var exe = Path.Combine(root, RuntimeProcessName + ".exe");
            if (File.Exists(exe))
                return exe;
        }
        return null;
    }

    private const string RuntimeProcessName = "sr_runtime";

    public static bool IsRuntimeRunning()
    {
        try
        {
            var found = Process.GetProcessesByName(RuntimeProcessName);
            foreach (var p in found)
                p.Dispose();
            return found.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Whether anything the runtime could track is plugged in: a present USB device under the vendor's
    // id, or the eye tracker maker's. The eye cameras, the facial tracker and the headsets that carry
    // them all enumerate under one of the two. Present devices only, so a tracker that was plugged in
    // once last year does not count. -xlinka
    private static readonly string[] VendorUsbIds = { "VID_0BB4&", "VID_2104&" };
    private const int CmGetIdListFilterEnumerator = 0x00000001;
    private const int CmGetIdListFilterPresent = 0x00000100;
    private const int CrSuccess = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern int CmGetDeviceIdListSize(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    private static extern int CmGetDeviceIdList(string? filter, [Out] char[] buffer, int length, int flags);

    public static bool IsVendorDevicePresent()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            const int flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
            if (CmGetDeviceIdListSize(out int length, "USB", flags) != CrSuccess || length <= 0)
                return false;
            var buffer = new char[length];
            if (CmGetDeviceIdList("USB", buffer, length, flags) != CrSuccess)
                return false;
            var ids = new string(buffer);
            foreach (var id in VendorUsbIds)
            {
                if (ids.Contains(id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SRanipal: could not list USB devices ({ex.Message}).");
            return false;
        }
    }

    public static void EnsureResolver()
    {
        if (_resolverInstalled)
            return;
        _resolverInstalled = true;

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SRanipalNative).Assembly, Resolve);
        }
        catch (Exception ex)
        {
            // Another resolver already owns this assembly. The default probe still applies, so this
            // is only a loss of the install-directory fallback.
            LumoraLogger.Warn($"SRanipal: could not install the library resolver ({ex.Message}); falling back to the default probe.");
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        foreach (var root in EnumerateInstallRoots())
        {
            foreach (var sub in DllSubPaths)
            {
                var full = Path.Combine(root, sub);
                if (!File.Exists(full))
                    continue;
                if (NativeLibrary.TryLoad(full, out handle))
                {
                    LumoraLogger.Log($"SRanipal: loaded the runtime library from '{full}'");
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    // Whether the DLL can be found AND called at all. Latched, because the answer cannot change
    // within a process and the failure path is an exception.
    public static bool IsLibraryUsable()
    {
        if (_probeState != 0)
            return _probeState == 1;

        EnsureResolver();
        try
        {
            IsViveProEye();
            _probeState = 1;
        }
        catch (DllNotFoundException)
        {
            _probeState = 2;
        }
        catch (EntryPointNotFoundException ex)
        {
            LumoraLogger.Warn($"SRanipal: the library loaded but is the wrong one ({ex.Message}).");
            _probeState = 2;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SRanipal: the library could not be called ({ex.GetType().Name}: {ex.Message}).");
            _probeState = 2;
        }
        return _probeState == 1;
    }

    // IsViveProEye without the exception surface, for the detection gate.
    public static bool SafeIsViveProEye()
    {
        if (!IsLibraryUsable())
            return false;
        try
        {
            return IsViveProEye();
        }
        catch
        {
            return false;
        }
    }
}
