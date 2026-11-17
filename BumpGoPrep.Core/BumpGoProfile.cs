using System.Globalization;
using System.Text;

namespace BumpGoPrep.Core;

/// <summary>Battery type, as stored in byte 8 of a BumpGo profile.</summary>
public enum BatteryType : byte
{
    LiPo = 0,
    LiIon = 1,
    LiFe = 2,
    LiHV = 3,
    NiMH = 4,
    NiCd = 5,
    Pb = 6,
}

/// <summary>Order of a NiMH/NiCd cycle, as stored in byte 19.</summary>
public enum CycleOrder : byte
{
    ChargeThenDischarge = 0,
    DischargeThenCharge = 1,
}

/// <summary>
/// The 28-byte charge profile the SkyRC D200neo+ stores on a BumpGo tag,
/// in user pages 0x04-0x0A. Worked out from tags saved on the charger with
/// one setting changed at a time; see docs/bumpgo-format.md.
/// </summary>
/// <remarks>
/// Layout (multi-byte values big-endian):
/// <code>
///  0-7   label, 8 ASCII chars, right-aligned (" LiPo 2S", "NiMH 10S", "   PB 6S")
///  8     battery type
///  9     cells
///  10    mode (numbering depends on battery type)
///  11-12 charge current, mA
///  13-14 discharge current, mA
///  15-16 discharge cut-off per cell, mV
///  17-18 target: charge or storage voltage per cell in mV (lithium, lead),
///        or -dV sensitivity in mV (NiMH/NiCd)
///  19    cycle order (NiMH/NiCd cycle)
///  20    cycle / re-peak count
///  21-22 trickle current, mA (NiMH/NiCd)
///  23    always 0x0A on every tag seen - probably the 10-minute rest time
///  24-26 zero
///  27    0xA5 end marker
/// </code>
/// No checksum and nothing tied to the tag UID: a profile copied to
/// another tag is accepted by the charger.
/// </remarks>
public sealed class BumpGoProfile
{
    public const int Length = 28;
    public const byte EndMarker = 0xA5;
    public const byte DefaultByte23 = 0x0A;

    /// <summary>Number of tag pages the profile occupies (0x04-0x0A).</summary>
    public const int PageCount = Length / 4;

    public BatteryType Type { get; init; }
    public int Cells { get; init; }
    public byte Mode { get; init; }
    public int ChargeCurrentMa { get; init; }
    public int DischargeCurrentMa { get; init; }
    public int DischargeCutoffMv { get; init; }

    /// <summary>
    /// Bytes 17-18. Charge voltage per cell (mV) in lithium charge modes and
    /// for lead; storage voltage in lithium storage mode; -dV in mV for NiMH/NiCd.
    /// </summary>
    public int Target { get; init; }

    public CycleOrder CycleOrder { get; init; }
    public int CycleCount { get; init; }
    public int TrickleCurrentMa { get; init; }
    public byte Byte23 { get; init; } = DefaultByte23;

    public static bool IsLithium(BatteryType t) => t is BatteryType.LiPo or BatteryType.LiIon or BatteryType.LiFe or BatteryType.LiHV;
    public static bool IsNickel(BatteryType t) => t is BatteryType.NiMH or BatteryType.NiCd;

    /// <summary>The label the charger writes: type name in 4 chars, a space, cells + "S", right-aligned in 8.</summary>
    public string Label
    {
        get
        {
            var name = Type switch
            {
                BatteryType.LiIon => "LiIo",
                BatteryType.Pb => "PB",
                _ => Type.ToString(),
            };
            return $"{name.PadLeft(4)} {Cells}S".PadLeft(8);
        }
    }

    public string ModeName => ModeNameFor(Type, Mode);

    // ---------------------------------------------------------------- modes

    private static readonly (string Key, byte Value, string Name)[] LithiumModes =
    {
        ("balance", 0, "Balance Charge"), ("charge", 1, "Charge"), ("discharge", 2, "Discharge"), ("storage", 3, "Storage"),
    };

    private static readonly (string Key, byte Value, string Name)[] NickelModes =
    {
        ("charge", 0, "Charge"), ("discharge", 2, "Discharge"), ("repeak", 3, "Re-Peak"), ("cycle", 4, "Cycle"),
    };

    private static readonly (string Key, byte Value, string Name)[] LeadModes =
    {
        ("charge", 0, "Charge (Normal)"), ("discharge", 1, "Discharge"), ("agm", 2, "AGM Charge"), ("cold", 3, "Cold Charge"),
    };

    private static (string Key, byte Value, string Name)[] ModesOf(BatteryType t) =>
        IsLithium(t) ? LithiumModes : IsNickel(t) ? NickelModes : LeadModes;

    /// <summary>The mode keywords accepted for a battery type, e.g. "balance", "charge".</summary>
    public static IReadOnlyList<string> ModeKeysFor(BatteryType t) => ModesOf(t).Select(m => m.Key).ToArray();

    public static string ModeNameFor(BatteryType t, byte mode) =>
        ModesOf(t).FirstOrDefault(m => m.Value == mode).Name ?? $"unknown mode {mode}";

    private bool IsStorage => IsLithium(Type) && Mode == 3;
    private bool IsCycle => IsNickel(Type) && Mode == 4;
    private bool IsRepeak => IsNickel(Type) && Mode == 3;

    // ------------------------------------------------------------ encoding

    public byte[] ToBytes()
    {
        var b = new byte[Length];
        Encoding.ASCII.GetBytes(Label).CopyTo(b, 0);
        b[8] = (byte)Type;
        b[9] = (byte)Cells;
        b[10] = Mode;
        PutU16(b, 11, ChargeCurrentMa);
        PutU16(b, 13, DischargeCurrentMa);
        PutU16(b, 15, DischargeCutoffMv);
        PutU16(b, 17, Target);
        b[19] = (byte)CycleOrder;
        b[20] = (byte)CycleCount;
        PutU16(b, 21, TrickleCurrentMa);
        b[23] = Byte23;
        b[27] = EndMarker;
        return b;
    }

    /// <summary>
    /// A tag image holding this profile in pages 0x04-0x0A and zeros in the
    /// rest of user memory, exactly as the charger leaves a tag. Use it as
    /// the template for <see cref="TagMode.Program"/>.
    /// </summary>
    public TagImage ToImage()
    {
        var bytes = ToBytes();
        var image = new TagImage();
        for (var page = NfcCommands.UserFirstPage; page <= NfcCommands.UserLastPage; page++)
        {
            var offset = (page - NfcCommands.UserFirstPage) * 4;
            image.SetPage(page, offset < Length ? bytes[offset..(offset + 4)] : new byte[4]);
        }

        return image;
    }

    public static bool TryParse(byte[] data, out BumpGoProfile? profile, out string? error)
    {
        profile = null;
        error = null;

        if (data.Length < Length)
        {
            error = $"a profile is {Length} bytes, got {data.Length}";
            return false;
        }

        if (data[27] != EndMarker)
        {
            error = $"no BumpGo profile (byte 27 is 0x{data[27]:X2}, expected 0x{EndMarker:X2})";
            return false;
        }

        if (data[8] > (byte)BatteryType.Pb)
        {
            error = $"unknown battery type {data[8]}";
            return false;
        }

        profile = new BumpGoProfile
        {
            Type = (BatteryType)data[8],
            Cells = data[9],
            Mode = data[10],
            ChargeCurrentMa = U16(data, 11),
            DischargeCurrentMa = U16(data, 13),
            DischargeCutoffMv = U16(data, 15),
            Target = U16(data, 17),
            CycleOrder = (CycleOrder)data[19],
            CycleCount = data[20],
            TrickleCurrentMa = U16(data, 21),
            Byte23 = data[23],
        };
        return true;
    }

    /// <summary>Reads the profile out of a tag image (pages 0x04-0x0A), or explains why it can't.</summary>
    public static bool TryFromImage(TagImage image, out BumpGoProfile? profile, out string? error)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < PageCount; i++)
        {
            var page = image[(byte)(NfcCommands.UserFirstPage + i)];
            if (page is null)
            {
                profile = null;
                error = $"the image has no page 0x{NfcCommands.UserFirstPage + i:X2}";
                return false;
            }

            bytes.AddRange(page);
        }

        return TryParse(bytes.ToArray(), out profile, out error);
    }

    /// <summary>Human-readable summary, one setting per line.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Battery          {Type} {Cells}S   (label \"{Label.Trim()}\")";
        yield return $"Mode             {ModeName}";
        yield return $"Charge current   {Amps(ChargeCurrentMa)}";
        yield return $"Discharge        {Amps(DischargeCurrentMa)}, cut-off {Volts(DischargeCutoffMv)}/cell";

        if (IsNickel(Type))
        {
            yield return $"-dV              {Target} mV";
            yield return $"Trickle          {(TrickleCurrentMa == 0 ? "off" : $"{TrickleCurrentMa} mA")}";
            if (IsCycle)
                yield return $"Cycle            {CycleCount} x, {(CycleOrder == CycleOrder.ChargeThenDischarge ? "charge > discharge" : "discharge > charge")}";
            if (IsRepeak)
                yield return $"Re-peak count    {CycleCount}";
        }
        else
        {
            yield return IsStorage
                ? $"Storage voltage  {Volts(Target)}/cell"
                : $"Charge voltage   {Volts(Target)}/cell";
            if (CycleCount != 0)
                yield return $"Cycle count      {CycleCount}";
        }

        if (Byte23 != DefaultByte23)
            yield return $"Byte 23          0x{Byte23:X2} (normally 0x0A)";
    }

    // ------------------------------------------------------------ building

    /// <summary>
    /// Builds a profile from settings, filling in the charger's defaults for
    /// anything not given and rejecting values outside what has been seen on
    /// the D200neo+. Throws <see cref="ArgumentException"/> with a message
    /// meant for the user.
    /// </summary>
    public static BumpGoProfile Create(ProfileSettings s)
    {
        var type = s.Type;
        var modes = ModesOf(type);
        var modeKey = (s.Mode ?? "charge").Trim().ToLowerInvariant();
        if (type == BatteryType.Pb && modeKey == "normal") modeKey = "charge";
        var mode = modes.FirstOrDefault(m => m.Key == modeKey);
        if (mode.Name is null)
            throw new ArgumentException(
                $"{type} has no mode '{s.Mode}'. Choose one of: {string.Join(", ", modes.Select(m => m.Key))}.");

        var (minCells, maxCells) = IsLithium(type) ? (1, 6) : IsNickel(type) ? (1, 15) : (1, 6);
        Require(s.Cells >= minCells && s.Cells <= maxCells, $"{type} cells must be {minCells}-{maxCells}.");

        var charge = MilliampsOrDefault(s.ChargeAmps, 2000, "Charge current", 100, 20_000);
        var discharge = MilliampsOrDefault(s.DischargeAmps, 2000, "Discharge current", 100, 2_000);

        var d = Defaults(type);
        var storageMode = IsLithium(type) && mode.Value == 3;

        // Discharge cut-off.
        var cutoff = d.Cutoff;
        if (s.CutoffVolts is { } cv)
        {
            Require(type == BatteryType.LiPo, $"Changing the discharge cut-off has only been verified for LiPo; {type} uses its default.");
            cutoff = Millivolts(cv, "Discharge cut-off", 3000, 3400);
        }

        // Target field: charge voltage, storage voltage, or -dV.
        int target;
        if (IsNickel(type))
        {
            Require(s.ChargeVolts is null && s.StorageVolts is null, $"{type} has no charge or storage voltage setting; use --delta-v.");
            target = s.DeltaVMillivolts ?? 6;
            Require(target is >= 3 and <= 15, "-dV must be 3-15 mV.");
        }
        else
        {
            Require(s.DeltaVMillivolts is null, "-dV only applies to NiMH and NiCd.");

            if (storageMode)
            {
                Require(s.ChargeVolts is null, "In storage mode the tag holds the storage voltage, not a charge voltage; use --storage-voltage.");
                target = d.Storage;
                if (s.StorageVolts is { } sv)
                {
                    Require(type == BatteryType.LiPo, $"Changing the storage voltage has only been verified for LiPo; {type} uses its default.");
                    target = Millivolts(sv, "Storage voltage", 3800, 3900);
                }
            }
            else
            {
                Require(s.StorageVolts is null, "--storage-voltage only applies in storage mode.");
                target = type == BatteryType.Pb && mode.Value is 2 or 3 ? 2450 : d.Charge;
                if (s.ChargeVolts is { } chv)
                {
                    Require(type == BatteryType.LiPo, $"Changing the charge voltage has only been verified for LiPo; {type} uses its default.");
                    target = Millivolts(chv, "Charge voltage", 4000, 4250);
                }
            }
        }

        // Trickle (NiMH/NiCd only).
        var trickle = 0;
        if (s.TrickleMilliamps is { } t)
        {
            Require(IsNickel(type), "Trickle current only applies to NiMH and NiCd.");
            Require(t is >= 0 and <= 200, "Trickle current must be 0-200 mA.");
            trickle = t;
        }

        // Cycles and order.
        var isCycle = IsNickel(type) && mode.Value == 4;
        var isRepeak = IsNickel(type) && mode.Value == 3;
        var cycles = s.Cycles ?? (isCycle || isRepeak ? 1 : 0);
        Require(cycles is >= 0 and <= 5, "Cycle count must be 0-5.");
        if (isCycle || isRepeak) Require(cycles >= 1, "Cycle and re-peak modes need a count of at least 1.");
        Require(IsLithium(type) || isCycle || isRepeak || s.Cycles is null,
            $"A cycle count only applies to lithium batteries and to {type} cycle/re-peak modes.");

        Require(s.Order is null || isCycle, "Cycle order only applies to NiMH/NiCd cycle mode.");

        return new BumpGoProfile
        {
            Type = type,
            Cells = s.Cells,
            Mode = mode.Value,
            ChargeCurrentMa = charge,
            DischargeCurrentMa = discharge,
            DischargeCutoffMv = cutoff,
            Target = target,
            CycleOrder = s.Order ?? CycleOrder.ChargeThenDischarge,
            CycleCount = cycles,
            TrickleCurrentMa = trickle,
        };
    }

    /// <summary>The charger's own defaults, as seen on freshly saved tags.</summary>
    private static (int Cutoff, int Charge, int Storage) Defaults(BatteryType t) => t switch
    {
        BatteryType.LiPo => (3300, 4200, 3850),
        BatteryType.LiIon => (3200, 4100, 3800),
        BatteryType.LiFe => (2900, 3650, 3300),
        BatteryType.LiHV => (3400, 4350, 3900),
        BatteryType.NiMH or BatteryType.NiCd => (900, 0, 0),
        _ => (1900, 2400, 0), // Pb
    };

    private static int MilliampsOrDefault(decimal? amps, int defaultMa, string what, int minMa, int maxMa)
    {
        if (amps is null) return defaultMa;
        var ma = amps.Value * 1000m;
        Require(ma % 100 == 0, $"{what} must be in 0.1 A steps.");
        Require(ma >= minMa && ma <= maxMa,
            $"{what} must be {minMa / 1000m:0.0}-{maxMa / 1000m:0.0} A.");
        return (int)ma;
    }

    private static int Millivolts(decimal volts, string what, int minMv, int maxMv)
    {
        var mv = volts * 1000m;
        Require(mv % 10 == 0, $"{what} must be in 0.01 V steps.");
        Require(mv >= minMv && mv <= maxMv, $"{what} must be {minMv / 1000m:0.00}-{maxMv / 1000m:0.00} V.");
        return (int)mv;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }

    private static string Amps(int ma) => (ma / 1000m).ToString("0.0", CultureInfo.InvariantCulture) + " A";
    private static string Volts(int mv) => (mv / 1000m).ToString("0.00", CultureInfo.InvariantCulture) + " V";

    private static void PutU16(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
    }

    private static int U16(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];
}

/// <summary>Settings for <see cref="BumpGoProfile.Create"/>. Null means "use the charger's default".</summary>
public sealed record ProfileSettings
{
    public BatteryType Type { get; init; }
    public int Cells { get; init; }

    /// <summary>Mode keyword, e.g. "balance", "charge", "storage", "cycle", "agm". Defaults to "charge".</summary>
    public string? Mode { get; init; }

    public decimal? ChargeAmps { get; init; }
    public decimal? DischargeAmps { get; init; }
    public decimal? CutoffVolts { get; init; }
    public decimal? ChargeVolts { get; init; }
    public decimal? StorageVolts { get; init; }
    public int? DeltaVMillivolts { get; init; }
    public int? TrickleMilliamps { get; init; }
    public int? Cycles { get; init; }
    public CycleOrder? Order { get; init; }
}
