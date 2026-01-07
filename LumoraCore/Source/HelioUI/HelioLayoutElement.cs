using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Optional sizing hints used by Helio layout groups.
/// Provides minimum, preferred, and flexible sizing information.
/// </summary>
public class HelioLayoutElement : Component
{
    public Sync<float2> MinSize { get; private set; }
    public Sync<float2> PreferredSize { get; private set; }
    public Sync<float2> FlexibleSize { get; private set; }
    public Sync<bool> IgnoreLayout { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        MinSize = new Sync<float2>(this, float2.Zero);
        PreferredSize = new Sync<float2>(this, new float2(64f, 32f));
        FlexibleSize = new Sync<float2>(this, new float2(1f, 1f));
        IgnoreLayout = new Sync<bool>(this, false);
    }

    internal LayoutMetrics GetMetrics(float2 fallbackSize)
    {
        return new LayoutMetrics
        {
            Min = MinSize.Value,
            Preferred = PreferredSize.Value == float2.Zero ? fallbackSize : PreferredSize.Value,
            Flexible = FlexibleSize.Value
        };
    }
}

public struct LayoutMetrics
{
    public float2 Min;
    public float2 Preferred;
    public float2 Flexible;
}
