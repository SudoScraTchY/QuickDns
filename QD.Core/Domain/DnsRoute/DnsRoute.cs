namespace QD.Core.Domain.DnsRoute;

/// <summary>
/// One route-based DNS rule: queries whose domain matches <see cref="Pattern"/> are sent
/// to the DNS servers of the linked preset instead of the fallback servers.
/// </summary>
public sealed class DnsRoute
{
    public int Id { get; init; }

    /// <summary>
    /// Match pattern. Forms: <c>*</c> (catch-all fallback), <c>example.com</c> (the domain
    /// and every subdomain), <c>*.example.com</c> (subdomains only). Case-insensitive.
    /// </summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>The preset whose DNS servers handle matching queries.</summary>
    public int PresetId { get; init; }

    public bool Enabled { get; init; }

    /// <summary>Display/creation order. Matching specificity always wins over position.</summary>
    public int Position { get; init; }
}
