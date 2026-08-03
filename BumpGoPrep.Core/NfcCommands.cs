namespace BumpGoPrep.Core;

/// <summary>
/// Commands used to prep a blank NTAG213 so the SkyRC D200neo+ recognizes it
/// as a valid empty BumpGo tag.
/// </summary>
internal static class NfcCommands
{
    /// <summary>NTAG213 page holding the 4-byte password (PWD).</summary>
    public const byte PasswordPage = 0x2B;

    /// <summary>NTAG213 page holding the 2-byte password ack (PACK) plus 2 reserved bytes.</summary>
    public const byte PackPage = 0x2C;

    /// <summary>
    /// The fixed password the charger expects. Writing it (and a zeroed PACK)
    /// is all a blank tag needs — the charger recognizes it as an empty BumpGo
    /// tag and writes the charging profile itself on the first bump.
    /// </summary>
    public static readonly byte[] PasswordValue = { 0x05, 0x07, 0x09, 0x03 };

    public static readonly byte[] PackValue = { 0x00, 0x00, 0x00, 0x00 };

    /// <summary>ACR122U "Get UID" pseudo-APDU.</summary>
    public static readonly byte[] GetUid = { 0xFF, 0xCA, 0x00, 0x00, 0x00 };

    /// <summary>Page holding the capability container, used to tell NTAG variants apart.</summary>
    public const byte CapabilityContainerPage = 0x03;

    /// <summary>Third capability container byte, which encodes usable memory size.</summary>
    public const byte Ntag213SizeByte = 0x12;

    public const byte Ntag215SizeByte = 0x3E;
    public const byte Ntag216SizeByte = 0x6D;

    /// <summary>Builds a "Read Binary" APDU: FF B0 00 &lt;page&gt; &lt;length&gt;.</summary>
    public static byte[] BuildReadBinary(byte page, byte length) =>
        new byte[] { 0xFF, 0xB0, 0x00, page, length };

    /// <summary>
    /// Builds an "Update Binary" APDU: FF D6 00 &lt;page&gt; 04 &lt;4 bytes&gt;.
    /// </summary>
    /// <remarks>
    /// This is the reader's built-in write command. It handles NfcA framing,
    /// CRC and the tag's ACK internally, so it avoids the PN532 Direct
    /// Transmit wrapping (D4 40 …) that raw NTAG WRITE commands would need.
    /// Success is reported purely through the status word.
    /// </remarks>
    public static byte[] BuildUpdateBinary(byte page, byte[] fourBytes)
    {
        if (fourBytes.Length != 4)
            throw new ArgumentException("NTAG213 pages are 4 bytes.", nameof(fourBytes));

        var apdu = new byte[5 + 4];
        apdu[0] = 0xFF;
        apdu[1] = 0xD6;
        apdu[2] = 0x00;
        apdu[3] = page;
        apdu[4] = 0x04;
        Array.Copy(fourBytes, 0, apdu, 5, 4);
        return apdu;
    }

    /// <summary>
    /// True if the response ends with the 90 00 status word. Every response
    /// from the reader carries this trailing SW1 SW2 pair; it is not tag data.
    /// </summary>
    public static bool IsSuccess(byte[] response) =>
        response.Length >= 2 && response[^2] == 0x90 && response[^1] == 0x00;

    /// <summary>Returns the response with its trailing SW1 SW2 status word removed.</summary>
    public static byte[] StripStatusWord(byte[] response) =>
        response.Length >= 2 ? response[..^2] : Array.Empty<byte>();
}
