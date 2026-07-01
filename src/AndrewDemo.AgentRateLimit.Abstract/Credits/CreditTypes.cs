using System.Globalization;

namespace AndrewDemo.AgentRateLimit.Abstract.Credits;

public readonly record struct CreditAmount(int Value)
{
    public static CreditAmount Zero { get; } = new(0);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct CreditDelta(int Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record RequestedCreditsInput(string RawValue)
{
    public static RequestedCreditsInput FromInt32(int value) =>
        new(value.ToString(CultureInfo.InvariantCulture));
}
