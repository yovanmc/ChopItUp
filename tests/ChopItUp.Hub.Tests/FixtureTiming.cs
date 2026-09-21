using System.Diagnostics;
using System.Text.Json;

namespace ChopItUp.Hub.Tests;

/// <summary>Opt-in fixture phase timings. Each test process owns one JSONL file.</summary>
internal static class FixtureTiming
{
    private static readonly object Gate = new();
    public static IDisposable Measure(string phase)
    {
        var directory = Environment.GetEnvironmentVariable("TEST_FIXTURE_TIMINGS");
        return string.IsNullOrWhiteSpace(directory) ? Empty.Instance : new Sample(directory, phase);
    }

    private sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();
        public void Dispose() { }
    }

    private sealed class Sample(string directory, string phase) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var line = JsonSerializer.Serialize(new { phase, milliseconds = Stopwatch.GetElapsedTime(_start).TotalMilliseconds });
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"fixture-{Environment.ProcessId}.jsonl"), line + Environment.NewLine);
            }
        }
    }
}
