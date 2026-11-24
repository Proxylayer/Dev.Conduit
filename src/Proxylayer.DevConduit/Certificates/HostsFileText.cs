using System.Text;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>The outcome of editing a hosts file's text.</summary>
/// <param name="Text">The new text.</param>
/// <param name="Changed">False when the managed line was already exactly right.</param>
/// <param name="ShadowingLines">Lines this app does not manage that map the same host name and come first, so they win.</param>
public sealed record HostsFileEdit(string Text, bool Changed, IReadOnlyList<string> ShadowingLines);

/// <summary>
/// Edits the text of a Windows hosts file. The app owns only lines ending in
/// <see cref="Marker"/>, one per host name; every other line is left exactly as it was,
/// with its own line ending, CRLF or LF. A new managed line takes the file's dominant line
/// ending (CRLF when there is none to go by), and whether the file ended with a line ending
/// is kept, so adding a line and removing it again gives back the original text exactly.
/// Characters are never interpreted beyond the ASCII the format needs, so the text may hold
/// one character per byte of a file in any single-byte or UTF-8 encoding. The managed line
/// itself is ASCII only. Pure: no file is read or written here.
/// </summary>
public static class HostsFileText
{
    public const string Marker = "# Dev.Conduit";

    private const string CrLf = "\r\n";
    private const string Lf = "\n";

    /// <summary>The managed line for a host name.</summary>
    /// <exception cref="ArgumentException">The address or host name is not plain ASCII without spaces or '#'.</exception>
    public static string ManagedLine(string address, string hostname)
    {
        RequireField(address, nameof(address));
        RequireField(hostname, nameof(hostname));
        return $"{address}\t{hostname}\t{Marker}";
    }

    /// <summary>
    /// Replaces this app's line for <paramref name="hostname"/>, or adds one at the end. Any
    /// duplicate managed lines for the same host are dropped. A replaced line keeps its own
    /// line ending.
    /// </summary>
    /// <exception cref="ArgumentException">The address or host name is not plain ASCII without spaces or '#'.</exception>
    public static HostsFileEdit SetEntry(string text, string address, string hostname)
    {
        ArgumentNullException.ThrowIfNull(text);
        hostname = DnsHostname.Normalize(hostname);
        var managed = ManagedLine(address?.Trim() ?? string.Empty, hostname);
        var lines = SplitLines(text);

        var result = new List<Line>(lines.Count + 1);
        var shadowing = new List<string>();
        var placed = false;

        foreach (var line in lines)
        {
            if (IsManagedFor(line.Content, hostname))
            {
                if (!placed)
                {
                    result.Add(line with { Content = managed });
                    placed = true;
                }

                continue;
            }

            if (!placed && MapsHostname(line.Content, hostname))
            {
                shadowing.Add(line.Content);
            }

            result.Add(line);
        }

        if (!placed)
        {
            var ending = DominantEnding(lines);
            if (result.Count > 0 && result[^1].Ending.Length == 0)
            {
                // The file did not end with a line ending, and it still will not: the last line
                // gets one to separate it from the managed line, which then goes without.
                result[^1] = result[^1] with { Ending = ending };
                result.Add(new Line(managed, string.Empty));
            }
            else
            {
                result.Add(new Line(managed, ending));
            }
        }

        var updated = Join(result);
        return new HostsFileEdit(updated, !string.Equals(updated, text, StringComparison.Ordinal), shadowing);
    }

    /// <summary>
    /// Removes every line this app wrote for <paramref name="hostname"/>; every other line is
    /// left as it was. When the file did not end with a line ending, the new last line loses
    /// the one <see cref="SetEntry"/> gave it.
    /// </summary>
    public static HostsFileEdit RemoveEntry(string text, string hostname)
    {
        ArgumentNullException.ThrowIfNull(text);
        hostname = DnsHostname.Normalize(hostname);
        var lines = SplitLines(text);
        var kept = lines.Where(line => !IsManagedFor(line.Content, hostname)).ToList();
        if (kept.Count == lines.Count)
        {
            return new HostsFileEdit(text, Changed: false, []);
        }

        if (lines[^1].Ending.Length == 0 && kept.Count > 0)
        {
            kept[^1] = kept[^1] with { Ending = string.Empty };
        }

        return new HostsFileEdit(Join(kept), Changed: true, []);
    }

    /// <summary>The address in this app's line for <paramref name="hostname"/>, or null when there is none.</summary>
    public static string? ManagedAddress(string text, string hostname)
    {
        ArgumentNullException.ThrowIfNull(text);
        hostname = DnsHostname.Normalize(hostname);
        var line = SplitLines(text).FirstOrDefault(candidate => IsManagedFor(candidate.Content, hostname));
        return line is null ? null : Fields(line.Content.TrimEnd()[..^Marker.Length]).FirstOrDefault();
    }

    /// <summary>True for a line this app wrote for <paramref name="hostname"/>.</summary>
    public static bool IsManagedFor(string line, string hostname)
    {
        var trimmed = line.TrimEnd();
        if (!trimmed.EndsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = Fields(trimmed[..^Marker.Length]);
        return fields.Count >= 2 && fields.Skip(1).Any(name => string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for an active line (not a comment) that maps <paramref name="hostname"/>.</summary>
    public static bool MapsHostname(string line, string hostname)
    {
        var content = line;
        var comment = content.IndexOf('#');
        if (comment >= 0)
        {
            content = content[..comment];
        }

        var fields = Fields(content);
        return fields.Count >= 2 && fields.Skip(1).Any(name => string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The managed line's fields are printable ASCII: no space, tab, '#' or control character.</summary>
    private static void RequireField(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character => character is <= ' ' or > '~' or '#'))
        {
            throw new ArgumentException($"'{value}' cannot go into a hosts file line: only printable ASCII without spaces or '#' can.", name);
        }
    }

    private static List<string> Fields(string text) =>
        text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The ending most lines use; CRLF on a tie or when no line has one.</summary>
    private static string DominantEnding(IReadOnlyList<Line> lines)
    {
        var lf = lines.Count(line => line.Ending == Lf);
        var crlf = lines.Count(line => line.Ending == CrLf);
        return lf > crlf ? Lf : CrLf;
    }

    /// <summary>
    /// The lines with their endings. A file that ends with a line ending has no empty line
    /// after it; one that does not has a last line with an empty ending. A carriage return
    /// not followed by a line feed is part of the line.
    /// </summary>
    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            var crlf = index > start && text[index - 1] == '\r';
            var contentEnd = crlf ? index - 1 : index;
            lines.Add(new Line(text[start..contentEnd], crlf ? CrLf : Lf));
            start = index + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new Line(text[start..], string.Empty));
        }

        return lines;
    }

    private static string Join(IEnumerable<Line> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line.Content).Append(line.Ending);
        }

        return builder.ToString();
    }

    private sealed record Line(string Content, string Ending);
}
