using QD.Core.Domain.DnsRoute;
using QD.Core.Helper;

namespace QD.Core.Application.Routing;

/// <summary>A compiled route rule: queries matching <see cref="Pattern"/> go to these servers.</summary>
public sealed record DnsRouteServer(string Pattern, string Primary, string? Secondary);

/// <summary>
/// Matches a queried domain name against route rules, YogaDNS-style: the most specific
/// (longest) matching suffix wins, and <c>*</c> is the catch-all fallback. Case-insensitive;
/// a plain pattern (<c>example.com</c>) matches the domain and all its subdomains, while
/// <c>*.example.com</c> matches subdomains only.
/// </summary>
public sealed class DnsRouteEngine
{
    private readonly List<CompiledRule> _rules;

    public DnsRouteEngine(IReadOnlyList<DnsRouteServer> rules)
    {
        _rules = rules
            .Select(Compile)
            .OfType<CompiledRule>()
            .OrderByDescending(r => r.Specificity) // stable: ties keep the input (position) order
            .ToList();
    }

    /// <summary>All compiled, enabled rules, most specific first (for diagnostics).</summary>
    public IReadOnlyList<DnsRouteServer> Rules => _rules.Select(r => r.Server).ToList();

    /// <summary>True when the pattern is a supported route pattern.</summary>
    public static bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var raw = pattern.Trim().ToLowerInvariant();
        if (raw is "" or "*.")
            return false;

        var candidate = raw.TrimEnd('.');
        if (candidate == "*")
            return true;

        if (candidate.StartsWith("*."))
            return DnsValidator.IsValidHostname(candidate[2..]);

        return DnsValidator.IsValidHostname(candidate);
    }

    /// <summary>The upstream servers for a queried domain, or null when no rule matches.</summary>
    public DnsRouteServer? Match(string? queryName)
    {
        var name = Normalize(queryName);
        if (name.Length == 0)
            return null;

        foreach (var rule in _rules) // most specific first
        {
            if (rule.Matches(name))
                return rule.Server;
        }

        return null;
    }

    private static CompiledRule? Compile(DnsRouteServer server)
    {
        var raw = server.Pattern.Trim().ToLowerInvariant();
        if (raw is "" or "*.")
            return null;

        var pattern = raw.TrimEnd('.');

        if (pattern == "*")
            return new CompiledRule(server, BaseDomain: string.Empty, SubdomainsOnly: false, CatchAll: true, Specificity: 0);

        if (pattern.StartsWith("*."))
        {
            var baseDomain = pattern[2..];
            if (!DnsValidator.IsValidHostname(baseDomain))
                return null;

            return new CompiledRule(server, BaseDomain: baseDomain, SubdomainsOnly: true, CatchAll: false, Specificity: baseDomain.Length);
        }

        if (!DnsValidator.IsValidHostname(pattern))
            return null;

        return new CompiledRule(server, BaseDomain: pattern, SubdomainsOnly: false, CatchAll: false, Specificity: pattern.Length);
    }

    private static string Normalize(string? queryName) =>
        (queryName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private sealed record CompiledRule(
        DnsRouteServer Server,
        string BaseDomain,
        bool SubdomainsOnly,
        bool CatchAll,
        int Specificity)
    {
        public bool Matches(string name)
        {
            if (CatchAll)
                return true;

            if (name.Equals(BaseDomain, StringComparison.Ordinal))
                return !SubdomainsOnly;

            return name.Length > BaseDomain.Length + 1
                   && name.EndsWith('.' + BaseDomain, StringComparison.Ordinal);
        }
    }
}
