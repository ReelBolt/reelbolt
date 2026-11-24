namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// Running totals (this process, since start) of webhooks rejected by signature verification and webhooks whose
/// processing failed. The counters in <see cref="BillingWebhookDiagnostics"/> only reach a metrics backend; these are
/// what the sweep and the daily report read to push an alert (QA-2). <see cref="BillingWebhookDiagnostics.Totals"/> is
/// the process's instance; a reader compares against the totals it saw last, so a reset never happens.
/// </summary>
public sealed class BillingWebhookFailureTotals
{
    private long _signatureFailures;
    private long _processingFailures;

    /// <summary>Webhooks rejected by signature verification so far.</summary>
    public long SignatureFailures => Interlocked.Read(ref _signatureFailures);

    /// <summary>Webhooks whose processing threw so far.</summary>
    public long ProcessingFailures => Interlocked.Read(ref _processingFailures);

    /// <summary>Counts one webhook rejected by signature verification.</summary>
    public void RecordSignatureFailure() => Interlocked.Increment(ref _signatureFailures);

    /// <summary>Counts one webhook whose processing threw.</summary>
    public void RecordProcessingFailure() => Interlocked.Increment(ref _processingFailures);
}
