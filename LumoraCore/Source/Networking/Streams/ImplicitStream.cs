namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Base class for streams that use periodic (implicit) updates.
/// Implicit streams send data at regular intervals based on Period and Phase.
/// </summary>
public abstract class ImplicitStream : Stream
{
    protected readonly Sync<uint> _period = new();
    protected readonly Sync<uint> _phase = new();

    private bool _forceUpdatePoint;

    /// <summary>
    /// Update period in sync ticks.
    /// </summary>
    public override uint Period => _period.Value;

    /// <summary>
    /// Phase offset for updates.
    /// </summary>
    public override uint Phase => _phase.Value;

    /// <summary>
    /// Force an explicit update on next check.
    /// </summary>
    public void ForceUpdate()
    {
        CheckOwnership();
        _forceUpdatePoint = true;
    }

    /// <summary>
    /// Check if this stream has explicit data to send.
    /// For implicit streams, this only returns true if ForceUpdate was called.
    /// </summary>
    public override bool IsExplicitUpdatePoint(ulong timePoint)
    {
        bool forceUpdatePoint = _forceUpdatePoint;
        _forceUpdatePoint = false;

        // If it's an implicit update point, don't also do explicit
        if (IsImplicitUpdatePoint(timePoint))
            return false;

        return forceUpdatePoint;
    }

    /// <summary>
    /// Set the update period and phase.
    /// </summary>
    public void SetUpdatePeriod(uint period, uint phase)
    {
        CheckOwnership();
        _period.Value = period;
        _phase.Value = phase;
    }

    protected override void OnInit()
    {
        base.OnInit();

        // Initialize period/phase sync members
        _period.Initialize(World, this);
        _phase.Initialize(World, this);
        _period.EndInitPhase();
        _phase.EndInitPhase();
    }
}
