using QD.Core.Domain.DnsRoute;

namespace QD.Core.Domain.DnsRoute;

/// <summary>Storage for the route-based DNS rules. Plain ADO.NET implementions only (trim-safe).</summary>
public interface IDnsRouteRepository
{
    int Count();

    IReadOnlyList<DnsRoute> GetAll();

    DnsRoute? FindById(int id);

    /// <summary>Creates an enabled rule at the end of the display order.</summary>
    DnsRoute Create(string pattern, int presetId);

    bool Update(int id, string pattern, int presetId, bool enabled);

    bool Delete(int id);

    /// <summary>True when the pattern already exists (case-insensitive), optionally excluding one id.</summary>
    bool PatternExists(string pattern, int excludeId = 0);
}
