// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input.Drivers;

// Eyelids, eyes and face from a headset streamed through Steam Link.
//
// Steam Link hands the headset's face and eye data to programs on the PC as OSC over UDP: in its own
// layout ("/sl/...", the face as the standard face-tracking expression weights) when SteamVR's Steam Link
// OSC port is set to Custom (9015), and as avatar-parameter and eye-tracking addresses on the default port
// (9000). The OpenXR gaze ray carries no eyelids at all, so this is where real blinks come from while
// streaming. Both ports and both layouts are read, because which one a person has set is not ours to
// know.
//
// Choices that matter, each one a way this goes wrong when done the obvious way:
//  - lids are only claimed once a lid value has actually arrived. Openness that starts at zero shuts the
//    avatar's eyes for good on a headset that sends a gaze but no lids;
//  - eyes are live while gaze values keep arriving, not while a face-camera confidence says so. A headset
//    with eye cameras and no face cameras reports zero face confidence and would never count as tracking;
//  - this only takes the eyes over when it has lids. A gaze alone is what the OpenXR ray already gives,
//    with blinks of its own, so that path keeps them;
//  - it listens on loopback only, and a port another program holds is simply skipped. -xlinka
public sealed class SteamLinkFaceDriver : IInputDriver
{
    private const int CustomPort = 9015;
    private const int DefaultPort = 9000;
    private const long LiveForMs = 500;

    public int UpdateOrder => 100;

    // Whether this driver owns the head device's eye fields right now. The OpenXR gaze stands down while
    // it does, so the two never write the same fields in one frame.
    public static bool EyesLive { get; private set; }

    private InputInterface? _input;
    private readonly List<UdpClient> _sockets = new();
    private readonly object _gate = new();
    private bool _started;
    private volatile bool _stopping;

    // Written by the receive threads under _gate, copied out once a frame into _frameWeights, which only
    // the frame thread touches.
    private readonly Dictionary<string, float> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _frameWeights = new(StringComparer.Ordinal);
    private System.Numerics.Vector3 _gazePoint;
    private System.Numerics.Vector3 _gazeDirection;
    private long _gazePointMs;
    private long _gazeDirectionMs;
    private long _lidsMs;
    private long _faceMs;
    private float _leftOpen = 1f;
    private float _rightOpen = 1f;
    private bool _announcedEyes;
    private bool _announcedFace;

    // Only where the game runs on a PC with a headset. A standalone headset has no Steam Link to hear.
    public static bool ShouldRegister() => !global::Godot.OS.HasFeature("android");

    public void RegisterInputs(InputInterface inputInterface)
    {
        _input = inputInterface;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public void UpdateInputs(float deltaTime)
    {
        var input = _input;
        if (input == null)
            return;

        // Ports are opened the first time VR is on, so a flat session never holds them.
        if (!_started && input.IsVRActive)
            Start();

        var head = input.HeadDevice;
        var mouth = input.MouthDevice;
        long now = Environment.TickCount64;

        System.Numerics.Vector3 point, direction;
        long pointMs, directionMs, lidsMs, faceMs;
        float leftOpen, rightOpen;
        var w = _frameWeights;
        lock (_gate)
        {
            point = _gazePoint;
            direction = _gazeDirection;
            pointMs = _gazePointMs;
            directionMs = _gazeDirectionMs;
            lidsMs = _lidsMs;
            faceMs = _faceMs;
            leftOpen = _leftOpen;
            rightOpen = _rightOpen;
            foreach (var pair in _weights)
                w[pair.Key] = pair.Value;
        }

        bool haveGaze = now - pointMs <= LiveForMs || now - directionMs <= LiveForMs;
        bool haveLids = now - lidsMs <= LiveForMs;
        bool eyes = input.IsVRActive && head != null && haveGaze && haveLids && !SRanipalDriver.EyesLive;

        if (eyes)
            WriteEyes(head!, point, now - pointMs <= LiveForMs, direction, leftOpen, rightOpen, w);
        else if (EyesLive && head != null)
        {
            head.HasEyeTracking = false;
            head.LeftEyeOpenness = 1f;
            head.RightEyeOpenness = 1f;
        }
        if (eyes != EyesLive)
        {
            EyesLive = eyes;
            if (eyes && !_announcedEyes)
            {
                _announcedEyes = true;
                LumoraLogger.Log("SteamLinkFace: eyes and eyelids live from Steam Link");
            }
        }

        bool face = input.IsVRActive && mouth != null && now - faceMs <= LiveForMs;
        if (face)
        {
            WriteMouth(mouth!, w);
            if (!_announcedFace)
            {
                _announcedFace = true;
                LumoraLogger.Log("SteamLinkFace: face tracking live from Steam Link");
            }
        }
        else if (mouth != null && _announcedFace && mouth.IsTracking)
        {
            mouth.IsTracking = false;
            mouth.ClearWeights();
        }
    }

    private static void WriteEyes(HeadDevice head, System.Numerics.Vector3 point, bool havePoint,
        System.Numerics.Vector3 direction, float leftOpen, float rightOpen, Dictionary<string, float> w)
    {
        // Head-local with -Z forward, the same frame the eye fields are read in. A fixation point gives
        // each eye its own aim, which is the small inward turn real eyes make.
        float halfIpd = Math.Clamp(head.IPD, 0.05f, 0.08f) * 0.5f;
        System.Numerics.Vector3 combined, left, right;
        if (havePoint && point.LengthSquared() > 1e-6f)
        {
            combined = System.Numerics.Vector3.Normalize(point);
            left = System.Numerics.Vector3.Normalize(point - new System.Numerics.Vector3(-halfIpd, 0f, 0f));
            right = System.Numerics.Vector3.Normalize(point - new System.Numerics.Vector3(halfIpd, 0f, 0f));
        }
        else if (direction.LengthSquared() > 1e-6f)
        {
            combined = left = right = System.Numerics.Vector3.Normalize(direction);
        }
        else
            return;

        head.HasEyeTracking = true;
        head.CombinedEyeGazeDirection = combined;
        head.LeftEyeGazeDirection = left;
        head.RightEyeGazeDirection = right;
        head.LeftEyeOpenness = leftOpen;
        head.RightEyeOpenness = rightOpen;
        head.LeftEyeWiden = Get(w, "UpperLidRaiserL");
        head.RightEyeWiden = Get(w, "UpperLidRaiserR");
        head.LeftEyeSqueeze = Get(w, "LidTightenerL");
        head.RightEyeSqueeze = Get(w, "LidTightenerR");
        head.LeftEyeFrown = Get(w, "BrowLowererL");
        head.RightEyeFrown = Get(w, "BrowLowererR");
    }

    // The face-tracking expression weights onto the device-agnostic mouth shapes.
    private static void WriteMouth(MouthDevice mouth, Dictionary<string, float> w)
    {
        mouth.IsTracking = true;
        float lipsToward = Get(w, "LipsToward");
        mouth.SetWeight(MouthShape.JawOpen, Clamp01(Get(w, "JawDrop") - lipsToward));
        mouth.SetWeight(MouthShape.JawLeft, Get(w, "JawSidewaysLeft"));
        mouth.SetWeight(MouthShape.JawRight, Get(w, "JawSidewaysRight"));
        mouth.SetWeight(MouthShape.JawForward, Get(w, "JawThrust"));
        mouth.SetWeight(MouthShape.MouthPucker, Avg(w, "LipPuckerL", "LipPuckerR"));
        mouth.SetWeight(MouthShape.MouthWide, Avg(w, "LipStretcherL", "LipStretcherR"));
        mouth.SetWeight(MouthShape.SmileLeft, Get(w, "LipCornerPullerL"));
        mouth.SetWeight(MouthShape.SmileRight, Get(w, "LipCornerPullerR"));
        mouth.SetWeight(MouthShape.FrownLeft, Get(w, "LipCornerDepressorL"));
        mouth.SetWeight(MouthShape.FrownRight, Get(w, "LipCornerDepressorR"));
        mouth.SetWeight(MouthShape.UpperUpLeft, Get(w, "UpperLipRaiserL"));
        mouth.SetWeight(MouthShape.UpperUpRight, Get(w, "UpperLipRaiserR"));
        mouth.SetWeight(MouthShape.UpperUp, Avg(w, "UpperLipRaiserL", "UpperLipRaiserR"));
        mouth.SetWeight(MouthShape.LowerDownLeft, Get(w, "LowerLipDepressorL"));
        mouth.SetWeight(MouthShape.LowerDownRight, Get(w, "LowerLipDepressorR"));
        mouth.SetWeight(MouthShape.LowerDown, Avg(w, "LowerLipDepressorL", "LowerLipDepressorR"));
        mouth.SetWeight(MouthShape.CheekPuffLeft, Get(w, "CheekPuffL"));
        mouth.SetWeight(MouthShape.CheekPuffRight, Get(w, "CheekPuffR"));
        mouth.SetWeight(MouthShape.CheekPuff, Avg(w, "CheekPuffL", "CheekPuffR"));
        mouth.SetWeight(MouthShape.CheekSuck, Avg(w, "CheekSuckL", "CheekSuckR"));
        mouth.SetWeight(MouthShape.UpperLipOverturn, Avg(w, "LipFunnelerLT", "LipFunnelerRT"));
        mouth.SetWeight(MouthShape.LowerLipOverturn, Avg(w, "LipFunnelerLB", "LipFunnelerRB"));
        mouth.SetWeight(MouthShape.UpperLipInside, Avg(w, "LipSuckLT", "LipSuckRT"));
        mouth.SetWeight(MouthShape.LowerLipInside, Avg(w, "LipSuckLB", "LipSuckRB"));
        mouth.SetWeight(MouthShape.UpperLipLeft, Get(w, "MouthLeft"));
        mouth.SetWeight(MouthShape.UpperLipRight, Get(w, "MouthRight"));
        mouth.SetWeight(MouthShape.LowerLipLeft, Get(w, "MouthLeft"));
        mouth.SetWeight(MouthShape.LowerLipRight, Get(w, "MouthRight"));
        float tongue = Get(w, "TongueOut");
        mouth.SetWeight(MouthShape.TongueOut, tongue);
        mouth.SetWeight(MouthShape.TongueExtend, tongue);
    }

    private static float Get(Dictionary<string, float> w, string name)
        => w.TryGetValue(name, out var v) ? Clamp01(v) : 0f;

    private static float Avg(Dictionary<string, float> w, string a, string b) => (Get(w, a) + Get(w, b)) * 0.5f;

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private void Start()
    {
        _started = true;
        foreach (int port in new[] { CustomPort, DefaultPort })
        {
            try
            {
                var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                _sockets.Add(socket);
                var thread = new Thread(() => Receive(socket, port))
                {
                    Name = $"Steam Link OSC {port}",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };
                thread.Start();
                LumoraLogger.Log($"SteamLinkFace: listening for Steam Link face and eye data on 127.0.0.1:{port}");
            }
            catch (SocketException ex)
            {
                LumoraLogger.Log($"SteamLinkFace: port {port} is taken by another program ({ex.SocketErrorCode}); not listening there");
            }
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        foreach (var socket in _sockets)
        {
            try { socket.Close(); } catch { }
        }
        _sockets.Clear();
        EyesLive = false;
    }

    private void Receive(UdpClient socket, int port)
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopping)
        {
            byte[] packet;
            try
            {
                packet = socket.Receive(ref from);
            }
            catch (SocketException) when (_stopping)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A sender that went away leaves a reset on Windows UDP sockets; keep listening.
                continue;
            }
            try
            {
                ReadPacket(packet, 0, packet.Length);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"SteamLinkFace: a malformed packet on port {port} was skipped ({ex.GetType().Name})");
            }
        }
    }

    // OSC: a bundle is "#bundle", an 8 byte time tag, then size-prefixed elements; a message is a padded
    // address, a padded type tag string and big-endian arguments.
    private void ReadPacket(byte[] data, int start, int length)
    {
        if (length >= 16 && data[start] == (byte)'#' && Encoding.ASCII.GetString(data, start, 7) == "#bundle")
        {
            int at = start + 16;
            int end = start + length;
            while (at + 4 <= end)
            {
                int size = ReadInt(data, at);
                at += 4;
                if (size <= 0 || at + size > end)
                    return;
                ReadPacket(data, at, size);
                at += size;
            }
            return;
        }
        ReadMessage(data, start, length);
    }

    private void ReadMessage(byte[] data, int start, int length)
    {
        int end = start + length;
        int at = start;
        string address = ReadString(data, ref at, end);
        if (address.Length == 0 || at >= end || data[at] != (byte)',')
            return;
        string tags = ReadString(data, ref at, end);
        Span<float> values = stackalloc float[4];
        int count = 0;
        for (int i = 1; i < tags.Length && count < values.Length; i++)
        {
            char tag = tags[i];
            if (tag == 'f' && at + 4 <= end)
            {
                values[count++] = BitConverter.Int32BitsToSingle(ReadInt(data, at));
                at += 4;
            }
            else if (tag == 'i' && at + 4 <= end)
            {
                values[count++] = ReadInt(data, at);
                at += 4;
            }
            else if (tag == 'T')
                values[count++] = 1f;
            else if (tag == 'F')
                values[count++] = 0f;
            else
                break;
        }
        if (count == 0)
            return;
        Apply(address, values[..count]);
    }

    private void Apply(string address, ReadOnlySpan<float> v)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (address.StartsWith("/sl/xrfb/facew/", StringComparison.Ordinal))
            {
                string name = address.Substring("/sl/xrfb/facew/".Length);
                _weights[name] = v[0];
                if (name == "EyesClosedL")
                {
                    _leftOpen = 1f - Clamp01(v[0]);
                    _lidsMs = now;
                }
                else if (name == "EyesClosedR")
                {
                    _rightOpen = 1f - Clamp01(v[0]);
                    _lidsMs = now;
                }
                else if (IsMouth(name))
                    _faceMs = now;
                return;
            }

            switch (address)
            {
                case "/sl/eyeTrackedGazePoint" when v.Length >= 3:
                    _gazePoint = new System.Numerics.Vector3(v[0], v[1], v[2]);
                    _gazePointMs = now;
                    break;
                // The eye-tracking layout's centre gaze: +Z forward, turned into this engine's -Z forward.
                case "/tracking/eye/CenterVecFull" when v.Length >= 3:
                    _gazeDirection = new System.Numerics.Vector3(v[0], v[1], -v[2]);
                    _gazeDirectionMs = now;
                    break;
                case "/tracking/eye/EyesClosedAmount":
                    _leftOpen = _rightOpen = 1f - Clamp01(v[0]);
                    _lidsMs = now;
                    break;
                // Avatar-parameter lids: 1 open, 0 shut.
                case "/avatar/parameters/LeftEyeLid":
                    _leftOpen = Clamp01(v[0]);
                    _lidsMs = now;
                    break;
                case "/avatar/parameters/RightEyeLid":
                    _rightOpen = Clamp01(v[0]);
                    _lidsMs = now;
                    break;
            }
        }
    }

    private static int ReadInt(byte[] data, int at)
        => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

    // A string runs to its terminating zero and is padded so the next field starts on a 4 byte boundary.
    private static string ReadString(byte[] data, ref int at, int end)
    {
        int begin = at;
        while (at < end && data[at] != 0)
            at++;
        string text = Encoding.ASCII.GetString(data, begin, at - begin);
        at = Math.Min(begin + ((at - begin + 4) & ~3), end);
        return text;
    }

    private static bool IsMouth(string name)
        => name.StartsWith("Jaw", StringComparison.Ordinal) || name.StartsWith("Lip", StringComparison.Ordinal)
           || name.StartsWith("Mouth", StringComparison.Ordinal) || name.StartsWith("Cheek", StringComparison.Ordinal)
           || name.StartsWith("Tongue", StringComparison.Ordinal) || name.StartsWith("Chin", StringComparison.Ordinal)
           || name.StartsWith("Dimpler", StringComparison.Ordinal) || name.StartsWith("UpperLip", StringComparison.Ordinal)
           || name.StartsWith("LowerLip", StringComparison.Ordinal);
}
