using System.Diagnostics.CodeAnalysis;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Splits a verb's arguments into its own options (<c>--out path</c> or <c>--out=path</c>) and
/// everything else, which goes on to configuration as <c>--Key value</c> pairs.
/// </summary>
public static class CommandArguments
{
    public static bool TryTake(
        string[] args,
        IReadOnlyCollection<string> optionNames,
        out Dictionary<string, string> options,
        out string[] rest,
        [NotNullWhen(false)] out string? error)
    {
        options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<string>();
        error = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var name = optionNames.FirstOrDefault(option =>
                string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                remaining.Add(argument);
                continue;
            }

            string value;
            if (argument.Length > name.Length)
            {
                value = argument[(name.Length + 1)..];
            }
            else if (index + 1 < args.Length)
            {
                value = args[++index];
            }
            else
            {
                rest = [];
                error = $"{name} needs a value.";
                return false;
            }

            if (!options.TryAdd(name, value))
            {
                rest = [];
                error = $"{name} is given twice.";
                return false;
            }
        }

        rest = [.. remaining];
        return true;
    }
}
