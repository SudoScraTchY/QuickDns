using QD.Core.Cotracts.DnsObjContracts;

namespace QD.Core.Domain.DnsObj;

/// <summary>
/// Storage contract for DNS presets. Implementations: SQLite (Microsoft.Data.Sqlite)
/// in the product, in-memory fake in tests.
/// </summary>
public interface IDnsObjRepository
{
    int Count();

    /// <summary>All presets, newest (highest id) first.</summary>
    IReadOnlyList<DnsObjViewModel> GetAll();

    DnsObjViewModel? FindById(int id);

    /// <summary>Inserts a preset and returns the stored view of it.</summary>
    DnsObjViewModel Create(string name, string primaryDns, string secondaryDns);

    bool Update(int id, string name, string primaryDns, string secondaryDns);

    bool Delete(int id);

    /// <summary>True when another preset (case-insensitive) already uses this name.</summary>
    bool NameExists(string name, int excludeId = 0);

    /// <summary>True when another preset already uses this server pair (either order).</summary>
    bool ServersExist(string primaryDns, string secondaryDns);
}
