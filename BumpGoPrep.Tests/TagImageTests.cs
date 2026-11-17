using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

public class TagImageTests
{
    private static TagImage Sample()
    {
        var image = new TagImage("04-A1-B2-C3-D4-E5-F6");
        image.SetPage(0x00, new byte[] { 0x04, 0xA1, 0xB2, 0x88 });
        image.SetPage(0x04, new byte[] { 0x42, 0x55, 0x4D, 0x50 }); // "BUMP"
        image.SetPage(0x10, new byte[] { 0x00, 0x01, 0xFE, 0xFF });
        image.SetPage(NfcCommands.Config0Page, new byte[] { 0x04, 0x00, 0x00, 0x04 });
        image.SetPage(NfcCommands.Config1Page, new byte[] { 0x80, 0x05, 0x00, 0x00 });
        return image;
    }

    [Fact]
    public void A_saved_dump_loads_back_identically()
    {
        var original = Sample();

        var loaded = TagImage.Parse(original.ToText(new DateTime(2026, 11, 17, 9, 0, 0)));

        Assert.Equal(original.Uid, loaded.Uid);
        Assert.Equal(original.PageNumbers, loaded.PageNumbers);
        foreach (var page in original.PageNumbers)
            Assert.Equal(original[page], loaded[page]);
    }

    [Fact]
    public void The_saved_dump_describes_the_protection_settings()
    {
        var text = Sample().ToText();

        Assert.Contains("AUTH0    0x04", text);
        Assert.Contains("reads and writes", text);
        Assert.Contains("|BUMP|", text);
    }

    [Fact]
    public void Parsing_tolerates_hand_edited_files()
    {
        const string text = """
            # a comment
            4: de ad be ef
              05 : 01 02 03 04   trailing note
            2B: ?? ?? ?? ??
            not a page line
            06: 01 02 03
            """;

        var image = TagImage.Parse(text);

        Assert.Equal(new byte[] { 0x04, 0x05 }, image.PageNumbers);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, image[0x04]);
        Assert.Null(image.Uid);
    }

    [Fact]
    public void Compare_lists_only_the_pages_and_bytes_that_differ()
    {
        var a = Sample();
        var b = Sample();
        b.SetPage(0x10, new byte[] { 0x00, 0x02, 0xFE, 0xFF });

        var diff = Assert.Single(TagImage.Compare(new[] { a, b }));

        Assert.Equal(0x10, diff.Page);
        Assert.Equal(new[] { false, true, false, false }, diff.ChangedBytes);
    }

    [Fact]
    public void Compare_counts_a_page_missing_from_one_dump_as_a_difference()
    {
        var a = Sample();
        var b = Sample();
        b.SetPage(0x20, new byte[] { 0, 0, 0, 0 });

        var diff = Assert.Single(TagImage.Compare(new[] { a, b }));

        Assert.Equal(0x20, diff.Page);
        Assert.Null(diff.Values[0]);
    }

    [Fact]
    public void UserPages_covers_only_the_profile_area()
    {
        Assert.Equal(new byte[] { 0x04, 0x10 }, Sample().UserPages);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Pages_must_be_four_bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => new TagImage().SetPage(0x04, new byte[length]));
    }
}
