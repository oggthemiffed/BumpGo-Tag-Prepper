// BumpGo Tag Prepper — Console front end.
//
// All the actual NFC / NTAG213 logic lives in BumpGoPrep.Core. This project
// just subscribes to its events and prints them. A future WinForms front end
// can reference BumpGoPrep.Core the same way and subscribe to the same
// events, without touching this file or duplicating any logic.

using System.Reflection;
using BumpGoPrep.Core;

const int LabelWidth = 20;

var version = Assembly.GetExecutingAssembly().GetName().Version;
var built = File.GetLastWriteTime(Environment.ProcessPath!);

WriteLine("BumpGo Tag Prepper", ConsoleColor.White);
WriteLine("Prepares blank NTAG213 tags for the SkyRC D200neo+.");
WriteLine($"build {version} (built {built:yyyy-MM-dd HH:mm:ss})", ConsoleColor.DarkGray);
WriteLine();

using var service = new BumpGoTagPrepperService();

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

WriteLine("Ready - place a blank NTAG213 on the reader. Ctrl+C to quit.", ConsoleColor.Cyan);
WriteLine();

var attempted = 0;
var failed = 0;

service.Log += (_, e) => WriteLine(e.Message, ConsoleColor.DarkGray);

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
        WriteLine($"    {label}OK", ConsoleColor.Green);
    }
    else
    {
        WriteLine($"    {label}FAILED", ConsoleColor.Red);
        WriteLine($"    {new string(' ', LabelWidth)}{e.Detail}", ConsoleColor.DarkGray);
    }
};

service.TagPrepped += (_, e) =>
{
    if (e.Success)
    {
        WriteLine($"    prepped - {e.TotalPreppedCount} done. Remove the tag and place the next one.",
            ConsoleColor.Green);
    }
    else
    {
        failed++;
        WriteLine("    not prepped - lift the tag off and place it back to retry.", ConsoleColor.Red);
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
WriteLine($"Stopped. {service.PreppedCount} tag(s) prepped"
    + (failed > 0 ? $", {failed} failed." : "."), ConsoleColor.White);

return failed > 0 ? 1 : 0;

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
