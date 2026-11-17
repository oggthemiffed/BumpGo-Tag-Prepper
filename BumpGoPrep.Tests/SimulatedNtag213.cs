using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>
/// An NTAG213 behind an ACR122U, modelled closely enough to exercise password
/// protection: AUTH0 / PROT decide which pages need PWD_AUTH, a refused
/// command NAKs and drops the tag to idle until it is re-selected, PWD and
/// PACK read back as zeros, and reads wrap from page 0x2C to 0x00.
/// </summary>
internal sealed class SimulatedNtag213 : ITagTransport
{
    public static readonly byte[] BumpGoPassword = { 0x05, 0x07, 0x09, 0x03 };

    private const int PageCount = 0x2D;

    private readonly object _gate = new();
    private readonly byte[][] _pages = new byte[PageCount][];
    private readonly List<byte> _writtenPages = new();
    private bool _authenticated;
    private bool _idle;

    public SimulatedNtag213(byte[]? uid = null)
    {
        Uid = uid ?? new byte[] { 0x04, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6 };

        for (var i = 0; i < PageCount; i++) _pages[i] = new byte[4];

        _pages[0] = new byte[] { Uid[0], Uid[1], Uid[2], 0x88 };
        _pages[1] = Uid[3..7];
        _pages[3] = new byte[] { 0xE1, 0x10, 0x12, 0x00 };

        // Factory defaults: nothing protected, password FF FF FF FF.
        _pages[NfcCommands.Config0Page] = new byte[] { 0x04, 0x00, 0x00, 0xFF };
        _pages[NfcCommands.Config1Page] = new byte[] { 0x00, 0x05, 0x00, 0x00 };
        _pages[NfcCommands.PasswordPage] = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    }

    public byte[] Uid { get; }

    /// <summary>Optional fault injection: transforms the data of a write before it is stored.</summary>
    public Func<byte, byte[], byte[]>? CorruptWrite { get; set; }

    public int Resets { get; private set; }

    public byte[] Page(byte page)
    {
        lock (_gate) return (byte[])_pages[page].Clone();
    }

    public IReadOnlyList<byte> WrittenPages
    {
        get { lock (_gate) return _writtenPages.ToArray(); }
    }

    public SimulatedNtag213 WithPage(byte page, params byte[] data)
    {
        _pages[page] = data;
        return this;
    }

    /// <summary>A tag the charger has saved a profile to: BumpGo password, protected from page 4.</summary>
    public SimulatedNtag213 ProtectedByCharger(bool protectReads = false, byte[]? password = null)
    {
        _pages[NfcCommands.PasswordPage] = password ?? BumpGoPassword;
        _pages[NfcCommands.Config0Page][NfcCommands.Auth0Index] = 0x04;
        if (protectReads) _pages[NfcCommands.Config1Page][0] |= NfcCommands.AccessProtBit;
        return this;
    }

    /// <summary>Fills user memory with recognisable data, standing in for a charger profile.</summary>
    public SimulatedNtag213 WithProfile(byte seed)
    {
        for (var p = NfcCommands.UserFirstPage; p <= NfcCommands.UserLastPage; p++)
            _pages[p] = new[] { seed, (byte)p, (byte)(seed ^ p), (byte)(p * 3) };
        return this;
    }

    private byte Auth0 => _pages[NfcCommands.Config0Page][NfcCommands.Auth0Index];
    private bool ReadsProtected => (_pages[NfcCommands.Config1Page][0] & NfcCommands.AccessProtBit) != 0;

    public bool TryTransmit(byte[] apdu, out byte[] response)
    {
        lock (_gate)
        {
            response = Handle(apdu);
            return true;
        }
    }

    private byte[] Handle(byte[] apdu)
    {
        if (apdu is [0xFF, 0xCA, ..])
            return Ok(Uid);

        if (apdu is [0xFF, 0xB0, 0x00, var readPage, 0x10])
            return Read(readPage);

        if (apdu is [0xFF, 0xB0, 0x00, var shortPage, 0x04])
        {
            var full = Read(shortPage);
            return full.Length == 18 ? Ok(full[..4]) : full;
        }

        if (apdu is [0xFF, 0xD6, 0x00, var writePage, 0x04, ..] && apdu.Length == 9)
            return Write(writePage, apdu[5..9]);

        if (apdu is [0xFF, 0x00, 0x00, 0x00, 0x07, 0xD4, 0x42, 0x1B, ..] && apdu.Length == 12)
            return PasswordAuth(apdu[8..12]);

        return Failed();
    }

    private byte[] Read(byte page)
    {
        if (_idle || page >= PageCount) return Nak();

        var data = new List<byte>();
        for (var i = 0; i < 4; i++)
        {
            var p = (page + i) % PageCount;
            if (ReadsProtected && !_authenticated && p >= Auth0) return Nak();

            // PWD and PACK always read back as zeros.
            data.AddRange(p >= NfcCommands.PasswordPage ? new byte[4] : _pages[p]);
        }

        return Ok(data.ToArray());
    }

    private byte[] Write(byte page, byte[] data)
    {
        if (_idle || page < 2 || page >= PageCount) return Nak();
        if (!_authenticated && page >= Auth0) return Nak();

        _writtenPages.Add(page);
        _pages[page] = CorruptWrite?.Invoke(page, data) ?? (byte[])data.Clone();
        return Ok();
    }

    private byte[] PasswordAuth(byte[] password)
    {
        if (_idle) return Ok(0xD5, 0x43, 0x01);

        if (!password.SequenceEqual(_pages[NfcCommands.PasswordPage]))
        {
            _idle = true;
            return Ok(0xD5, 0x43, 0x01);
        }

        _authenticated = true;
        var pack = _pages[NfcCommands.PackPage];
        return Ok(0xD5, 0x43, 0x00, pack[0], pack[1]);
    }

    /// <summary>The tag refused: it drops to idle, losing any authentication.</summary>
    private byte[] Nak()
    {
        _idle = true;
        _authenticated = false;
        return Failed();
    }

    public bool TryReset()
    {
        lock (_gate)
        {
            Resets++;
            _idle = false;
            _authenticated = false;
            return true;
        }
    }

    public void Dispose()
    {
        // A fresh connection on the next poll doesn't re-select the tag, so
        // state (including idle) carries over; assumed to match real hardware.
    }

    private static byte[] Ok(params byte[] data) => data.Concat(new byte[] { 0x90, 0x00 }).ToArray();
    private static byte[] Failed() => new byte[] { 0x63, 0x00 };
}
