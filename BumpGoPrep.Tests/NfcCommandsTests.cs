using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

public class NfcCommandsTests
{
    [Fact]
    public void BuildUpdateBinary_writes_four_bytes_to_the_requested_page()
    {
        var apdu = NfcCommands.BuildUpdateBinary(0x2B, new byte[] { 0x05, 0x07, 0x09, 0x03 });

        Assert.Equal(new byte[] { 0xFF, 0xD6, 0x00, 0x2B, 0x04, 0x05, 0x07, 0x09, 0x03 }, apdu);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void BuildUpdateBinary_rejects_anything_that_is_not_a_full_page(int length)
    {
        Assert.Throws<ArgumentException>(() => NfcCommands.BuildUpdateBinary(0x2B, new byte[length]));
    }

    [Fact]
    public void BuildReadBinary_reads_the_requested_page_and_length()
    {
        var apdu = NfcCommands.BuildReadBinary(NfcCommands.CapabilityContainerPage, 4);

        Assert.Equal(new byte[] { 0xFF, 0xB0, 0x00, 0x03, 0x04 }, apdu);
    }

    [Fact]
    public void The_password_is_the_value_the_charger_expects()
    {
        // Guards the two constants the whole procedure depends on: the charger
        // only accepts a tag carrying this password with a zeroed PACK.
        Assert.Equal(new byte[] { 0x05, 0x07, 0x09, 0x03 }, NfcCommands.PasswordValue);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, NfcCommands.PackValue);
        Assert.Equal(0x2B, NfcCommands.PasswordPage);
        Assert.Equal(0x2C, NfcCommands.PackPage);
    }

    [Fact]
    public void A_bare_status_word_counts_as_success()
    {
        // A successful write returns nothing but 90 00. Treating that as tag
        // data was the original bug: it reported working writes as failures.
        Assert.True(NfcCommands.IsSuccess(new byte[] { 0x90, 0x00 }));
        Assert.Empty(NfcCommands.StripStatusWord(new byte[] { 0x90, 0x00 }));
    }

    [Fact]
    public void The_status_word_is_recognised_after_a_pn532_response_header()
    {
        // D5 41 00 is the PN532 InDataExchange header; the trailing 90 00 is
        // the reader's own status word, not part of the tag's reply.
        var response = new byte[] { 0xD5, 0x41, 0x00, 0x90, 0x00 };

        Assert.True(NfcCommands.IsSuccess(response));
        Assert.Equal(new byte[] { 0xD5, 0x41, 0x00 }, NfcCommands.StripStatusWord(response));
    }

    [Fact]
    public void A_failure_status_word_is_not_success()
    {
        Assert.False(NfcCommands.IsSuccess(new byte[] { 0x63, 0x00 }));
        Assert.False(NfcCommands.IsSuccess(new byte[] { 0x6A, 0x82 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_response_too_short_to_hold_a_status_word_is_not_success(int length)
    {
        Assert.False(NfcCommands.IsSuccess(new byte[length]));
        Assert.Empty(NfcCommands.StripStatusWord(new byte[length]));
    }

    [Fact]
    public void StripStatusWord_leaves_the_payload_intact()
    {
        var uid = new byte[] { 0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        var response = uid.Concat(new byte[] { 0x90, 0x00 }).ToArray();

        Assert.Equal(uid, NfcCommands.StripStatusWord(response));
    }
}

public class PasswordAuthCommandTests
{
    [Fact]
    public void BuildPasswordAuth_wraps_pwd_auth_in_a_direct_transmit()
    {
        var apdu = NfcCommands.BuildPasswordAuth(NfcCommands.PasswordValue);

        Assert.Equal(
            new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x07, 0xD4, 0x42, 0x1B, 0x05, 0x07, 0x09, 0x03 },
            apdu);
    }

    [Fact]
    public void A_successful_auth_returns_the_pack()
    {
        var ok = NfcCommands.TryParsePasswordAuth(new byte[] { 0xD5, 0x43, 0x00, 0x12, 0x34, 0x90, 0x00 }, out var pack);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0x12, 0x34 }, pack);
    }

    [Theory]
    [InlineData(new byte[] { 0xD5, 0x43, 0x01, 0x90, 0x00 })]       // tag NAK'd (wrong password)
    [InlineData(new byte[] { 0xD5, 0x43, 0x00, 0x90, 0x00 })]       // no PACK in the reply
    [InlineData(new byte[] { 0x63, 0x00 })]                         // reader error
    [InlineData(new byte[] { 0xD5, 0x41, 0x00, 0x00, 0x00, 0x90, 0x00 })] // wrong PN532 response code
    public void Anything_else_is_a_failed_auth(byte[] response)
    {
        Assert.False(NfcCommands.TryParsePasswordAuth(response, out _));
    }
}
