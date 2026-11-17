using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>
/// A stand-in for a tag on the reader. Replies are decided by a handler the
/// test supplies, so a test can describe exactly how its tag behaves; every
/// APDU sent to it is recorded for assertions.
/// </summary>
internal sealed class FakeTag : ITagTransport
{
    private readonly Func<byte[], byte[]?> _respond;
    private readonly List<byte[]> _sent = new();
    private int _disposeCount;

    /// <param name="respond">
    /// Returns the reply for an APDU, or null to simulate a failed exchange
    /// (tag lifted mid-command, reader error).
    /// </param>
    public FakeTag(Func<byte[], byte[]?> respond) => _respond = respond;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public IReadOnlyList<byte[]> Sent
    {
        get { lock (_sent) return _sent.ToArray(); }
    }

    /// <summary>Every APDU sent with the given instruction byte, in order.</summary>
    public IReadOnlyList<byte[]> SentWithInstruction(byte instruction) =>
        Sent.Where(a => a.Length > 1 && a[1] == instruction).ToArray();

    public bool TryTransmit(byte[] apdu, out byte[] response)
    {
        lock (_sent) _sent.Add((byte[])apdu.Clone());

        var reply = _respond(apdu);
        response = reply ?? Array.Empty<byte>();
        return reply is not null;
    }

    public bool TryReset() => true;

    public void Dispose() => Interlocked.Increment(ref _disposeCount);
}

/// <summary>Building blocks for tag replies and APDU inspection.</summary>
internal static class Apdu
{
    public const byte GetUid = 0xCA;
    public const byte ReadBinary = 0xB0;
    public const byte UpdateBinary = 0xD6;

    public static readonly byte[] DefaultUid = { 0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
    public static readonly byte[] Ntag213Cc = { 0xE1, 0x10, 0x12, 0x00 };
    public static readonly byte[] Ntag215Cc = { 0xE1, 0x10, 0x3E, 0x00 };
    public static readonly byte[] VirginCc = { 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Data followed by the 90 00 success status word.</summary>
    public static byte[] Ok(params byte[] data) => data.Concat(new byte[] { 0x90, 0x00 }).ToArray();

    /// <summary>The 63 00 "operation failed" status word.</summary>
    public static byte[] Failed() => new byte[] { 0x63, 0x00 };

    /// <summary>The page a Read/Update Binary APDU targets.</summary>
    public static byte TargetPage(byte[] apdu) => apdu[3];

    /// <summary>The payload of an Update Binary APDU.</summary>
    public static byte[] WrittenData(byte[] apdu) => apdu[5..];

    /// <summary>A tag that answers everything successfully.</summary>
    public static Func<byte[], byte[]?> HealthyTag(byte[]? uid = null, byte[]? capabilityContainer = null) =>
        apdu => apdu[1] switch
        {
            GetUid => Ok(uid ?? DefaultUid),
            ReadBinary => Ok(capabilityContainer ?? Ntag213Cc),
            UpdateBinary => Ok(),
            _ => null,
        };
}
