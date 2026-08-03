using System.Diagnostics;
using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>
/// Drives the watch loop against a fake tag, so the prepping behaviour is
/// exercised end to end without a reader attached.
/// </summary>
public class BumpGoTagPrepperServiceTests
{
    private const string ReaderName = "TEST READER";

    [Fact]
    public void A_healthy_tag_gets_the_password_then_the_pack()
    {
        var tag = new FakeTag(Apdu.HealthyTag());
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));

        var writes = tag.SentWithInstruction(Apdu.UpdateBinary);
        Assert.Equal(2, writes.Count);

        Assert.Equal(NfcCommands.PasswordPage, Apdu.TargetPage(writes[0]));
        Assert.Equal(new byte[] { 0x05, 0x07, 0x09, 0x03 }, Apdu.WrittenData(writes[0]));

        Assert.Equal(NfcCommands.PackPage, Apdu.TargetPage(writes[1]));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, Apdu.WrittenData(writes[1]));

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(1, harness.PreppedCount);
        Assert.Equal(new[] { "Password (page 43): OK", "PACK (page 44): OK" }, harness.Steps());
    }

    [Fact]
    public void A_tag_left_sitting_on_the_reader_is_prepped_only_once()
    {
        var tag = new FakeTag(Apdu.HealthyTag());
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));

        // Give the loop several more polls with the same tag still present.
        Thread.Sleep(1000);

        Assert.Single(harness.PrepResults());
        Assert.Equal(2, tag.SentWithInstruction(Apdu.UpdateBinary).Count);
    }

    [Fact]
    public void Lifting_the_tag_off_and_replacing_it_preps_it_again()
    {
        var present = true;
        var tag = new FakeTag(Apdu.HealthyTag());
        using var harness = Harness.Watching(_ => present ? tag : null);

        Assert.True(harness.WaitForPrepCount(1));

        present = false;
        Assert.True(harness.WaitFor(() => harness.Removed().Count == 1));

        present = true;
        Assert.True(harness.WaitForPrepCount(2));

        Assert.Equal(new[] { true, true }, harness.PrepResults());
        Assert.Equal(2, harness.PreppedCount);
    }

    [Fact]
    public void A_reader_error_code_is_not_mistaken_for_a_tag()
    {
        // The reader answers 63 00 when it cannot read a UID. Treating those
        // bytes as a UID used to make the loop "detect" a phantom tag.
        var tag = new FakeTag(apdu => apdu[1] == Apdu.GetUid ? Apdu.Failed() : Apdu.Ok());
        using var harness = Harness.Watching(_ => tag);

        Thread.Sleep(800);

        Assert.Empty(harness.Detected());
        Assert.Empty(harness.PrepResults());
        Assert.Empty(tag.SentWithInstruction(Apdu.UpdateBinary));
    }

    [Fact]
    public void A_write_that_fails_once_is_retried_and_can_still_succeed()
    {
        var attempts = 0;
        var tag = new FakeTag(apdu =>
        {
            if (apdu[1] != Apdu.UpdateBinary) return Apdu.HealthyTag()(apdu);
            return Interlocked.Increment(ref attempts) == 1 ? null : Apdu.Ok();
        });

        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(3, tag.SentWithInstruction(Apdu.UpdateBinary).Count); // failed, retried, then the pack
    }

    [Fact]
    public void The_pack_page_is_left_alone_when_the_password_write_fails()
    {
        var tag = new FakeTag(apdu => apdu[1] == Apdu.UpdateBinary ? Apdu.Failed() : Apdu.HealthyTag()(apdu));
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitFor(() => harness.PrepResults().Count == 1));

        Assert.Equal(new[] { false }, harness.PrepResults());
        Assert.Equal(0, harness.PreppedCount);

        var writes = tag.SentWithInstruction(Apdu.UpdateBinary);
        Assert.All(writes, w => Assert.Equal(NfcCommands.PasswordPage, Apdu.TargetPage(w)));
        Assert.DoesNotContain(writes, w => Apdu.TargetPage(w) == NfcCommands.PackPage);
    }

    [Fact]
    public void A_tag_that_failed_can_be_retried_by_lifting_it_off_and_back_on()
    {
        var present = true;
        var healthy = false;
        var tag = new FakeTag(apdu =>
            apdu[1] == Apdu.UpdateBinary && !healthy ? Apdu.Failed() : Apdu.HealthyTag()(apdu));

        using var harness = Harness.Watching(_ => present ? tag : null);

        Assert.True(harness.WaitFor(() => harness.PrepResults().Count == 1));
        Assert.Equal(new[] { false }, harness.PrepResults());

        present = false;
        Assert.True(harness.WaitFor(() => harness.Removed().Count == 1));

        healthy = true;
        present = true;

        Assert.True(harness.WaitForPrepCount(1));
        Assert.Equal(new[] { false, true }, harness.PrepResults());
    }

    [Fact]
    public void Warns_when_the_tag_reports_a_different_memory_size()
    {
        var tag = new FakeTag(Apdu.HealthyTag(capabilityContainer: Apdu.Ntag215Cc));
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));

        Assert.Contains(harness.Logs(), m => m.Contains("NTAG215"));
    }

    [Fact]
    public void Says_nothing_about_an_unformatted_tag()
    {
        // A virgin tag has an empty capability container; that is normal for a
        // blank sticker and must not produce a scary warning.
        var tag = new FakeTag(Apdu.HealthyTag(capabilityContainer: Apdu.VirginCc));
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));

        Assert.DoesNotContain(harness.Logs(), m => m.Contains("Warning"));
    }

    [Fact]
    public void The_connection_is_released_after_every_poll()
    {
        var tag = new FakeTag(Apdu.HealthyTag());
        using var harness = Harness.Watching(_ => tag);

        Assert.True(harness.WaitForPrepCount(1));
        Assert.True(harness.WaitFor(() => tag.DisposeCount >= 2));
    }

    /// <summary>Runs a service against a fake reader and records what it reports.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly BumpGoTagPrepperService _service;
        private readonly object _gate = new();
        private readonly List<string?> _detected = new();
        private readonly List<string?> _removed = new();
        private readonly List<bool> _prepped = new();
        private readonly List<string> _steps = new();
        private readonly List<string> _logs = new();

        private Harness(Func<string, ITagTransport?> connect)
        {
            _service = new BumpGoTagPrepperService(connect);

            _service.TagDetected += (_, e) => Record(_detected, e.Uid);
            _service.TagRemoved += (_, e) => Record(_removed, e.Uid);
            _service.TagPrepped += (_, e) => Record(_prepped, e.Success);
            _service.Log += (_, e) => Record(_logs, e.Message);
            _service.StepCompleted += (_, e) =>
                Record(_steps, $"{e.StepLabel}: {(e.Success ? "OK" : "FAILED")}");
        }

        public static Harness Watching(Func<string, ITagTransport?> connect)
        {
            var harness = new Harness(connect);
            harness._service.StartWatching(ReaderName);
            return harness;
        }

        public int PreppedCount => _service.PreppedCount;

        public IReadOnlyList<string?> Detected() => Snapshot(_detected);
        public IReadOnlyList<string?> Removed() => Snapshot(_removed);
        public IReadOnlyList<bool> PrepResults() => Snapshot(_prepped);
        public IReadOnlyList<string> Steps() => Snapshot(_steps);
        public IReadOnlyList<string> Logs() => Snapshot(_logs);

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
}
