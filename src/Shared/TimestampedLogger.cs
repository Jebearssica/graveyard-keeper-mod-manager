using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace Shared;

internal sealed class TimestampedLogger
{
    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    private readonly ManualLogSource _source;

    internal TimestampedLogger(ManualLogSource source) => _source = source;

    private static string Prefix() =>
        $"[{DateTime.Now:HH:mm:ss.fff} | T+{ProcessClock.Elapsed.TotalSeconds:0.000}] ";

    internal void LogInfo(object data)    => _source.LogInfo(Prefix() + data);
    internal void LogWarning(object data) => _source.LogWarning(Prefix() + data);
    internal void LogError(object data)   => _source.LogError(Prefix() + data);
    internal void LogDebug(object data)   => _source.LogDebug(Prefix() + data);
}
