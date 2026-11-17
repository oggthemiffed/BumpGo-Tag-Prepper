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

    /// <summary>Raised in <see cref="TagMode.Dump"/> with everything read off the tag.</summary>
    public event EventHandler<TagReadEventArgs>? TagRead;

    /// <summary>General free-text status/log messages.</summary>
    public event EventHandler<LogEventArgs>? Log;

    /// <summary>Number of tags successfully processed in the current mode.</summary>
    public int PreppedCount => _preppedCount;

    /// <summary>What to do with each tag. Set before <see cref="StartWatching"/>.</summary>
    public TagMode Mode { get; set; } = TagMode.Prep;

    /// <summary>
    /// The tag image whose user-memory pages are copied onto each tag in
    /// <see cref="TagMode.Program"/>. Typically a dump of a tag the charger
    /// has already saved a profile to.
    /// </summary>
    public TagImage? Template { get; set; }

    /// <summary>
    /// In <see cref="TagMode.Program"/>, also copy the template's AUTH0 and
    /// ACCESS settings, so the copy is password-protected the same way the
    /// charger protected the original. Off by default. The config-lock and
    /// failed-attempt-limit bits are never copied, so a tag can't be bricked.
    /// </summary>
    public bool CopyProtectionConfig { get; set; }

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

        if (Mode == TagMode.Program)
        {
            if (Template is null)
                throw new InvalidOperationException("Program mode needs a Template.");
            if (!Template.UserPages.Any())
                throw new InvalidOperationException(
                    "The template holds no user-memory pages (0x04-0x27), so there is nothing to program.");
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

            var success = ProcessTag(transport!, uid);
            if (success)
            {
                Interlocked.Increment(ref _preppedCount);
            }

            TagPrepped?.Invoke(this, new TagPreppedEventArgs(uid, success, _preppedCount));

            Wait(token, PollIntervalMs);
        }

        RaiseLog("Stopped watching.");
    }

    private bool ProcessTag(ITagTransport transport, string uid)
    {
        var session = new TagSession(transport);

        return Mode switch
        {
            TagMode.Dump => DumpTag(session, uid),
            TagMode.Program => ProgramTag(session),
            _ => EnsureWriteAccess(session) && PrepOneTag(session),
        };
    }

    private bool PrepOneTag(TagSession session)
    {
        var ok = WritePage(session, NfcCommands.PasswordPage, NfcCommands.PasswordValue,
            "Password (page 43)");
        if (!ok) return false;

        return WritePage(session, NfcCommands.PackPage, NfcCommands.PackValue,
            "PACK (page 44)");
    }

    private bool DumpTag(TagSession session, string uid)
    {
        var image = ReadImage(session, uid);
        if (image is null)
        {
            StepCompleted?.Invoke(this, new TagStepEventArgs("Read tag", false,
                "could not read every page - hold the tag still and try again"));
            return false;
        }

        StepCompleted?.Invoke(this, new TagStepEventArgs("Read tag", true,
            $"{image.PageNumbers.Count} pages"));
        TagRead?.Invoke(this, new TagReadEventArgs(uid, image));
        return true;
    }

    private bool ProgramTag(TagSession session)
    {
        var template = Template!;

        if (!EnsureWriteAccess(session)) return false;

        WarnIfCapabilityContainerDiffers(session, template);

        if (!PrepOneTag(session)) return false;
        if (!WriteProfile(session, template)) return false;
        if (!VerifyProfile(session, template)) return false;

        return !CopyProtectionConfig || WriteProtectionConfig(session, template);
    }

    /// <summary>
    /// Makes sure the tag will accept writes. A blank or prepped tag is open,
    /// but one the charger has already used is normally password-protected,
    /// so authenticate with the BumpGo password when AUTH0 says so (or when
    /// the configuration pages can't even be read).
    /// </summary>
    private bool EnsureWriteAccess(TagSession session)
    {
        var config = ReadWithAuth(session, NfcCommands.Config0Page);
        if (config is null)
        {
            StepCompleted?.Invoke(this, new TagStepEventArgs("Read config", false,
                "could not read the tag's configuration pages"));
            return false;
        }

        if (session.Authenticated) return true;

        // A full read from page 0x29 covers CFG0, CFG1, PWD and PACK. Anything
        // shorter isn't a normal reader reply; with nothing to go on, assume
        // the tag is open and let the writes themselves report any problem.
        if (config.Length < 8) return true;

        var auth0 = config[NfcCommands.Auth0Index];
        if (auth0 > NfcCommands.PackPage) return true;

        RaiseLog($"Tag is password-protected from page 0x{auth0:X2}; authenticating with the BumpGo password.");
        return Authenticate(session);
    }

    private bool Authenticate(TagSession session)
    {
        var ok = session.Transport.TryTransmit(NfcCommands.BuildPasswordAuth(NfcCommands.PasswordValue),
                     out var response)
                 && NfcCommands.TryParsePasswordAuth(response, out var pack);

        session.Authenticated = ok;

        var detail = ok
            ? "accepted"
            : "the tag rejected the BumpGo password (it may belong to something else)";
        StepCompleted?.Invoke(this, new TagStepEventArgs("Password auth", ok, detail));
        return ok;
    }

    /// <summary>Reads pages 0x00-0x2A. PWD and PACK always read back as zeros, so they are skipped.</summary>
    private TagImage? ReadImage(TagSession session, string uid)
    {
        var image = new TagImage(uid);

        for (var page = 0; page <= NfcCommands.LastReadablePage; page += 4)
        {
            var data = ReadWithAuth(session, (byte)page);
            if (data is null || data.Length < 16) return null;

            for (var i = 0; i < 4 && page + i <= NfcCommands.LastReadablePage; i++)
                image.SetPage((byte)(page + i), data[(i * 4)..(i * 4 + 4)]);
        }

        return image;
    }

    /// <summary>
    /// Reads four pages starting at <paramref name="page"/>. If the tag refuses,
    /// it is most likely read-protected: re-select it, authenticate, and try once more.
    /// </summary>
    private byte[]? ReadWithAuth(TagSession session, byte page)
    {
        var data = ReadFourPages(session.Transport, page);
        if (data is not null || session.Authenticated) return data;

        RaiseLog($"Page 0x{page:X2} can't be read without the password; authenticating.");

        if (!session.Transport.TryReset())
        {
            StepCompleted?.Invoke(this, new TagStepEventArgs("Re-select tag", false,
                "the reader could not re-select the tag"));
            return null;
        }

        return Authenticate(session) ? ReadFourPages(session.Transport, page) : null;
    }

    /// <summary>One Read Binary of 16 bytes (four pages). Null if the tag or reader refused.</summary>
    private static byte[]? ReadFourPages(ITagTransport transport, byte page)
    {
        var apdu = NfcCommands.BuildReadBinary(page, 16);
        if (!transport.TryTransmit(apdu, out var response) || !NfcCommands.IsSuccess(response))
            return null;

        return NfcCommands.StripStatusWord(response);
    }

    private bool WriteProfile(TagSession session, TagImage template)
    {
        var pages = template.UserPages.ToArray();

        foreach (var page in pages)
        {
            if (!TryWritePage(session.Transport, page, template[page]!, out var detail))
            {
                StepCompleted?.Invoke(this, new TagStepEventArgs("Profile data", false,
                    $"page 0x{page:X2}: {detail}"));
                return false;
            }
        }

        StepCompleted?.Invoke(this, new TagStepEventArgs("Profile data", true,
            $"{pages.Length} pages written"));
        return true;
    }

    private bool VerifyProfile(TagSession session, TagImage template)
    {
        for (var start = NfcCommands.UserFirstPage; start <= NfcCommands.UserLastPage; start += 4)
        {
            var data = ReadWithAuth(session, (byte)start);
            if (data is null || data.Length < 16)
            {
                StepCompleted?.Invoke(this, new TagStepEventArgs("Verify", false,
                    $"could not read back page 0x{start:X2}"));
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                var page = (byte)(start + i);
                var expected = template[page];
                if (expected is null || page > NfcCommands.UserLastPage) continue;

                var actual = data[(i * 4)..(i * 4 + 4)];
                if (!actual.SequenceEqual(expected))
                {
                    StepCompleted?.Invoke(this, new TagStepEventArgs("Verify", false,
                        $"page 0x{page:X2} reads back as {BitConverter.ToString(actual)}, " +
                        $"expected {BitConverter.ToString(expected)}"));
                    return false;
                }
            }
        }

        StepCompleted?.Invoke(this, new TagStepEventArgs("Verify", true, "profile reads back correctly"));
        return true;
    }

    /// <summary>
    /// Copies AUTH0 and ACCESS from the template. ACCESS goes first: once AUTH0
    /// is written the protection is live, and further writes would need the
    /// password. The CFGLCK and AUTHLIM bits are stripped - one makes the
    /// configuration permanent, the other can permanently disable the tag
    /// after failed password attempts.
    /// </summary>
    private bool WriteProtectionConfig(TagSession session, TagImage template)
    {
        const string label = "Protection config";

        var cfg0 = template[NfcCommands.Config0Page];
        var cfg1 = template[NfcCommands.Config1Page];
        if (cfg0 is null || cfg1 is null)
        {
            StepCompleted?.Invoke(this, new TagStepEventArgs(label, false,
                "the template has no CFG0/CFG1 pages to copy"));
            return false;
        }

        cfg1 = (byte[])cfg1.Clone();
        const byte unsafeBits = NfcCommands.AccessCfgLckBit | NfcCommands.AccessAuthLimMask;
        if ((cfg1[0] & unsafeBits) != 0)
        {
            RaiseLog($"Template ACCESS 0x{cfg1[0]:X2} has lock/attempt-limit bits set; " +
                     $"writing 0x{cfg1[0] & ~unsafeBits:X2} instead so the tag stays changeable.");
            cfg1[0] = (byte)(cfg1[0] & ~unsafeBits);
        }

        if (!TryWritePage(session.Transport, NfcCommands.Config1Page, cfg1, out var detail) ||
            !TryWritePage(session.Transport, NfcCommands.Config0Page, cfg0, out detail))
        {
            StepCompleted?.Invoke(this, new TagStepEventArgs(label, false, detail));
            return false;
        }

        StepCompleted?.Invoke(this, new TagStepEventArgs(label, true,
            $"AUTH0 0x{cfg0[NfcCommands.Auth0Index]:X2}, ACCESS 0x{cfg1[0]:X2}"));
        return true;
    }

    /// <summary>
    /// The capability container is one-time-programmable, so it is never
    /// written. If it doesn't match the template, say so: it is a likely
    /// culprit if the charger rejects a programmed tag.
    /// </summary>
    private void WarnIfCapabilityContainerDiffers(TagSession session, TagImage template)
    {
        var expected = template[NfcCommands.CapabilityContainerPage];
        if (expected is null) return;

        var data = ReadFourPages(session.Transport, 0x00);
        if (data is null || data.Length < 16) return;

        var actual = data[12..16];
        if (!actual.SequenceEqual(expected))
        {
            RaiseLog($"Note: this tag's capability container ({BitConverter.ToString(actual)}) differs from " +
                     $"the template's ({BitConverter.ToString(expected)}). It can't be rewritten, " +
                     "so if the charger rejects this tag, that may be why.");
        }
    }

    private bool WritePage(TagSession session, byte page, byte[] value, string label)
    {
        var ok = TryWritePage(session.Transport, page, value, out var detail);
        StepCompleted?.Invoke(this, new TagStepEventArgs(label, ok, ok ? "written" : detail));
        return ok;
    }

    private static bool TryWritePage(ITagTransport transport, byte page, byte[] value, out string detail)
    {
        var apdu = NfcCommands.BuildUpdateBinary(page, value);
        detail = "";

        // Writes are idempotent, so retry a couple of times: the tag can miss
        // an exchange if it is moved or the field is briefly disturbed.
        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            var transmitted = transport.TryTransmit(apdu, out var response);

            if (transmitted && NfcCommands.IsSuccess(response))
                return true;

            if (attempt < MaxWriteAttempts)
            {
                Thread.Sleep(RetryDelayMs);
                continue;
            }

            detail = transmitted && response.Length > 0
                ? $"reader returned {BitConverter.ToString(response)}"
                : "no response from tag";
        }

        return false;
    }

    /// <summary>Per-tag state: the connection, and whether we've authenticated on it.</summary>
    private sealed class TagSession
    {
        public TagSession(ITagTransport transport) => Transport = transport;

        public ITagTransport Transport { get; }
        public bool Authenticated { get; set; }
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
