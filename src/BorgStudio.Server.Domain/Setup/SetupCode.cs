using System.Security.Cryptography;
using System.Text;

namespace BorgStudio.Server.Domain.Setup;

/// <summary>
/// One-time code that protects the setup wizard of a fresh server: only someone who can read the
/// server's log or data folder can create the first administrator.
/// </summary>
public static class SetupCode
{
    // No 0/O and 1/I/L, so the code can be typed from a log without guessing.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int GroupCount = 4;
    private const int GroupLength = 4;

    /// <summary>A new random code such as <c>K7QF-M2XA-9PDR-HT4W</c> (about 79 bits).</summary>
    public static string Generate()
    {
        var groups = Enumerable.Range(0, GroupCount)
            .Select(_ => RandomNumberGenerator.GetString(Alphabet, GroupLength));
        return string.Join('-', groups);
    }

    /// <summary>Upper case, without spaces and dashes, so users may type it either way.</summary>
    public static string Normalize(string? code)
    {
        var builder = new StringBuilder();
        foreach (var c in code ?? string.Empty)
        {
            if (!char.IsWhiteSpace(c) && c != '-')
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Compares in constant time, ignoring case, spaces and dashes.</summary>
    public static bool Matches(string? expected, string? entered)
    {
        var left = Normalize(expected);
        var right = Normalize(entered);
        return left.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }
}
