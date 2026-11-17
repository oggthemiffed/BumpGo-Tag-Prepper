using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BumpGoPrep.Core;

/// <summary>
/// A page-by-page copy of an NTAG213's memory, as read off a tag or loaded
/// from a dump file. Used both to save what the charger wrote and as the
/// template that <see cref="TagMode.Program"/> copies onto blank tags.
/// </summary>
/// <remarks>
/// The text format is one page per line, <c>PP: B0 B1 B2 B3</c>, with
/// <c>#</c> comment lines. Anything after the four bytes is ignored, so the
/// ASCII column and notes in a saved dump don't get in the way, and a file
/// can be edited by hand.
/// </remarks>
public sealed partial class TagImage
{
    private readonly SortedDictionary<byte, byte[]> _pages = new();

    public TagImage(string? uid = null) => Uid = uid;

    /// <summary>UID of the tag this was read from, if known.</summary>
    public string? Uid { get; }

    /// <summary>The pages present, in address order.</summary>
    public IReadOnlyCollection<byte> PageNumbers => _pages.Keys;

    /// <summary>The 4 bytes of a page, or null if this image doesn't hold it.</summary>
    public byte[]? this[byte page] => _pages.TryGetValue(page, out var data) ? data : null;

    public void SetPage(byte page, byte[] data)
    {
        if (data.Length != 4)
            throw new ArgumentException("NTAG213 pages are 4 bytes.", nameof(data));
        _pages[page] = (byte[])data.Clone();
    }

    /// <summary>The user-memory pages (0x04-0x27) this image holds: the charger's profile data.</summary>
    public IEnumerable<byte> UserPages =>
        _pages.Keys.Where(p => p >= NfcCommands.UserFirstPage && p <= NfcCommands.UserLastPage);

    /// <summary>AUTH0 from CFG0, or null if the image has no CFG0.</summary>
    public byte? Auth0 => this[NfcCommands.Config0Page]?[NfcCommands.Auth0Index];

    /// <summary>The ACCESS byte from CFG1, or null if the image has no CFG1.</summary>
    public byte? Access => this[NfcCommands.Config1Page]?[0];

    /// <summary>Human-readable description of the password protection settings.</summary>
    public IEnumerable<string> DescribeConfig()
    {
        if (Auth0 is { } auth0)
        {
            yield return auth0 > NfcCommands.PackPage
                ? $"AUTH0    0x{auth0:X2} (no pages need the password)"
                : $"AUTH0    0x{auth0:X2} (pages 0x{auth0:X2} and up need the password)";
        }

        if (Access is { } access)
        {
            var reads = (access & NfcCommands.AccessProtBit) != 0 ? "reads and writes" : "writes only";
            var lck = (access & NfcCommands.AccessCfgLckBit) != 0 ? ", config permanently locked" : "";
            yield return $"ACCESS   0x{access:X2} (password protects {reads}{lck})";
        }
    }

    /// <summary>Which part of NTAG213 memory a page belongs to.</summary>
    public static string RegionOf(byte page) => page switch
    {
        <= 0x02 => "UID / lock",
        0x03 => "capability container",
        <= NfcCommands.UserLastPage => "user",
        NfcCommands.DynamicLockPage => "dynamic lock",
        NfcCommands.Config0Page => "CFG0 (AUTH0)",
        NfcCommands.Config1Page => "CFG1 (ACCESS)",
        NfcCommands.PasswordPage => "password",
        NfcCommands.PackPage => "PACK",
        _ => "beyond NTAG213",
    };

    public string ToText(DateTime? readAt = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BumpGo Tag Prepper dump (NTAG213)");
        if (Uid is not null) sb.AppendLine($"# UID      {Uid}");
        if (readAt is not null) sb.AppendLine($"# Read     {readAt:yyyy-MM-dd HH:mm:ss}");
        foreach (var line in DescribeConfig()) sb.AppendLine($"# {line}");
        sb.AppendLine("# Pages 2B (password) and 2C (PACK) cannot be read back and are not listed.");
        sb.AppendLine("#");

        foreach (var (page, data) in _pages)
        {
            var hex = string.Join(' ', data.Select(b => b.ToString("X2")));
            var ascii = new string(data.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
            sb.AppendLine($"{page:X2}: {hex}  |{ascii}|  {RegionOf(page)}");
        }

        return sb.ToString();
    }

    public static TagImage Parse(string text)
    {
        string? uid = null;
        var parsed = new List<(byte Page, byte[] Data)>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                var uidMatch = UidLine().Match(line);
                if (uidMatch.Success) uid = uidMatch.Groups[1].Value;
                continue;
            }

            var m = PageLine().Match(line);
            if (!m.Success) continue;

            var page = byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
            var data = m.Groups[2].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => byte.Parse(h, NumberStyles.HexNumber))
                .ToArray();
            parsed.Add((page, data));
        }

        var image = new TagImage(uid);
        foreach (var (page, data) in parsed) image.SetPage(page, data);
        return image;
    }

    public static TagImage Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Compares several images page by page and returns every page where they
    /// don't all agree (a page missing from one image counts as different).
    /// This is the tool for working out the profile layout: program tags with
    /// settings that differ in one way, dump them, and compare.
    /// </summary>
    public static IReadOnlyList<PageDifference> Compare(IReadOnlyList<TagImage> images)
    {
        var pages = images.SelectMany(i => i.PageNumbers).Distinct().Order();
        var result = new List<PageDifference>();

        foreach (var page in pages)
        {
            var values = images.Select(i => i[page]).ToArray();
            var changed = new bool[4];
            for (var b = 0; b < 4; b++)
            {
                var distinct = values.Select(v => v is null ? -1 : v[b]).Distinct().Count();
                changed[b] = distinct > 1;
            }

            if (changed.Any(c => c))
                result.Add(new PageDifference(page, values, changed));
        }

        return result;
    }

    [GeneratedRegex(@"^#\s*UID\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex UidLine();

    [GeneratedRegex(@"^([0-9A-Fa-f]{1,2})\s*:\s*((?:[0-9A-Fa-f]{2}\s+){3}[0-9A-Fa-f]{2})(?![0-9A-Fa-f])")]
    private static partial Regex PageLine();
}

/// <summary>One page that differs between compared images.</summary>
/// <param name="Values">The page's bytes in each image, in order; null where an image lacks the page.</param>
/// <param name="ChangedBytes">Which of the 4 byte positions differ.</param>
public sealed record PageDifference(byte Page, IReadOnlyList<byte[]?> Values, IReadOnlyList<bool> ChangedBytes);
