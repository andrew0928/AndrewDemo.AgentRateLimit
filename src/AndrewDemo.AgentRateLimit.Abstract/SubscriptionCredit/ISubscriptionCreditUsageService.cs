namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// External usage surface of the subscription credit rate limit, per spec section 3.
/// Concurrent calls against the same subscription must observe results equivalent to
/// some serial order, and accepted credits must never exceed the credits coverable by
/// the subscription allowance plus the extra pool (spec section 6).
/// </summary>
public interface ISubscriptionCreditUsageService
{
    /// <summary>
    /// Consume usage (spec 3.1). Always returns a decision; accepted, rejected,
    /// conflict and invalid decisions all leave a queryable audit record.
    /// </summary>
    Task<UsageDecision> ConsumeAsync(UsageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preview usage (spec 3.2): evaluates the same decision pipeline as
    /// <see cref="ConsumeAsync"/> against current state without changing usage totals,
    /// window usage, the extra pool balance, idempotency bindings or reconciliation
    /// results. Preview decisions carry no audit reference.
    /// </summary>
    Task<UsageDecision> PreviewAsync(UsageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query current usage status of a subscription (spec 3.3).
    /// Returns <c>null</c> when the subscription does not exist.
    /// </summary>
    Task<SubscriptionUsageStatus?> GetUsageStatusAsync(string subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query the audit trail of a user or subscription (spec 3.4), oldest first.
    /// </summary>
    Task<IReadOnlyList<AuditRecord>> QueryAuditTrailAsync(AuditTrailQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Export a reconciliation report for a half-open period
    /// <c>[fromInclusive, toExclusive)</c> (spec 3.5).
    /// </summary>
    Task<ReconciliationReport> ExportReconciliationReportAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken = default);
}
