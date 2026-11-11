using CasePilot.Core.Abstractions;

namespace CasePilot.Platform;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SystemDelay : IDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

public sealed class GuidIds : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}
