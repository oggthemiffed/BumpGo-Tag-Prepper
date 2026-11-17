using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>
/// Dump and Program modes, driven against a simulated NTAG213 so password
/// protection behaves as it does on a real tag.
/// </summary>
public class ProgrammingTests
{
    private static TagImage DumpOf(SimulatedNtag213 tag)
    {
        using var harness = Harness.Processing(tag, s => s.Mode = TagMode.Dump);
        Assert.Equal(new[] { true }, harness.PrepResults());
        return Assert.Single(harness.Images());
    }

    private static Harness Program(SimulatedNtag213 tag, TagImage template, bool withProtection = false) =>
        Harness.Processing(tag, s =>
        {
            s.Mode = TagMode.Program;
            s.Template = template;
            s.CopyProtectionConfig = withProtection;
        });

    [Fact]
    public void Dump_reads_every_readable_page_and_writes_nothing()
    {
        var tag = new SimulatedNtag213().WithProfile(0x5A).ProtectedByCharger();

        var image = DumpOf(tag);

        Assert.Equal(Enumerable.Range(0, 0x2B).Select(p => (byte)p), image.PageNumbers);
        Assert.Equal(tag.Page(0x10), image[0x10]);
        Assert.Equal((byte)0x04, image.Auth0);
        Assert.Empty(tag.WrittenPages);
    }

    [Fact]
    public void Dump_authenticates_when_the_charger_has_protected_reads()
    {
        var tag = new SimulatedNtag213().WithProfile(0x33).ProtectedByCharger(protectReads: true);

        var image = DumpOf(tag);

        Assert.True(tag.Resets >= 1);
        foreach (var page in image.UserPages)
            Assert.Equal(tag.Page(page), image[page]);
    }

    [Fact]
    public void Program_preps_the_tag_then_copies_and_verifies_the_profile()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x77).ProtectedByCharger());
        var blank = new SimulatedNtag213(new byte[] { 0x04, 1, 2, 3, 4, 5, 6 });

        using var harness = Program(blank, template);

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(SimulatedNtag213.BumpGoPassword, blank.Page(NfcCommands.PasswordPage));
        for (var p = NfcCommands.UserFirstPage; p <= NfcCommands.UserLastPage; p++)
            Assert.Equal(template[p], blank.Page(p));

        Assert.Equal(
            new[] { "Password (page 43): OK", "PACK (page 44): OK", "Profile data: OK", "Verify: OK" },
            harness.Steps());

        // Protection is left off unless asked for.
        Assert.Equal(0xFF, blank.Page(NfcCommands.Config0Page)[NfcCommands.Auth0Index]);
    }

    [Fact]
    public void Program_only_ever_writes_user_memory_password_and_pack()
    {
        // A template with every page filled, including UID, lock and config pages.
        var template = new TagImage();
        for (byte p = 0; p <= NfcCommands.LastReadablePage; p++)
            template.SetPage(p, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE });

        var blank = new SimulatedNtag213();
        using var harness = Program(blank, template);

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.All(blank.WrittenPages, p => Assert.True(
            p is >= NfcCommands.UserFirstPage and <= NfcCommands.UserLastPage
              or NfcCommands.PasswordPage or NfcCommands.PackPage,
            $"wrote page 0x{p:X2}"));
    }

    [Fact]
    public void Program_can_overwrite_a_tag_the_charger_already_protected()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x11));
        var used = new SimulatedNtag213().WithProfile(0x99).ProtectedByCharger();

        using var harness = Program(used, template);

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Contains("Password auth: OK", harness.Steps());
        Assert.Equal(template[0x20], used.Page(0x20));
    }

    [Fact]
    public void Program_gives_up_cleanly_on_a_tag_with_someone_elses_password()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x11));
        var foreign = new SimulatedNtag213().WithProfile(0x99)
            .ProtectedByCharger(password: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        using var harness = Program(foreign, template);

        Assert.Equal(new[] { false }, harness.PrepResults());
        Assert.Contains("Password auth: FAILED", harness.Steps());
        Assert.Empty(foreign.WrittenPages);
    }

    [Fact]
    public void Program_reports_a_page_that_does_not_read_back_as_written()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x42));
        var flaky = new SimulatedNtag213
        {
            CorruptWrite = (page, data) => page == 0x0C ? new byte[4] : data,
        };

        using var harness = Program(flaky, template);

        Assert.Equal(new[] { false }, harness.PrepResults());
        Assert.Contains("Verify: FAILED", harness.Steps());
    }

    [Fact]
    public void Copying_protection_sets_auth0_and_access_but_never_the_lock_bits()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x42).ProtectedByCharger(protectReads: true));

        // Pretend the charger had also set CFGLCK and an attempt limit.
        var cfg1 = template[NfcCommands.Config1Page]!;
        cfg1[0] |= NfcCommands.AccessCfgLckBit | 0x03;
        template.SetPage(NfcCommands.Config1Page, cfg1);

        var blank = new SimulatedNtag213();
        using var harness = Program(blank, template, withProtection: true);

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(0x04, blank.Page(NfcCommands.Config0Page)[NfcCommands.Auth0Index]);
        Assert.Equal(NfcCommands.AccessProtBit, blank.Page(NfcCommands.Config1Page)[0]);
        Assert.Contains(harness.Logs(), m => m.Contains("lock"));
    }

    [Fact]
    public void A_programmed_tag_can_be_dumped_back_to_the_same_profile()
    {
        var template = DumpOf(new SimulatedNtag213().WithProfile(0x6B).ProtectedByCharger());
        var blank = new SimulatedNtag213();

        using (var harness = Program(blank, template, withProtection: true))
            Assert.Equal(new[] { true }, harness.PrepResults());

        var copy = DumpOf(blank);
        var differences = TagImage.Compare(new[] { template, copy });

        // Only the UID pages should differ.
        Assert.All(differences, d => Assert.True(d.Page <= 0x02, $"page 0x{d.Page:X2} differs"));
    }

    [Fact]
    public void Prep_authenticates_first_on_a_tag_the_charger_already_used()
    {
        var used = new SimulatedNtag213().WithProfile(0x01).ProtectedByCharger();

        using var harness = Harness.Processing(used);

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(
            new[] { "Password auth: OK", "Password (page 43): OK", "PACK (page 44): OK" },
            harness.Steps());
    }

    [Fact]
    public void Program_mode_refuses_to_start_without_a_template()
    {
        using var service = new BumpGoTagPrepperService(_ => null) { Mode = TagMode.Program };

        Assert.Throws<InvalidOperationException>(() => service.StartWatching(Harness.ReaderName));
    }

    [Fact]
    public void Program_mode_refuses_a_template_with_no_profile_pages()
    {
        var template = new TagImage();
        template.SetPage(0x00, new byte[] { 0x04, 0x11, 0x22, 0x88 });

        using var service = new BumpGoTagPrepperService(_ => null) { Mode = TagMode.Program, Template = template };

        Assert.Throws<InvalidOperationException>(() => service.StartWatching(Harness.ReaderName));
    }
}
