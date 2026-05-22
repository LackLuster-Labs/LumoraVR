using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Concentus;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams.Audio;

public class OpusStream : Stream, IAudioStream
{

    //NOTE: the current effective incoming buffer size should be 40 ms will make it ajustable later
    #region Stearm Stuff
    public override bool HasValidData => false;

    public override uint Period => 0;

    public override uint Phase => 0;
    public override void Encode(System.IO.BinaryWriter writer)
    {
    }

    public override void Decode(System.IO.BinaryReader reader, StreamMessage message)
    {
    }
    public override bool IsExplicitUpdatePoint(ulong timePoint) => false;
    #endregion
    
    public delegate bool TryRequestData(out float2[] pcm);

    public TryRequestData? DataRequested;
    private object? _underlying;
    public OpusStream(object underlying)
    {
        _underlying = underlying;
    }
    public OpusStream() { }
    private struct Packet
    {
        private Packet(ushort seq)
        {
            new Packet(seq, null);
        }
        private Packet(ushort seq, byte[] data)
        {
            _sequence = seq;
            _data = data;
        }
        ushort _sequence;
        public ushort Sequence => _sequence;
        byte[] _data;
        public static implicit operator byte[](Packet p) => p._data;
        public static implicit operator ReadOnlySpan<byte>(Packet p) => p._data.AsSpan();
        public static implicit operator Packet((ushort seq, byte[] d) i) => new Packet(i.seq, i.d);

    }
    protected override void OnInit()
    {
        base.OnInit();
        if (IsLocal)
        {
            opusEncoder = OpusCodecFactory.CreateEncoder(_sampleRate, _channelCount,Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
            OutputBuffer = new byte[NetworkLimits.MaxRawFrameBytes];
        }
        else
        {
            jitterBuffer[0] = (0, Array.Empty<byte>());
            dummyBuffer = new float[_channelCount + framesize];
            opusDecoder = OpusCodecFactory.CreateDecoder(_sampleRate, _channelCount);
            packetQueue = new();
        }
        _pollingrate = PollingRate.Value;
        Task.Delay(200).ContinueWith((_) =>
        World?.Session?.RawStreamManager?.StartPolling(this));
        PollingRate.OnChanged += (newrate) =>
        {
            ChangePollingRate(newrate);
        };
    }
    private void ChangePollingRate(uint rate)
    {
        World?.Session?.RawStreamManager?.StopPolling(this);
        _pollingrate = rate;
        World?.Session?.RawStreamManager?.StartPolling(this);
    }
    private ConcurrentQueue<Packet> packetQueue = null!;
    private Packet[] jitterBuffer = new Packet[_bufferLimit];
    private byte[] OutputBuffer;
    [HideInInspector]
    public readonly Sync<int> framesize = new(480);
    [HideInInspector]
    public readonly Sync<uint> PollingRate = new(10);
    private float[] dummyBuffer;
    private IOpusDecoder opusDecoder = null!;
    private IOpusEncoder opusEncoder = null!;
    private ushort _sequenceOut = 0;
    private uint _pollingrate;
    uint IRawStream.PollingRate => _pollingrate;
    #region Sample rate
    // static for now but other stream types will need this
    public int SampleRate => _sampleRate;
    private readonly int _sampleRate = 48000;
    #endregion
    #region Channel Count 
    public int ChannelConunt => _channelCount;
    private int _channelCount = 1;
    #endregion
    private int _currentIndex;
    private static readonly int _bufferLimit = 4;
    public event PCMCallback? OnNewData;
    private int currentIndex = 0;

    public void EnqueueRawFrame(ushort sequence, ReadOnlyMemory<byte> payload)
    {
        if (packetQueue.Count > _bufferLimit) packetQueue.TryDequeue(out _);
        packetQueue.Enqueue((sequence, payload.ToArray()));
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
        if (!IsLocal)
        {
            if (OnNewData is null) return;
            int available = 0;
            int lastSequence = jitterBuffer[_currentIndex].Sequence;
            {
                while (packetQueue.TryDequeue(out var pkt))
                {
                    if (pkt.Sequence <= lastSequence) continue;
                    int upbylength = System.Math.DivRem(pkt.Sequence - lastSequence, _bufferLimit, out var currentIndex);
                    if (upbylength > 5) opusDecoder.ResetState(); //if we skiped more then 3*4 120ms  
                    else
                        for (int i = 0; i < upbylength * _bufferLimit; i++)
                            opusDecoder.Decode(null, dummyBuffer, framesize, false);
                    jitterBuffer[currentIndex] = pkt;
                    if (upbylength > 0) _currentIndex = currentIndex;
                    available = _currentIndex - currentIndex + 1;
                }
            }
            if (available > 0)
            {
                int outsize = opusDecoder.Decode(jitterBuffer[_currentIndex], dummyBuffer, framesize, true);
                OnNewData(MemoryMarshal.Cast<float, float2>(dummyBuffer.AsSpan(outsize)));
            }
        }else
        {
            if(DataRequested?.Invoke(out var pcm) ?? false)
            {
                
                int truelength = opusEncoder.Encode(MemoryMarshal.Cast<float2,float>(pcm),framesize,OutputBuffer,OutputBuffer.Length);
                Owner.World.Session.SendRawFrame(this,++_sequenceOut,OutputBuffer.AsSpan(truelength));
            }
        }

    }

    public object? GetPassthrough() => _underlying;
}