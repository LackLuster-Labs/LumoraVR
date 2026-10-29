// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;

namespace Lumora.Core.Photos;

// What the streaming program's own log says it is doing. It starts a new log file every run, keeps it open
// for others to read, and writes a fixed banner the moment a recording, a stream or a replay buffer actually
// starts and again when it stops, whatever the reason. Reading those needs no WebSocket server, no password
// and no setup, so it answers for the program as most people run it.
//
// Only the bytes added since the last look are read, into one buffer kept for the purpose. A log in a
// folder of its own (a portable install) is not found; the link answers for that one. Only asked while the
// program is running, so a log left mid-recording by a crash says nothing. -xlinka
public sealed class ObsLogTail
{
    private const int MaxFirstRead = 8 * 1024 * 1024;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private IReadOnlyList<string>? _folders;
    private string? _path;
    private long _offset;
    private int _carry;
    private bool _skipToLine;
    private bool _recording;
    private bool _streaming;
    private bool _replay;

    // Where to look; null looks where the program keeps its logs on this machine.
    public IReadOnlyList<string>? Folders
    {
        get => _folders;
        set
        {
            _folders = value;
            Forget();
        }
    }

    public string? CurrentLog => _path;

    public CaptureState State => _streaming ? CaptureState.Streaming
        : _recording || _replay ? CaptureState.Recording
        : CaptureState.None;

    public CaptureState Read()
    {
        var newest = Newest(_folders ?? DefaultFolders);
        if (newest == null)
        {
            Forget();
            return CaptureState.None;
        }
        if (!string.Equals(newest, _path, StringComparison.Ordinal))
        {
            Forget();
            _path = newest;
        }

        using var handle = File.OpenHandle(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(handle);
        if (length < _offset)
        {
            Forget();
            _path = newest;
        }
        if (_offset == 0 && length > MaxFirstRead)
        {
            _offset = length - MaxFirstRead;
            _skipToLine = true;
        }

        while (_offset < length)
        {
            int read = RandomAccess.Read(handle, _buffer.AsSpan(_carry), _offset);
            if (read <= 0)
                break;
            _offset += read;
            int filled = _carry + read;
            var text = _buffer.AsSpan(0, filled);
            int end = text.LastIndexOf((byte)'\n');
            if (end < 0)
            {
                // A line longer than the buffer is no banner; drop it rather than stall on it.
                _carry = filled == _buffer.Length ? 0 : filled;
                continue;
            }
            var lines = text[..(end + 1)];
            if (_skipToLine)
            {
                _skipToLine = false;
                lines = lines[(lines.IndexOf((byte)'\n') + 1)..];
            }
            Scan(lines);
            _carry = filled - (end + 1);
            text[(end + 1)..].CopyTo(_buffer);
        }
        return State;
    }

    public void Forget()
    {
        _path = null;
        _offset = 0;
        _carry = 0;
        _skipToLine = false;
        _recording = false;
        _streaming = false;
        _replay = false;
    }

    private void Scan(ReadOnlySpan<byte> text)
    {
        while (true)
        {
            int at = text.IndexOf("==== "u8);
            if (at < 0)
                return;
            text = text[(at + 5)..];
            if (text.StartsWith("Recording Start"u8))
                _recording = true;
            else if (text.StartsWith("Recording Stop"u8))
                _recording = false;
            else if (text.StartsWith("Streaming Start"u8))
                _streaming = true;
            else if (text.StartsWith("Streaming Stop"u8))
                _streaming = false;
            else if (text.StartsWith("Replay Buffer Start"u8))
                _replay = true;
            else if (text.StartsWith("Replay Buffer Stop"u8))
                _replay = false;
            else if (text.StartsWith("Shutting down"u8))
                _recording = _streaming = _replay = false;
        }
    }

    private static string? Newest(IReadOnlyList<string> folders)
    {
        string? newest = null;
        var newestAt = DateTime.MinValue;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.txt"))
            {
                var at = file.LastWriteTimeUtc;
                if (at > newestAt)
                {
                    newestAt = at;
                    newest = file.FullName;
                }
            }
        }
        return newest;
    }

    private static IReadOnlyList<string>? _defaultFolders;

    private static IReadOnlyList<string> DefaultFolders => _defaultFolders ??= FindFolders();

    private static IReadOnlyList<string> FindFolders()
    {
        var folders = new List<string>();
        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrEmpty(root))
                return;
            var path = root;
            foreach (var part in parts)
                path = Path.Combine(path, part);
            folders.Add(path);
        }

        if (OperatingSystem.IsWindows())
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs");

        // A native build beside the game: from Linux itself, or from inside Wine through its root drive.
        string? home = Environment.GetEnvironmentVariable("HOME");
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (OperatingSystem.IsWindows())
        {
            if (!WinePlatform.IsWine || WinePlatform.IsSimulated)
                return folders;
            home = home != null && home.StartsWith('/') ? "Z:" + home : null;
            config = config != null && config.StartsWith('/') ? "Z:" + config : null;
        }
        else if (!OperatingSystem.IsLinux())
        {
            return folders;
        }
        Add(config ?? (home != null ? Path.Combine(home, ".config") : null), "obs-studio", "logs");
        Add(home, ".var", "app", "com.obsproject.Studio", "config", "obs-studio", "logs");
        return folders;
    }
}
