// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;

namespace Lumora.Core.Components.Audio;

// Listens to an AudioOutput and says what it is playing: band levels, RMS and peak, loudness, onsets, tempo,
// pitch and where the spectrum's weight sits. For light shows, visualisers, a mouth that moves with a song.
//
// What it reads is the mixer's copy of every block the output plays, taken at the top of each render (see
// OutputMonitorTap), never the output's source: reading a stream advances it and would take the sound away
// from the mixer. The copy is before the output's volume and position, so the readings are the sound itself
// wherever the listener stands. The analysis runs on the world thread as the blocks are drained, a few
// hundred microseconds a second of audio at the defaults.
//
// Readings are this machine's own and are not synced: every machine hears the same sound, so the readings
// on every machine agree closely without a number crossing the wire, and a script that needs one value
// for everyone reads it where the script runs. With no output, or one that is not playing, the analysis
// hears silence and every reading falls away. Band levels are amplitudes: a sine of amplitude A inside a band reads A. Loudness is in LUFS,
// minus infinity for silence. Pitch is 0 when nothing pitched is playing. -xlinka
[ComponentCategory("Audio")]
public class AudioAnalyzer : Component
{
    private const int DrainLength = 4096;

    // Longer than this between updates (the slot was inactive, the world stalled) and what waited in the
    // tap is old news: it is dropped rather than analysed as if it had just been heard.
    private const double StaleSeconds = 0.25;

    public readonly SyncRef<AudioOutput> Output;
    // Either an output or the source component that an output plays. Analysis always taps the output;
    // reading a source itself would consume its stream. Output takes precedence when both are set. -xlinka
    public readonly SyncRef<Component> Source = new();
    private Component? _sourceSeen;
    private AudioOutput? _sourceOutput;
    private double _nextSourceSearch;

    private AudioOutput? ResolveOutput()
    {
        if (Output.Target is { IsDestroyed: false } direct)
            return direct;
        var source = Source.Target;
        if (source is AudioOutput output)
            return output;
        if (source == null || source.IsDestroyed)
            return null;
        double now = World?.Time.TotalTime ?? 0;
        if (!ReferenceEquals(source, _sourceSeen) || now >= _nextSourceSearch)
        {
            _sourceSeen = source;
            _sourceOutput = null;
            _nextSourceSearch = now + 1.0;
            foreach (var candidate in source.World.RootSlot.GetComponentsInChildren<AudioOutput>())
                if (!candidate.IsDestroyed && ReferenceEquals(candidate.Source.Target, source))
                {
                    _sourceOutput = candidate;
                    break;
                }
        }
        return _sourceOutput is { IsDestroyed: false } ? _sourceOutput : null;
    }

    // Any length (mixed radix); longer resolves finer frequencies and answers more slowly.
    [Range(AudioAnalysisSettings.MinFftSize, AudioAnalysisSettings.MaxFftSize, "0", exponential: true)]
    public readonly Sync<int> FftSize;

    // Ignored by Octave and ThirdOctave, which take every standard band that fits the range.
    [Range(AudioAnalysisSettings.MinBands, AudioAnalysisSettings.MaxBands, "0")]
    public readonly Sync<int> BandCount;

    public readonly Sync<FrequencyScale> BandScale;

    public readonly Sync<WindowShape> Window;

    [Range(AudioAnalysisSettings.MinFrequencyLimit, AudioAnalysisSettings.MaxFrequencyLimit, "0", exponential: true)]
    public readonly Sync<float> MinFrequency;

    [Range(AudioAnalysisSettings.MinFrequencyLimit, AudioAnalysisSettings.MaxFrequencyLimit, "0", exponential: true)]
    public readonly Sync<float> MaxFrequency;

    // The share of a band's level kept each sixtieth of a second: 0 follows every frame, 0.99 barely moves.
    [Range(0f, AudioAnalysisSettings.MaxSmoothing, "0.00")]
    public readonly Sync<float> Smoothing;

    private readonly StereoSample[] _drain = new StereoSample[DrainLength];
    // The last stretch of what was heard, left and right averaged, for a script that wants the samples
    // themselves rather than readings of them. As long as the largest transform the analysis takes.
    private readonly float[] _recent = new float[AudioAnalysisSettings.MaxFftSize];
    private int _recentAt;
    private int _recentFilled;
    private OutputMonitorTap? _tap;
    private AudioAnalysis? _analysis;
    private long _onsetsSeen;
    private double _lastListen = double.NegativeInfinity;

    // An onset heard on this machine: a drum hit, a note starting. World thread, from this component's
    // update; the float is how far it cleared the detector's threshold, in dB.
    public event Action<AudioAnalyzer, float>? Beat;

    public AudioAnalyzer()
    {
        Output = new SyncRef<AudioOutput>(this);
        FftSize = new Sync<int>(this, AudioAnalysisSettings.DefaultFftSize);
        BandCount = new Sync<int>(this, AudioAnalysisSettings.DefaultBandCount);
        BandScale = new Sync<FrequencyScale>(this, FrequencyScale.Logarithmic);
        Window = new Sync<WindowShape>(this, WindowShape.Hann);
        MinFrequency = new Sync<float>(this, AudioAnalysisSettings.DefaultMinFrequency);
        MaxFrequency = new Sync<float>(this, AudioAnalysisSettings.DefaultMaxFrequency);
        Smoothing = new Sync<float>(this, AudioAnalysisSettings.DefaultSmoothing);
    }

    public AudioAnalysisSettings Settings => new()
    {
        FftSize = FftSize.Value,
        BandCount = BandCount.Value,
        BandScale = BandScale.Value,
        Window = Window.Value,
        MinFrequency = MinFrequency.Value,
        MaxFrequency = MaxFrequency.Value,
        Smoothing = Smoothing.Value,
    };

    // True while an output is being heard on this machine.
    public bool IsListening => _tap?.Voice != null;

    // How many bands the analysis actually has: BandCount, or the standard bands that fit for Octave and
    // ThirdOctave.
    public int ActiveBandCount => _analysis?.BandCount ?? 0;

    public float BandLevel(int band) => _analysis?.BandLevel(band) ?? 0f;

    public float BandLow(int band) => _analysis != null && (uint)band < (uint)_analysis.BandCount ? _analysis.Bands.Low(band) : 0f;

    public float BandCenter(int band) => _analysis != null && (uint)band < (uint)_analysis.BandCount ? _analysis.Bands.Center(band) : 0f;

    public float BandHigh(int band) => _analysis != null && (uint)band < (uint)_analysis.BandCount ? _analysis.Bands.High(band) : 0f;

    public int CopyBandLevels(Span<float> destination)
    {
        var analysis = _analysis;
        if (analysis == null)
            return 0;
        int n = System.Math.Min(destination.Length, analysis.BandCount);
        analysis.BandLevels.Slice(0, n).CopyTo(destination);
        return n;
    }

    public float Rms => _analysis?.Rms ?? 0f;

    public float Peak => _analysis?.Peak ?? 0f;

    public float MomentaryLoudness => _analysis?.Momentary ?? float.NegativeInfinity;

    public float ShortTermLoudness => _analysis?.ShortTerm ?? float.NegativeInfinity;

    public float IntegratedLoudness => _analysis?.Integrated ?? float.NegativeInfinity;

    public float Tempo => _analysis?.Tempo ?? 0f;

    public float TempoConfidence => _analysis?.TempoConfidence ?? 0f;

    public float Pitch => _analysis?.Pitch ?? 0f;

    public float PitchConfidence => _analysis?.PitchConfidence ?? 0f;

    public float SpectralCentroid => _analysis?.Centroid ?? 0f;

    public float SpectralFlatness => _analysis?.Flatness ?? 0f;

    public float SpectralRolloff => _analysis?.Rolloff ?? 0f;

    // Samples a second this machine hears at, 0 before it has listened.
    public int SampleRate => _analysis?.SampleRate ?? 0;

    // The latest samples heard, oldest first, as many as fit and have been heard. Returns how many.
    public int CopyRecent(Span<float> destination)
    {
        int n = System.Math.Min(destination.Length, _recentFilled);
        int start = (_recentAt - n + _recent.Length) % _recent.Length;
        for (int i = 0; i < n; i++)
            destination[i] = _recent[(start + i) % _recent.Length];
        return n;
    }

    // The analysis's own spectrum, bin 0 at 0 Hz, as amplitudes: a sine of amplitude A on a bin reads A.
    public ReadOnlySpan<float> SpectrumMagnitudes => _analysis != null ? _analysis.Spectrum.Magnitudes : ReadOnlySpan<float>.Empty;

    // Hertz between neighbouring spectrum bins, 0 before it has listened.
    public float SpectrumBinWidth => _analysis?.Spectrum.BinWidth ?? 0f;

    public int CopySpectrumDecibels(Span<float> destination)
    {
        var analysis = _analysis;
        if (analysis == null)
            return 0;
        int n = System.Math.Min(destination.Length, analysis.Spectrum.BinCount);
        analysis.Spectrum.CopyDecibels(destination.Slice(0, n));
        return n;
    }

    public long BeatCount { get; private set; }

    public float LastBeatStrength { get; private set; }

    // World time of the last onset, negative infinity until one is heard.
    public double LastBeatTime { get; private set; } = double.NegativeInfinity;

    // Samples the analysis missed because this machine's world thread fell a whole ring behind the mixer.
    public long DroppedSamples => _tap?.Dropped ?? 0;

    public void ResetIntegratedLoudness() => _analysis?.ResetIntegrated();

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (!IsDestroyed)
            Listen();
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        StopListening();
    }

    public override void OnDestroy()
    {
        StopListening();
        base.OnDestroy();
    }

    private void Listen()
    {
        var simulator = Engine.Current?.AudioSystem?.Simulator;
        if (simulator == null)
        {
            StopListening();
            return;
        }
        if (_tap == null || !ReferenceEquals(_tap.Simulator, simulator))
        {
            _tap?.Dispose();
            _tap = new OutputMonitorTap(simulator);
        }

        var settings = Settings.Clamped();
        if (_analysis == null || _analysis.SampleRate != simulator.SampleRate || _analysis.Settings != settings)
        {
            _analysis = new AudioAnalysis(simulator.SampleRate, settings);
            _onsetsSeen = 0;
        }

        double now = World?.Time.TotalTime ?? 0.0;
        if (now - _lastListen > StaleSeconds)
            _tap.Skip();
        _lastListen = now;

        var output = ResolveOutput();
        var voice = output != null && !output.IsDestroyed ? output.Voice : null;
        _tap.Voice = voice;

        if (voice == null)
        {
            // Nothing to hear: silence at the world's own pace, so the readings fall away instead of freezing.
            _tap.Skip();
            float delta = World?.Time.Delta ?? 0f;
            int quiet = System.Math.Min(DrainLength, (int)(delta * simulator.SampleRate));
            if (quiet > 0)
            {
                _drain.AsSpan(0, quiet).Clear();
                _analysis.Write(_drain.AsSpan(0, quiet));
                Remember(_drain.AsSpan(0, quiet));
            }
        }
        else
        {
            int budget = _tap.Capacity;
            int read;
            while (budget > 0 && (read = _tap.Read(_drain)) > 0)
            {
                _analysis.Write(_drain.AsSpan(0, read));
                Remember(_drain.AsSpan(0, read));
                budget -= read;
            }
        }

        long onsets = _analysis.Onsets;
        while (_onsetsSeen < onsets)
        {
            _onsetsSeen++;
            BeatCount++;
            LastBeatStrength = _analysis.LastOnsetStrength;
            LastBeatTime = World?.Time.TotalTime ?? 0.0;
            try
            {
                Beat?.Invoke(this, LastBeatStrength);
            }
            catch (Exception ex)
            {
                Logging.Logger.Error($"Audio Analyzer: a beat handler threw: {ex.Message}");
            }
        }
    }

    private void Remember(ReadOnlySpan<StereoSample> heard)
    {
        int size = _recent.Length;
        foreach (var sample in heard)
        {
            _recent[_recentAt] = (sample.left + sample.right) * 0.5f;
            _recentAt = (_recentAt + 1) % size;
        }
        _recentFilled = System.Math.Min(size, _recentFilled + heard.Length);
    }

    private void StopListening()
    {
        _tap?.Dispose();
        _tap = null;
    }
}
