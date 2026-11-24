using System.Diagnostics.CodeAnalysis;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// The <c>.sha256</c> asset: a SHA-256 hex digest, optionally followed by the file name as
/// <c>sha256sum</c> writes it (<c>digest  name</c> or <c>digest *name</c>). A file name, when
/// present, must be the MSI's. Pure.
/// </summary>
public static class ChecksumFile
{
    /// <summary>A checksum file is one line; anything far larger is not one.</summary>
    public const int MaxBytes = 4 * 1024;

    private const int DigestHexLength = 64;

    public static bool TryParse(string? text, string expectedFileName, [NotNullWhen(true)] out byte[]? digest)
    {
        digest = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxBytes)
        {
            return false;
        }

        var line = text.TrimStart('﻿')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
        {
            return false;
        }

        var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hex = parts[0];
        if (hex.Length != DigestHexLength || !hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        if (parts.Length == 2)
        {
            var name = parts[1].TrimStart('*');
            if (!string.Equals(name, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        digest = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>The line build.ps1 writes: lower-case hex, two spaces, the file name.</summary>
    public static string Format(byte[] digest, string fileName) => $"{Convert.ToHexStringLower(digest)}  {fileName}";
}
