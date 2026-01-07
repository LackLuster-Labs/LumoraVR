namespace Lumora.Core.Networking.Messages;

/// <summary>
/// Result of validating a delta change from a client.
/// Authority uses this to decide whether to accept or reject changes.
/// </summary>
public class DeltaValidationResult
{
    public bool IsValid { get; set; }
    public string RejectionReason { get; set; }
    public object CorrectedValue { get; set; }

    public static DeltaValidationResult Accept()
    {
        return new DeltaValidationResult { IsValid = true };
    }

    public static DeltaValidationResult Reject(string reason, object correctedValue = null)
    {
        return new DeltaValidationResult
        {
            IsValid = false,
            RejectionReason = reason,
            CorrectedValue = correctedValue
        };
    }
}
