namespace HighPerf.Api;

internal sealed class ComputeCounter
{
    private long _value;
    public long Increment() => Interlocked.Increment(ref _value);
}
