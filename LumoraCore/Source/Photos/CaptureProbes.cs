// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lumora.Core.Photos;

// Hands the name of every process running on this machine to the scan. False when it cannot look.
public interface ICaptureProbe
{
    bool Scan(CaptureScan scan);
}

// One Toolhelp snapshot: a single call into the kernel, the names read straight out of the entry with no
// string made for any of them. Needs no rights past the user's own. Under Wine it sees the prefix only.
internal sealed unsafe class WindowsProcessProbe : ICaptureProbe
{
    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll")]
    private static extern int Process32FirstW(IntPtr snapshot, ProcessEntry* entry);

    [DllImport("kernel32.dll")]
    private static extern int Process32NextW(IntPtr snapshot, ProcessEntry* entry);

    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(IntPtr handle);

    public bool Scan(CaptureScan scan)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == Invalid)
            return false;
        try
        {
            ProcessEntry entry = default;
            entry.Size = (uint)sizeof(ProcessEntry);
            for (int more = Process32FirstW(snapshot, &entry); more != 0; more = Process32NextW(snapshot, &entry))
            {
                var name = new ReadOnlySpan<char>(entry.ExeFile, 260);
                int end = name.IndexOf('\0');
                scan.See(end >= 0 ? name[..end] : name);
            }
            return true;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}

// /proc on Linux, and the host's /proc from inside Wine through its root drive, where a program running
// natively next to the game is otherwise out of sight. Each process is named by the file name of its first
// argument rather than its comm, which the kernel cuts to fifteen letters.
internal sealed class ProcProbe : ICaptureProbe
{
    private readonly string _root;
    private readonly byte[] _bytes = new byte[512];
    private readonly char[] _chars = new char[512];

    public ProcProbe(string root) => _root = root;

    public bool Scan(CaptureScan scan)
    {
        if (!Directory.Exists(_root))
            return false;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir.AsSpan());
            if (id.Length == 0 || !IsNumber(id))
                continue;
            int length;
            try
            {
                using var handle = File.OpenHandle(Path.Join(dir, "cmdline"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                length = RandomAccess.Read(handle, _bytes, 0);
            }
            catch (Exception)
            {
                continue; // gone since the listing, or not ours to read
            }
            var first = _bytes.AsSpan(0, length);
            int nul = first.IndexOf((byte)0);
            if (nul >= 0)
                first = first[..nul];
            if (first.IsEmpty)
                continue;
            int count = Encoding.UTF8.GetChars(first, _chars);
            var name = _chars.AsSpan(0, count);
            int slash = name.LastIndexOfAny('/', '\\');
            if (slash >= 0)
                name = name[(slash + 1)..];
            // A program that rewrote its arguments into one line keeps them after a space.
            int space = name.IndexOf(' ');
            if (space > 0)
                name = name[..space];
            scan.See(name);
        }
        return true;
    }

    private static bool IsNumber(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return true;
    }
}

internal sealed class BothProbes : ICaptureProbe
{
    private readonly ICaptureProbe _first;
    private readonly ICaptureProbe _second;

    public BothProbes(ICaptureProbe first, ICaptureProbe second)
    {
        _first = first;
        _second = second;
    }

    public bool Scan(CaptureScan scan)
    {
        bool first = _first.Scan(scan);
        bool second;
        try
        {
            second = _second.Scan(scan);
        }
        catch (Exception)
        {
            second = false;
        }
        return first || second;
    }
}
