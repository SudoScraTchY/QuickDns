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
}
