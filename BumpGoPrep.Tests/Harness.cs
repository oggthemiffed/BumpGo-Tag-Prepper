using System.Diagnostics;
using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>Runs a service against a fake reader and records what it reports.</summary>
internal sealed class Harness : IDisposable
{
    public const string ReaderName = "TEST READER";

    private readonly BumpGoTagPrepperService _service;
    private readonly object _gate = new();
    private readonly List<string?> _detected = new();
    private readonly List<string?> _removed = new();
    private readonly List<bool> _prepped = new();
    private readonly List<string> _steps = new();
    private readonly List<string> _logs = new();
    private readonly List<TagImage> _images = new();

    private Harness(Func<string, ITagTransport?> connect)
    {
        _service = new BumpGoTagPrepperService(connect);

        _service.TagDetected += (_, e) => Record(_detected, e.Uid);
        _service.TagRemoved += (_, e) => Record(_removed, e.Uid);
        _service.TagPrepped += (_, e) => Record(_prepped, e.Success);
        _service.TagRead += (_, e) => Record(_images, e.Image);
        _service.Log += (_, e) => Record(_logs, e.Message);
        _service.StepCompleted += (_, e) =>
            Record(_steps, $"{e.StepLabel}: {(e.Success ? "OK" : "FAILED")}");
    }

    public static Harness Watching(Func<string, ITagTransport?> connect,
        Action<BumpGoTagPrepperService>? configure = null)
    {
        var harness = new Harness(connect);
        configure?.Invoke(harness._service);
        harness._service.StartWatching(ReaderName);
        return harness;
    }

    /// <summary>Watches a single tag that stays on the reader until the first result is in.</summary>
    public static Harness Processing(ITagTransport tag, Action<BumpGoTagPrepperService>? configure = null)
    {
        var harness = Watching(_ => tag, configure);
        if (!harness.WaitFor(() => harness.PrepResults().Count >= 1))
            throw new TimeoutException("The tag was never processed.");
        return harness;
    }

    public int PreppedCount => _service.PreppedCount;

    public IReadOnlyList<string?> Detected() => Snapshot(_detected);
    public IReadOnlyList<string?> Removed() => Snapshot(_removed);
    public IReadOnlyList<bool> PrepResults() => Snapshot(_prepped);
    public IReadOnlyList<string> Steps() => Snapshot(_steps);
    public IReadOnlyList<string> Logs() => Snapshot(_logs);
    public IReadOnlyList<TagImage> Images() => Snapshot(_images);

    /// <summary>Waits for at least <paramref name="count"/> successful preps.</summary>
    public bool WaitForPrepCount(int count) =>
        WaitFor(() => PrepResults().Count(success => success) >= count);

    public bool WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    private void Record<T>(List<T> into, T value)
    {
        lock (_gate) into.Add(value);
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> from)
    {
        lock (_gate) return from.ToArray();
    }

    public void Dispose() => _service.Dispose();
}
