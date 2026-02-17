using System;
using System.Collections.Generic;
using Godot;

namespace Aquamarine.Godot.Debug;

#nullable enable

/// <summary>
/// Lightweight custom graph for visualizing memory history samples.
/// Draws line traces for committed, GC, estimated component memory and video memory.
/// </summary>
public partial class MemoryHistoryGraph : Control
{
    private readonly struct MemoryPoint
    {
        public readonly long Committed;
        public readonly long Gc;
        public readonly long Estimated;
        public readonly long Video;

        public MemoryPoint(long committed, long gc, long estimated, long video)
        {
            Committed = committed;
            Gc = gc;
            Estimated = estimated;
            Video = video;
        }
    }

    private readonly List<MemoryPoint> _samples = new();

    [Export] public int MaxSamples { get; set; } = 180;
    [Export] public Color GraphBackgroundColor { get; set; } = new(0.045f, 0.045f, 0.06f, 1f);
    [Export] public Color GridColor { get; set; } = new(0.2f, 0.2f, 0.28f, 0.35f);
    [Export] public Color CommittedColor { get; set; } = new(0.95f, 0.45f, 0.95f, 1f);
    [Export] public Color GcColor { get; set; } = new(0.4f, 0.9f, 0.5f, 1f);
    [Export] public Color EstimatedColor { get; set; } = new(1f, 0.7f, 0.4f, 1f);
    [Export] public Color VideoColor { get; set; } = new(0.4f, 0.8f, 1f, 1f);

    public override void _Ready()
    {
        ClipContents = true;
        CustomMinimumSize = new Vector2(0, 140);
    }

    public void AddSample(long committedBytes, long gcBytes, long estimatedBytes, long videoBytes)
    {
        _samples.Add(new MemoryPoint(committedBytes, gcBytes, estimatedBytes, videoBytes));

        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }

        QueueRedraw();
    }

    public void ClearSamples()
    {
        _samples.Clear();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        DrawRect(rect, GraphBackgroundColor, true);

        if (_samples.Count == 0 || Size.X < 8f || Size.Y < 8f)
        {
            return;
        }

        const float padLeft = 8f;
        const float padTop = 8f;
        const float padRight = 8f;
        const float padBottom = 8f;

        var graphRect = new Rect2(
            padLeft,
            padTop,
            Mathf.Max(1f, Size.X - (padLeft + padRight)),
            Mathf.Max(1f, Size.Y - (padTop + padBottom)));

        // Grid lines
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            float y = graphRect.Position.Y + graphRect.Size.Y * t;
            DrawLine(
                new Vector2(graphRect.Position.X, y),
                new Vector2(graphRect.End.X, y),
                GridColor,
                1f);
        }

        long maxBytes = 1;
        foreach (var sample in _samples)
        {
            maxBytes = Math.Max(maxBytes, sample.Committed);
            maxBytes = Math.Max(maxBytes, sample.Gc);
            maxBytes = Math.Max(maxBytes, sample.Estimated);
            maxBytes = Math.Max(maxBytes, sample.Video);
        }

        DrawSeries(graphRect, maxBytes, CommittedColor, 2f, s => s.Committed);
        DrawSeries(graphRect, maxBytes, GcColor, 2f, s => s.Gc);
        DrawSeries(graphRect, maxBytes, EstimatedColor, 2f, s => s.Estimated);
        DrawSeries(graphRect, maxBytes, VideoColor, 2f, s => s.Video);
    }

    private void DrawSeries(Rect2 graphRect, long maxBytes, Color color, float width, Func<MemoryPoint, long> selector)
    {
        if (_samples.Count < 2)
        {
            return;
        }

        var points = new Vector2[_samples.Count];
        float xStep = _samples.Count > 1 ? graphRect.Size.X / (_samples.Count - 1) : 0f;

        for (int i = 0; i < _samples.Count; i++)
        {
            long value = selector(_samples[i]);
            float normalized = Mathf.Clamp((float)value / maxBytes, 0f, 1f);
            float x = graphRect.Position.X + xStep * i;
            float y = graphRect.End.Y - normalized * graphRect.Size.Y;
            points[i] = new Vector2(x, y);
        }

        DrawPolyline(points, color, width, true);
    }
}
