using PCSC;
using PCSC.Exceptions;

namespace BumpGoPrep.Core;

/// <summary>
/// Encapsulates all NFC reader / tag-prepping logic behind events, so it can
/// be driven by a console app, a WinForms app, or anything else without
/// duplicating the PC/SC or NTAG213 command handling.
///
/// Consumers subscribe to the events and call <see cref="StartWatching"/>.
/// Events are raised on a background thread — GUI consumers (e.g. WinForms)
/// must marshal back to the UI thread themselves (e.g. via Control.Invoke)
/// before touching UI controls.
/// </summary>
public sealed class BumpGoTagPrepperService : IDisposable
{
    private const int PollIntervalMs = 250;
    private const int MaxWriteAttempts = 3;
    private const int RetryDelayMs = 100;

    private readonly ISCardContext? _context;

    /// <summary>
    /// Opens a connection to the tag on the named reader, or returns null when
    /// no tag is present. Swapped out by tests to run without hardware.
    /// </summary>
    private readonly Func<string, ITagTransport?> _connect;

    private CancellationTokenSource? _cts;
    private Task? _watchTask;
    private int _preppedCount;

    /// <summary>Raised whenever a new (not-just-processed) tag is seen on the reader.</summary>
    public event EventHandler<TagDetectedEventArgs>? TagDetected;

    /// <summary>Raised when the tag is lifted off the reader.</summary>
    public event EventHandler<TagRemovedEventArgs>? TagRemoved;

    /// <summary>Raised after each individual write step (password page, PACK page).</summary>
    public event EventHandler<TagStepEventArgs>? StepCompleted;

    /// <summary>Raised once a tag has been fully processed.</summary>
    public event EventHandler<TagPreppedEventArgs>? TagPrepped;

    /// <summary>General free-text status/log messages.</summary>
    public event EventHandler<LogEventArgs>? Log;

    public int PreppedCount => _preppedCount;

    public BumpGoTagPrepperService()
    {
        _context = ContextFactory.Instance.Establish(SCardScope.System);
        _connect = OpenTag;
    }

    /// <summary>Test seam: drives the watch loop from a supplied transport instead of real hardware.</summary>
    internal BumpGoTagPrepperService(Func<string, ITagTransport?> connect)
    {
        _connect = connect;
    }

    /// <summary>Lists the names of all currently connected PC/SC readers.</summary>
    public IReadOnlyList<string> ListReaders()
    {
        if (_context is null)
            return Array.Empty<string>();

        try
        {
            var names = _context.GetReaders();
            return names ?? Array.Empty<string>();
        }
        catch (PCSCException ex)
        {
            RaiseLog($"Could not list readers: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Starts a background loop that watches the given reader and preps every
    /// new blank tag it detects. Call <see cref="StopWatching"/> to stop.
    /// </summary>
    public void StartWatching(string readerName)
    {
        if (_watchTask is { IsCompleted: false })
        {
            RaiseLog("Already watching a reader.");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _watchTask = Task.Run(() => WatchLoop(readerName, token), token);
    }

    /// <summary>Stops the background watch loop, if running.</summary>
    public void StopWatching()
    {
        _cts?.Cancel();
    }

    private void WatchLoop(string readerName, CancellationToken token)
    {
        // UID of the tag currently sitting on the reader, so we prep it once
        // rather than continuously. Cleared when the tag is lifted off, which
        // is what lets the same tag be presented again to retry it.
        string? presentUid = null;

        RaiseLog($"Watching reader: {readerName}");

        while (!token.IsCancellationRequested)
        {
            using var transport = _connect(readerName);
            var uid = transport is null ? null : ReadUid(transport);

            if (uid is null)
            {
                if (presentUid is not null)
                {
                    var removed = presentUid;
                    presentUid = null;
                    TagRemoved?.Invoke(this, new TagRemovedEventArgs(removed));
                }

                Wait(token, PollIntervalMs);
                continue;
            }

            if (uid == presentUid)
            {
                Wait(token, PollIntervalMs);
                continue;
            }

            presentUid = uid;
            TagDetected?.Invoke(this, new TagDetectedEventArgs(uid));

            WarnIfUnexpectedTagType(transport!);

            var success = PrepOneTag(transport!);
            if (success)
            {
                Interlocked.Increment(ref _preppedCount);
            }

            TagPrepped?.Invoke(this, new TagPreppedEventArgs(uid, success, _preppedCount));

            Wait(token, PollIntervalMs);
        }

        RaiseLog("Stopped watching.");
    }

    private bool PrepOneTag(ITagTransport transport)
    {
        var ok = WritePage(transport, NfcCommands.PasswordPage, NfcCommands.PasswordValue,
            "Password (page 43)");
        if (!ok) return false;

        return WritePage(transport, NfcCommands.PackPage, NfcCommands.PackValue,
            "PACK (page 44)");
    }

    private bool WritePage(ITagTransport transport, byte page, byte[] value, string label)
    {
        var apdu = NfcCommands.BuildUpdateBinary(page, value);

        // Writes are idempotent, so retry a couple of times: the tag can miss
        // an exchange if it is moved or the field is briefly disturbed.
        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            var transmitted = transport.TryTransmit(apdu, out var response);

            if (transmitted && NfcCommands.IsSuccess(response))
            {
                StepCompleted?.Invoke(this, new TagStepEventArgs(label, true, "written"));
                return true;
            }

            if (attempt < MaxWriteAttempts)
            {
                Thread.Sleep(RetryDelayMs);
                continue;
            }

            var detail = transmitted && response.Length > 0
                ? $"reader returned {BitConverter.ToString(response)}"
                : "no response from tag";
            StepCompleted?.Invoke(this, new TagStepEventArgs(label, false, detail));
            return false;
        }

        return false;
    }

    /// <summary>
    /// Logs a warning if the tag reports a memory size other than NTAG213's.
    /// Only meaningful once a tag has been NDEF-formatted; a virgin tag has an
    /// empty capability container and is left alone.
    /// </summary>
    private void WarnIfUnexpectedTagType(ITagTransport transport)
    {
        var apdu = NfcCommands.BuildReadBinary(NfcCommands.CapabilityContainerPage, 4);

        if (!transport.TryTransmit(apdu, out var response) || !NfcCommands.IsSuccess(response))
            return;

        var cc = NfcCommands.StripStatusWord(response);
        if (cc.Length < 3 || cc[0] != 0xE1)
            return;

        if (cc[2] == NfcCommands.Ntag213SizeByte)
            return;

        var guess = cc[2] switch
        {
            NfcCommands.Ntag215SizeByte => "looks like an NTAG215",
            NfcCommands.Ntag216SizeByte => "looks like an NTAG216",
            _ => "does not report NTAG213 memory size",
        };

        RaiseLog($"Warning: this tag {guess}; the BumpGo procedure targets NTAG213.");
    }

    private string? ReadUid(ITagTransport transport)
    {
        if (!transport.TryTransmit(NfcCommands.GetUid, out var response))
            return null;

        // Anything that isn't 90 00 is a reader error code (e.g. 63 00), not a
        // UID — returning it would make the watch loop treat the failure as a
        // brand new tag.
        if (!NfcCommands.IsSuccess(response))
            return null;

        var uid = NfcCommands.StripStatusWord(response);
        return uid.Length > 0 ? BitConverter.ToString(uid) : null;
    }

    private ITagTransport? OpenTag(string readerName)
    {
        var reader = new SCardReader(_context!);

        try
        {
            if (reader.Connect(readerName, SCardShareMode.Shared, SCardProtocol.Any) != SCardError.Success)
            {
                reader.Dispose();
                return null;
            }
        }
        catch (PCSCException)
        {
            reader.Dispose();
            return null;
        }

        return new ScardTransport(reader);
    }

    /// <summary>Sleeps, but wakes immediately if the loop has been cancelled.</summary>
    private static void Wait(CancellationToken token, int milliseconds) =>
        token.WaitHandle.WaitOne(milliseconds);

    private void RaiseLog(string message) => Log?.Invoke(this, new LogEventArgs(message));

    public void Dispose()
    {
        StopWatching();
        try
        {
            _watchTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore — best-effort shutdown
        }

        _context?.Dispose();
    }
}
