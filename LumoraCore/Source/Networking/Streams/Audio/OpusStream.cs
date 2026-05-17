using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using Lumora.Core.Math;
using Lumora.Core.Networking.Streams;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams.Audio;

public class OpusStream : Stream, IAudioStream
{
    private struct packet
    {
        private packet(ushort seq, byte[] data)
        {
            _sequence = seq;
            _data = data;
        }
        ushort _sequence;
        byte[] _data;
        public static implicit operator byte[](packet p) => p._data;
        public static implicit operator packet((ushort seq, byte[] d) i) => new packet(i.seq, i.d);

    }
    protected override void OnInit()
    {
        base.OnInit();
        if (this.IsLocal)
        {
            opusEncoder = OpusCodecFactory.CreateEncoder(48000, 2);
        }
        else
        {
            opusDecoder = OpusCodecFactory.CreateDecoder(48000, 2);
            packetQueue = new();
        }
        _pollingrate = PollingRate.Value;
        World.Session.RawStreamManager.StartPolling(this);
        PollingRate.OnChanged += (newrate) =>
        {
            ChangeSampleRate(newrate);
        };
    }
    private void ChangeSampleRate(uint rate)
    {
        World.Session.RawStreamManager.StopPolling(this);
        _pollingrate = rate;
        World.Session.RawStreamManager.StartPolling(this);
    }
    private ConcurrentQueue<packet> packetQueue = null!;
    public readonly Sync<int> framesize = new(480);
    public readonly Sync<uint> PollingRate = new(20);
    private IOpusDecoder opusDecoder = null!;
    private IOpusEncoder opusEncoder = null!;
    private ushort _sequenceIn = 0;
    private ushort _sequenceOut = 0;
    public override bool HasValidData => false;

    public override uint Period => 0;

    public override uint Phase => 0;
    private uint _pollingrate;
    uint IRawStream.PollingRate => _pollingrate;
    public override bool IsExplicitUpdatePoint(ulong timePoint) => false;

    public void EnqueueRawFrame(ushort sequence, ReadOnlyMemory<byte> payload)
    {
        packetQueue.Enqueue((sequence, payload.ToArray()));
    }

    public override void Encode(System.IO.BinaryWriter writer)
    {
    }

    public override void Decode(System.IO.BinaryReader reader, StreamMessage message)
    {
    }

    public int GetFramesAvailable()
    {
        throw new NotImplementedException();
    }

    public float2[]? GetFrames(int count)
    {
        throw new NotImplementedException();
    }

    public async Task Poll()
    {
    }
}