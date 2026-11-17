// BumpGo Tag Prepper — Console front end.
//
// All the actual NFC / NTAG213 logic lives in BumpGoPrep.Core. This project
// just parses the command line, subscribes to the service's events and prints
// them. A future WinForms front end can reference BumpGoPrep.Core the same way
// and subscribe to the same events, without duplicating any logic.
//
//   BumpGoPrep.Console                         prep blank tags (default)
//   BumpGoPrep.Console dump [--out <dir>]      save every tag's memory to a file
//   BumpGoPrep.Console program <dump-file> [--with-protection]
//                                              copy a saved profile onto tags
//   BumpGoPrep.Console diff <file> <file> [...]
//                                              show which bytes differ between dumps
//   BumpGoPrep.Console create --type <t> --cells <n> [options]
//                                              build a profile from settings and program tags
//   BumpGoPrep.Console decode <dump-file> [...]
//                                              show the charge profile held in dumps

using System.Reflection;
using BumpGoPrep.Core;

const int LabelWidth = 20;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "prep";

if (command is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

if (command == "diff")
    return RunDiff(args.Skip(1).ToArray());

if (command == "decode")
    return RunDecode(args.Skip(1).ToArray());

TagMode mode;
TagImage? template = null;
string? templatePath = null;
var copyProtection = false;
var dumpDir = "dumps";

switch (command)
{
    case "prep":
        mode = TagMode.Prep;
        break;

    case "dump":
        mode = TagMode.Dump;
        var outIndex = Array.IndexOf(args, "--out");
        if (outIndex > 0)
        {
            if (outIndex + 1 >= args.Length)
                return UsageError("--out needs a folder name.");
            dumpDir = args[outIndex + 1];
        }
        break;

    case "program":
        mode = TagMode.Program;
        templatePath = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        if (templatePath is null)
            return UsageError("program needs the dump file to copy, e.g. program dumps/04-A1-B2-C3-D4-E5-F6_20261117-090000.txt");
        if (!File.Exists(templatePath))
            return UsageError($"Can't find {templatePath}.");

        template = TagImage.Load(templatePath);
        if (!template.UserPages.Any())
            return UsageError($"{templatePath} has no user-memory pages (04-27) in it.");

        copyProtection = args.Contains("--with-protection");
        break;

    case "create":
    {
        BumpGoProfile profile;
        try
        {
            profile = BumpGoProfile.Create(ParseProfileSettings(args.Skip(1).ToArray()));
        }
        catch (ArgumentException ex)
        {
            return UsageError(ex.Message);
        }

        WriteLine("Profile:", ConsoleColor.White);
        foreach (var line in profile.Describe())
            WriteLine($"  {line}");
        WriteLine($"  Bytes            {BitConverter.ToString(profile.ToBytes()).Replace('-', ' ')}", ConsoleColor.DarkGray);
        WriteLine();

        template = profile.ToImage();

        var savePath = OptionValue(args, "--save");
        if (savePath is not null)
        {
            File.WriteAllText(savePath, $"# Created: {profile.Type} {profile.Cells}S {profile.ModeName}\n" + template.ToText());
            WriteLine($"Saved {Path.GetFullPath(savePath)}. Program tags with:  program {savePath}", ConsoleColor.Green);
            return 0;
        }

        mode = TagMode.Program;
        templatePath = $"\"{profile.Label.Trim()} {profile.ModeName}\"";
        break;
    }

    default:
        return UsageError($"Unknown command '{args[0]}'.");
}

var version = Assembly.GetExecutingAssembly().GetName().Version;
var built = File.GetLastWriteTime(Environment.ProcessPath!);

WriteLine("BumpGo Tag Prepper", ConsoleColor.White);
WriteLine("Prepares and programs NTAG213 tags for the SkyRC D200neo+.");
WriteLine($"build {version} (built {built:yyyy-MM-dd HH:mm:ss})", ConsoleColor.DarkGray);
WriteLine();

using var service = new BumpGoTagPrepperService
{
    Mode = mode,
    Template = template,
    CopyProtectionConfig = copyProtection,
};

var readers = service.ListReaders();
if (readers.Count == 0)
{
    WriteLine("No PC/SC readers found. Is the ACR122U plugged in and are its drivers installed?",
        ConsoleColor.Red);
    return 1;
}

// Prefer the ACR122U if other readers (smartcard slots, virtual readers) are present.
var readerName = readers.FirstOrDefault(r => r.Contains("ACR122", StringComparison.OrdinalIgnoreCase))
    ?? readers[0];

WriteLine($"Reader: {readerName}");
foreach (var other in readers.Where(r => r != readerName))
    WriteLine($"        (ignoring {other})", ConsoleColor.DarkGray);

var (doneVerb, ready) = mode switch
{
    TagMode.Dump => ("dumped", $"Dump mode - each tag is read and saved to {Path.GetFullPath(dumpDir)}. Nothing is written."),
    TagMode.Program => ("programmed", $"Program mode - copying the profile in {templatePath} ({template!.UserPages.Count()} pages)"
                                     + (copyProtection ? " and its password protection." : ".")),
    _ => ("prepped", "Prep mode - place a blank NTAG213 on the reader."),
};

WriteLine(ready, ConsoleColor.Cyan);
if (mode == TagMode.Program)
{
    foreach (var line in template!.DescribeConfig())
        WriteLine($"  template {line}", ConsoleColor.DarkGray);
}
WriteLine("Ctrl+C to quit.", ConsoleColor.Cyan);
WriteLine();

var attempted = 0;
var failed = 0;

service.Log += (_, e) => WriteLine($"    {e.Message}", ConsoleColor.DarkYellow);

service.TagDetected += (_, e) =>
{
    attempted++;
    WriteLine($"[{attempted}] {e.Uid ?? "unknown UID"}", ConsoleColor.White);
};

service.StepCompleted += (_, e) =>
{
    var label = e.StepLabel.PadRight(LabelWidth);
    if (e.Success)
    {
        WriteLine($"    {label}OK  {e.Detail}", ConsoleColor.Green);
    }
    else
    {
        WriteLine($"    {label}FAILED", ConsoleColor.Red);
        WriteLine($"    {new string(' ', LabelWidth)}{e.Detail}", ConsoleColor.DarkGray);
    }
};

service.TagRead += (_, e) =>
{
    Directory.CreateDirectory(dumpDir);
    var now = DateTime.Now;
    var path = Path.Combine(dumpDir, $"{e.Uid}_{now:yyyyMMdd-HHmmss}.txt");
    File.WriteAllText(path, e.Image.ToText(now));

    foreach (var line in e.Image.DescribeConfig())
        WriteLine($"    {line}", ConsoleColor.DarkGray);

    if (BumpGoProfile.TryFromImage(e.Image, out var profile, out string? _))
    {
        foreach (var line in profile!.Describe())
            WriteLine($"    {line}", ConsoleColor.Cyan);
    }
    else
    {
        WriteLine("    no BumpGo profile on this tag", ConsoleColor.DarkGray);
    }

    var used = e.Image.UserPages.Count(p => e.Image[p]!.Any(b => b != 0));
    WriteLine($"    {used} of 36 user pages hold data", ConsoleColor.DarkGray);
    WriteLine($"    saved {path}", ConsoleColor.Gray);
};

service.TagPrepped += (_, e) =>
{
    if (e.Success)
    {
        WriteLine($"    {doneVerb} - {e.TotalPreppedCount} done. Remove the tag and place the next one.",
            ConsoleColor.Green);
    }
    else
    {
        failed++;
        WriteLine($"    not {doneVerb} - lift the tag off and place it back to retry.", ConsoleColor.Red);
    }

    WriteLine();
};

// Ctrl+C stops the watch loop cleanly instead of killing the process mid-write.
var exitSignal = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitSignal.Set();
};

service.StartWatching(readerName);
exitSignal.Wait();

service.StopWatching();

WriteLine();
WriteLine($"Stopped. {service.PreppedCount} tag(s) {doneVerb}"
    + (failed > 0 ? $", {failed} failed." : "."), ConsoleColor.White);

return failed > 0 ? 1 : 0;

static int RunDiff(string[] files)
{
    if (files.Length < 2)
        return UsageError("diff needs at least two dump files.");

    foreach (var f in files.Where(f => !File.Exists(f)))
        return UsageError($"Can't find {f}.");

    var images = files.Select(TagImage.Load).ToArray();
    var names = files.Select(Path.GetFileNameWithoutExtension).ToArray();
    var differences = TagImage.Compare(images);

    for (var i = 0; i < files.Length; i++)
        WriteLine($"  [{i + 1}] {names[i]}", ConsoleColor.Gray);
    WriteLine();

    if (differences.Count == 0)
    {
        WriteLine("The dumps are identical.", ConsoleColor.Green);
        return 0;
    }

    WriteLine("page  " + string.Join("  ", Enumerable.Range(1, files.Length).Select(i => $"[{i}]".PadRight(11))));
    foreach (var d in differences)
    {
        var cells = d.Values.Select(v => v is null ? "-- -- -- --" : BitConverter.ToString(v).Replace('-', ' '));
        WriteLine($" {d.Page:X2}   {string.Join("  ", cells)}   {TagImage.RegionOf(d.Page)}");

        // Mark which byte positions changed, under the first column.
        var marks = string.Concat(d.ChangedBytes.Select(c => c ? "^^ " : "   "));
        WriteLine($"      {marks}", ConsoleColor.Yellow);
    }

    return 0;
}

static int RunDecode(string[] files)
{
    if (files.Length == 0)
        return UsageError("decode needs at least one dump file.");

    var failed = 0;
    foreach (var file in files)
    {
        WriteLine(file, ConsoleColor.White);
        if (!File.Exists(file))
        {
            WriteLine("  can't find this file", ConsoleColor.Red);
            failed++;
            continue;
        }

        if (BumpGoProfile.TryFromImage(TagImage.Load(file), out var profile, out var error))
        {
            foreach (var line in profile!.Describe())
                WriteLine($"  {line}");
        }
        else
        {
            WriteLine($"  {error}", ConsoleColor.DarkGray);
        }

        WriteLine();
    }

    return failed > 0 ? 1 : 0;
}

static string? OptionValue(string[] args, string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static ProfileSettings ParseProfileSettings(string[] args)
{
    string? Opt(string name) => OptionValue(args, name);

    decimal? Dec(string name)
    {
        var v = Opt(name);
        if (v is null) return null;
        if (!decimal.TryParse(v, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            throw new ArgumentException($"{name} needs a number, got '{v}'.");
        return d;
    }

    int? Int(string name) => Dec(name) is { } d
        ? d == Math.Floor(d) ? (int)d : throw new ArgumentException($"{name} needs a whole number.")
        : null;

    var known = new[]
    {
        "--type", "--cells", "--mode", "--charge", "--discharge", "--cutoff", "--charge-voltage",
        "--storage-voltage", "--delta-v", "--trickle", "--cycles", "--order", "--save",
    };
    for (var i = 0; i < args.Length; i += 2)
    {
        if (!known.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown option '{args[i]}'.");
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{args[i]} needs a value.");
    }

    var typeText = Opt("--type") ?? throw new ArgumentException(
        "create needs --type: " + string.Join(", ", Enum.GetNames<BatteryType>()) + ".");
    if (!Enum.TryParse<BatteryType>(typeText, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        throw new ArgumentException($"Unknown battery type '{typeText}'. Use one of: {string.Join(", ", Enum.GetNames<BatteryType>())}.");

    var cells = Int("--cells") ?? throw new ArgumentException("create needs --cells.");

    CycleOrder? order = Opt("--order")?.ToLowerInvariant() switch
    {
        null => null,
        "cd" or "charge-discharge" => CycleOrder.ChargeThenDischarge,
        "dc" or "discharge-charge" => CycleOrder.DischargeThenCharge,
        var o => throw new ArgumentException($"--order must be cd (charge then discharge) or dc, got '{o}'."),
    };

    return new ProfileSettings
    {
        Type = type,
        Cells = cells,
        Mode = Opt("--mode"),
        ChargeAmps = Dec("--charge"),
        DischargeAmps = Dec("--discharge"),
        CutoffVolts = Dec("--cutoff"),
        ChargeVolts = Dec("--charge-voltage"),
        StorageVolts = Dec("--storage-voltage"),
        DeltaVMillivolts = Int("--delta-v"),
        TrickleMilliamps = Int("--trickle"),
        Cycles = Int("--cycles"),
        Order = order,
    };
}

static int UsageError(string message)
{
    WriteLine(message, ConsoleColor.Red);
    WriteLine();
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    WriteLine("Usage:");
    WriteLine("  BumpGoPrep.Console [prep]                     prep blank tags for the charger (default)");
    WriteLine("  BumpGoPrep.Console dump [--out <folder>]      read tags and save each to a text file");
    WriteLine("  BumpGoPrep.Console program <dump-file> [--with-protection]");
    WriteLine("                                                copy the profile from a dump onto tags");
    WriteLine("  BumpGoPrep.Console diff <file> <file> [...]   show which bytes differ between dumps");
    WriteLine("  BumpGoPrep.Console decode <file> [...]        show the charge profile held in dumps");
    WriteLine("  BumpGoPrep.Console create --type <type> --cells <n> [options]");
    WriteLine("                                                build a profile from settings and program tags");
    WriteLine();
    WriteLine("create options (anything left out uses the charger's default):");
    WriteLine("  --type             LiPo, LiIon, LiFe, LiHV, NiMH, NiCd, Pb");
    WriteLine("  --mode             lithium: balance, charge, discharge, storage");
    WriteLine("                     NiMH/NiCd: charge, discharge, repeak, cycle");
    WriteLine("                     Pb: charge (normal), discharge, agm, cold          (default: charge)");
    WriteLine("  --charge <A>       charge current, 0.1-20.0                           (default 2.0)");
    WriteLine("  --discharge <A>    discharge current, 0.1-2.0                         (default 2.0)");
    WriteLine("  --charge-voltage   LiPo only, 4.00-4.25 V per cell                    (default 4.20)");
    WriteLine("  --storage-voltage  LiPo storage mode only, 3.80-3.90 V                (default 3.85)");
    WriteLine("  --cutoff <V>       LiPo discharge cut-off, 3.00-3.40 V                (default 3.30)");
    WriteLine("  --delta-v <mV>     NiMH/NiCd, 3-15                                    (default 6)");
    WriteLine("  --trickle <mA>     NiMH/NiCd, 0-200                                   (default 0 = off)");
    WriteLine("  --cycles <n>       cycle/re-peak count, 0-5");
    WriteLine("  --order cd|dc      NiMH/NiCd cycle: charge>discharge or discharge>charge");
    WriteLine("  --save <file>      write the profile to a file instead of programming tags");
}

static void WriteLine(string text = "", ConsoleColor? color = null)
{
    if (color is null)
    {
        Console.WriteLine(text);
        return;
    }

    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color.Value;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}
