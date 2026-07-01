namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public readonly record struct UserId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SubscriptionId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct IdempotencyKey(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct CorrelationId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct AuditReference(string Value)
{
    public override string ToString() => Value;
}
