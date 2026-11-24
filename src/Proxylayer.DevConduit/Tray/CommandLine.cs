using System.Text;

namespace Proxylayer.DevConduit.Tray;

/// <summary>
/// Builds a Windows command line from separate arguments, quoted so that the standard
/// argument parser splits it back into exactly those arguments. Needed where a command line
/// must be a single string, as with <c>ShellExecute</c>.
/// </summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself escaped.
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so they do not escape it.
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
