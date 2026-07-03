namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Administration surface for provisioning subscriptions and making audited manual
/// changes. Every state change performed here is expressed as an append-only audit
/// record visible in the audit trail and reconciliation report.
/// </summary>
public interface ISubscriptionCreditAdministrationService
{
    /// <summary>
    /// Provision a subscription with window limits and an initial extra pool balance.
    /// Writes an extra pool seed audit record so reconciliation can reconstruct the
    /// pool balance from the beginning of the subscription's life.
    /// </summary>
    Task CreateSubscriptionAsync(SubscriptionDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Enable or disable a subscription, leaving a status change audit record.</summary>
    Task SetSubscriptionEnabledAsync(
        string subscriptionId,
        bool enabled,
        string actor,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply a manual extra pool change (spec 4.4, TC-EXTRA-003). Throws
    /// <see cref="InvalidOperationException"/> when the subscription does not exist or
    /// the resulting balance would be negative; the balance is unchanged in that case.
    /// Returns the audit record expressing the change.
    /// </summary>
    Task<AuditRecord> AdjustExtraPoolAsync(ExtraPoolAdjustment adjustment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a manual accounting correction (spec section 6, TC-AUDIT-003) as a new
    /// audit record. The original record remains queryable and unchanged.
    /// </summary>
    Task<AuditRecord> RecordManualCorrectionAsync(ManualCorrection correction, CancellationToken cancellationToken = default);
}
