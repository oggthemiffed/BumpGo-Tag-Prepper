using PCSC;
using PCSC.Exceptions;

namespace BumpGoPrep.Core;

/// <summary>
/// A live connection to the tag currently on the reader, narrowed to the one
/// operation the prepping logic actually needs. Keeping the PC/SC plumbing
/// behind this seam lets the command/response handling be exercised without
/// hardware.
/// </summary>
internal interface ITagTransport : IDisposable
{
    /// <summary>
    /// Sends an APDU and returns the raw reply, including its trailing status
    /// word. Returns false if the exchange itself failed (tag removed, reader
    /// error), as opposed to the tag replying with a failure status.
    /// </summary>
    bool TryTransmit(byte[] apdu, out byte[] response);

    /// <summary>
    /// Re-activates the tag (RF reset and re-select). Needed after the tag has
    /// NAK'd a command, e.g. a read of a password-protected page, because an
    /// NTAG drops back to its idle state on a NAK and ignores everything until
    /// it is selected again.
    /// </summary>
    bool TryReset();
}

/// <summary>Talks to a real tag over PC/SC. Owns the reader it wraps.</summary>
internal sealed class ScardTransport : ITagTransport
{
    private const int MaxResponseLength = 258;

    private readonly SCardReader _reader;

    public ScardTransport(SCardReader reader) => _reader = reader;

    public bool TryTransmit(byte[] apdu, out byte[] response)
    {
        response = new byte[MaxResponseLength];
        var sendPci = SCardPCI.GetPci(_reader.ActiveProtocol);

        SCardError result;
        try
        {
            result = _reader.Transmit(sendPci, apdu, ref response);
        }
        catch (PCSCException)
        {
            response = Array.Empty<byte>();
            return false;
        }

        if (result != SCardError.Success)
        {
            response = Array.Empty<byte>();
            return false;
        }

        return true;
    }

    public bool TryReset()
    {
        try
        {
            return _reader.Reconnect(SCardShareMode.Shared, SCardProtocol.Any,
                SCardReaderDisposition.Reset) == SCardError.Success;
        }
        catch (PCSCException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            // Leave: don't power the tag down, it may be lifted off at any moment.
            _reader.Disconnect(SCardReaderDisposition.Leave);
        }
        catch (PCSCException)
        {
            // ignore — tag may already have been removed
        }
        catch (InvalidOperationException)
        {
            // ignore — already disconnected
        }

        _reader.Dispose();
    }
}
