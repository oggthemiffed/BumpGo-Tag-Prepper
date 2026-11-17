using System.Text;
using BumpGoPrep.Core;

namespace BumpGoPrep.Tests;

/// <summary>
/// Every expected byte string here was read off a tag the D200neo+ itself
/// saved (sample IDs from the test plans in brackets), so these tests check
/// that a profile built from settings matches what the charger writes.
/// </summary>
public class BumpGoProfileTests
{
    private static byte[] Tag(string label, string hex) =>
        Encoding.ASCII.GetBytes(label).Concat(Convert.FromHexString(hex.Replace(" ", ""))).ToArray();

    public static IEnumerable<object[]> ChargerSaved()
    {
        // [A02] LiPo 2S charge 5.0 A, all defaults.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "charge", ChargeAmps = 5.0m },
            Tag(" LiPo 2S", "00 02 01 13 88 07 d0 0c e4 10 68 00 00 00 00 0a 00 00 00 a5"),
        };
        // [C10] 12.8 A: the high byte of the current is a full byte, unsigned.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "charge", ChargeAmps = 12.8m },
            Tag(" LiPo 2S", "00 02 01 32 00 07 d0 0c e4 10 68 00 00 00 00 0a 00 00 00 a5"),
        };
        // [B04] 6S.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 6, Mode = "charge", ChargeAmps = 5.0m },
            Tag(" LiPo 6S", "00 06 01 13 88 07 d0 0c e4 10 68 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-B01] LiPo discharge, cut-off 3.00 V, charge voltage 4.25 V carried over.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "discharge", ChargeAmps = 5.0m, CutoffVolts = 3.00m, ChargeVolts = 4.25m },
            Tag(" LiPo 2S", "00 02 02 13 88 07 d0 0b b8 10 9a 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-B03] LiPo storage at 3.80 V, cut-off 3.40 V.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "storage", ChargeAmps = 5.0m, CutoffVolts = 3.40m, StorageVolts = 3.80m },
            Tag(" LiPo 2S", "00 02 03 13 88 07 d0 0d 48 0e d8 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-A02] LiPo charge voltage 4.00 V, 1.0 A discharge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "charge", ChargeAmps = 5.0m, DischargeAmps = 1.0m, ChargeVolts = 4.00m },
            Tag(" LiPo 2S", "00 02 01 13 88 03 e8 0c e4 0f a0 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-D02] LiPo balance charge with a cycle count of 2.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "balance", ChargeAmps = 5.0m, DischargeAmps = 1.0m, CutoffVolts = 3.40m, ChargeVolts = 4.25m, Cycles = 2 },
            Tag(" LiPo 2S", "00 02 00 13 88 03 e8 0d 48 10 9a 00 02 00 00 0a 00 00 00 a5"),
        };
        // [P2-C01] LiHV balance charge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiHV, Cells = 2, Mode = "balance", ChargeAmps = 5.0m },
            Tag(" LiHV 2S", "03 02 00 13 88 07 d0 0d 48 10 fe 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C04] LiHV storage.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiHV, Cells = 2, Mode = "storage", ChargeAmps = 5.0m },
            Tag(" LiHV 2S", "03 02 03 13 88 07 d0 0d 48 0f 3c 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C08] LiIon storage: label abbreviates to "LiIo".
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiIon, Cells = 2, Mode = "storage", ChargeAmps = 5.0m },
            Tag(" LiIo 2S", "01 02 03 13 88 07 d0 0c 80 0e d8 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C06] LiIon charge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiIon, Cells = 2, Mode = "charge", ChargeAmps = 5.0m },
            Tag(" LiIo 2S", "01 02 01 13 88 07 d0 0c 80 10 04 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C10] LiFe charge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiFe, Cells = 2, Mode = "charge", ChargeAmps = 5.0m },
            Tag(" LiFe 2S", "02 02 01 13 88 07 d0 0b 54 0e 42 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C12] LiFe storage.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.LiFe, Cells = 2, Mode = "storage", ChargeAmps = 5.0m },
            Tag(" LiFe 2S", "02 02 03 13 88 07 d0 0b 54 0c e4 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C14] NiMH discharge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "discharge", ChargeAmps = 5.0m },
            Tag(" NiMH 6S", "04 06 02 13 88 07 d0 03 84 00 06 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C16] NiMH cycle, discharge first, once.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "cycle", ChargeAmps = 5.0m, Order = CycleOrder.DischargeThenCharge },
            Tag(" NiMH 6S", "04 06 04 13 88 07 d0 03 84 00 06 01 01 00 00 0a 00 00 00 a5"),
        };
        // [P2-C16.1] NiMH re-peak.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "repeak", ChargeAmps = 2.0m },
            Tag(" NiMH 6S", "04 06 03 07 d0 07 d0 03 84 00 06 00 01 00 00 0a 00 00 00 a5"),
        };
        // [P2-C19] NiCd cycle, charge first.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.NiCd, Cells = 6, Mode = "cycle", ChargeAmps = 2.0m },
            Tag(" NiCd 6S", "05 06 04 07 d0 07 d0 03 84 00 06 00 01 00 00 0a 00 00 00 a5"),
        };
        // [P2-F02] NiMH 15 cells, -dV 12 mV, 100 mA trickle: 2-digit label.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.NiMH, Cells = 15, Mode = "charge", ChargeAmps = 5.0m, DischargeAmps = 1.0m, DeltaVMillivolts = 12, TrickleMilliamps = 100 },
            Tag("NiMH 15S", "04 0f 00 13 88 03 e8 03 84 00 0c 00 00 00 64 0a 00 00 00 a5"),
        };
        // [P2-F03] Pb 6 cells, normal charge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.Pb, Cells = 6, Mode = "normal", ChargeAmps = 5.0m, DischargeAmps = 1.0m },
            Tag("   PB 6S", "06 06 00 13 88 03 e8 07 6c 09 60 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C20] Pb AGM charge: 2.45 V per cell.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.Pb, Cells = 3, Mode = "agm", ChargeAmps = 2.0m },
            Tag("   PB 3S", "06 03 02 07 d0 07 d0 07 6c 09 92 00 00 00 00 0a 00 00 00 a5"),
        };
        // [P2-C22] Pb cold charge.
        yield return new object[]
        {
            new ProfileSettings { Type = BatteryType.Pb, Cells = 3, Mode = "cold", ChargeAmps = 2.0m },
            Tag("   PB 3S", "06 03 03 07 d0 07 d0 07 6c 09 92 00 00 00 00 0a 00 00 00 a5"),
        };
    }

    [Theory]
    [MemberData(nameof(ChargerSaved))]
    public void A_profile_built_from_settings_matches_what_the_charger_saved(ProfileSettings settings, byte[] saved)
    {
        Assert.Equal(saved, BumpGoProfile.Create(settings).ToBytes());
    }

    [Theory]
    [MemberData(nameof(ChargerSaved))]
    public void Every_charger_saved_profile_decodes_and_re_encodes_identically(ProfileSettings settings, byte[] saved)
    {
        Assert.True(BumpGoProfile.TryParse(saved, out var profile, out _));
        Assert.Equal(settings.Type, profile!.Type);
        Assert.Equal(saved, profile.ToBytes());
    }

    [Fact]
    public void The_factory_profile_on_a_new_skyrc_tag_decodes()
    {
        // [R01] What SkyRC ship on their own tags.
        var saved = Tag(" LiPo 2S", "00 02 00 27 10 03 e8 0c e4 10 68 00 00 00 00 0a 00 00 00 a5");

        Assert.True(BumpGoProfile.TryParse(saved, out var p, out _));
        Assert.Equal("Balance Charge", p!.ModeName);
        Assert.Equal(10_000, p.ChargeCurrentMa);
        Assert.Equal(1_000, p.DischargeCurrentMa);
    }

    [Fact]
    public void A_blank_tag_is_not_mistaken_for_a_profile()
    {
        // [R02] Factory NDEF header on a blank NTAG213.
        var image = new TagImage();
        image.SetPage(0x04, new byte[] { 0x01, 0x03, 0xA0, 0x0C });
        image.SetPage(0x05, new byte[] { 0x34, 0x03, 0x00, 0xFE });
        for (byte p = 0x06; p <= 0x0A; p++) image.SetPage(p, new byte[4]);

        Assert.False(BumpGoProfile.TryFromImage(image, out _, out var error));
        Assert.Contains("no BumpGo profile", error!);
    }

    [Fact]
    public void The_tag_image_holds_the_profile_then_zeros_like_the_charger_leaves_it()
    {
        var image = BumpGoProfile.Create(new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, ChargeAmps = 5.0m }).ToImage();

        Assert.Equal(36, image.UserPages.Count());
        Assert.Equal(new byte[] { 0x20, 0x4C, 0x69, 0x50 }, image[0x04]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xA5 }, image[0x0A]);
        Assert.Equal(new byte[4], image[0x0B]);
        Assert.Equal(new byte[4], image[0x27]);
        Assert.True(BumpGoProfile.TryFromImage(image, out var back, out _));
        Assert.Equal(5_000, back!.ChargeCurrentMa);
    }

    [Fact]
    public void A_created_profile_programs_onto_a_tag_and_reads_back()
    {
        var image = BumpGoProfile.Create(new ProfileSettings
        {
            Type = BatteryType.LiPo, Cells = 3, ChargeAmps = 3.3m, ChargeVolts = 4.15m,
        }).ToImage();
        var blank = new SimulatedNtag213();

        using var harness = Harness.Processing(blank, s =>
        {
            s.Mode = TagMode.Program;
            s.Template = image;
        });

        Assert.Equal(new[] { true }, harness.PrepResults());
        Assert.Equal(new byte[] { 0x6F, 0x20, 0x33, 0x53 }, blank.Page(0x05)); // "o 3S"
        Assert.Equal(new byte[] { 0x00, 0x03, 0x01, 0x0C }, blank.Page(0x06)); // LiPo, 3 cells, charge, 3.3 A high byte
        Assert.Equal(new byte[] { 0xE4, 0x10, 0x36, 0x00 }, blank.Page(0x08)); // 4.15 V
    }

    public static IEnumerable<object[]> Rejected() => new[]
    {
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 7 }, "cells" },
        new object[] { new ProfileSettings { Type = BatteryType.NiMH, Cells = 16 }, "cells" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, ChargeAmps = 20.1m }, "Charge current" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, ChargeAmps = 1.25m }, "0.1 A steps" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, DischargeAmps = 2.5m }, "Discharge current" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, ChargeVolts = 4.30m }, "Charge voltage" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, ChargeVolts = 3.95m }, "Charge voltage" },
        new object[] { new ProfileSettings { Type = BatteryType.LiHV, Cells = 2, ChargeVolts = 4.30m }, "only been verified for LiPo" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, CutoffVolts = 3.50m }, "cut-off" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "storage", ChargeVolts = 4.20m }, "storage" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, StorageVolts = 3.85m }, "storage mode" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Mode = "cycle" }, "no mode" },
        new object[] { new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "storage" }, "no mode" },
        new object[] { new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, ChargeVolts = 1.5m }, "--delta-v" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, DeltaVMillivolts = 6 }, "-dV" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, TrickleMilliamps = 50 }, "Trickle" },
        new object[] { new ProfileSettings { Type = BatteryType.LiPo, Cells = 2, Order = CycleOrder.DischargeThenCharge }, "Cycle order" },
        new object[] { new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "cycle", Cycles = 0 }, "at least 1" },
        new object[] { new ProfileSettings { Type = BatteryType.NiMH, Cells = 6, Mode = "charge", Cycles = 2 }, "cycle count" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Settings_outside_what_the_charger_allows_are_refused(ProfileSettings settings, string messagePart)
    {
        var ex = Assert.Throws<ArgumentException>(() => BumpGoProfile.Create(settings));
        Assert.Contains(messagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_profile_with_the_wrong_end_marker_is_rejected()
    {
        var bytes = BumpGoProfile.Create(new ProfileSettings { Type = BatteryType.LiPo, Cells = 2 }).ToBytes();
        bytes[27] = 0x00;

        Assert.False(BumpGoProfile.TryParse(bytes, out _, out var error));
        Assert.Contains("byte 27", error!);
    }
}
