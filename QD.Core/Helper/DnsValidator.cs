using System.Net;
using System.Diagnostics.CodeAnalysis;

namespace QD.Core.Helper;

/// <summary>Shared input validation for DNS values.</summary>
public static class DnsValidator
{
    /// <summary>
    /// True when the value parses as an IPv4 or IPv6 address.
    /// Guards against <see cref="IPAddress.TryParse"/> quirks such as accepting "1" as 0.0.0.1.
    /// </summary>
    public static bool IsValidIpAddress([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (!candidate.Contains('.') && !candidate.Contains(':'))
            return false;

        return IPAddress.TryParse(candidate, out _);
    }

    /// <summary>
    /// True when the value is a syntactically valid ASCII hostname (labels of 1-63
    /// alphanumeric/hyphen characters, at most 253 characters total). Internationalized
    /// (non-ASCII) names must be converted to punycode first.
    /// </summary>
    public static bool IsValidHostname([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim().TrimEnd('.');
        if (candidate.Length == 0 || candidate.Length > 253)
            return false;

        foreach (var ch in candidate)
        {
            if (!char.IsAscii(ch))
                return false;
        }

        foreach (var label in candidate.Split('.'))
        {
            if (label.Length is 0 or > 63)
                return false;

            if (label.StartsWith('-') || label.EndsWith('-'))
                return false;

            foreach (var ch in label)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                    return false;
            }
        }

        return true;
    }
}
