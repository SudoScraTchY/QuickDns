using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Domain.DnsObj;

namespace QD.Tests.Fakes;

/// <summary>In-memory stand-in for the SQLite repository.</summary>
internal sealed class FakeDnsObjRepository : IDnsObjRepository
{
    public List<DnsObjViewModel> Store { get; } = [];

    private int _nextId = 1;

    public int Count() => Store.Count;

    public IReadOnlyList<DnsObjViewModel> GetAll() => Store
        .OrderByDescending(x => x.Id)
        .ToList();

    public DnsObjViewModel? FindById(int id) => Store.FirstOrDefault(x => x.Id == id);

    public DnsObjViewModel Create(string name, string primaryDns, string secondaryDns)
    {
        var preset = new DnsObjViewModel(_nextId++, name, primaryDns, secondaryDns);
        Store.Add(preset);
        return preset;
    }

    public bool Update(int id, string name, string primaryDns, string secondaryDns)
    {
        var existing = Store.FirstOrDefault(x => x.Id == id);
        if (existing is null)
            return false;

        existing.Name = name;
        existing.FirstDns = primaryDns;
        existing.SecondDns = secondaryDns;
        return true;
    }

    public bool Delete(int id) => Store.RemoveAll(x => x.Id == id) > 0;

    public bool NameExists(string name, int excludeId = 0) => Store.Any(x =>
        x.Id != excludeId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool ServersExist(string primaryDns, string secondaryDns) => Store.Any(x =>
        (x.FirstDns == primaryDns && x.SecondDns == secondaryDns) ||
        (x.FirstDns == secondaryDns && x.SecondDns == primaryDns));
}

internal static class FakeDnsStoreExtensions
{
    public static void AddPreset(this FakeDnsObjRepository repository, string name, string first, string second = "")
        => repository.Create(name, first, second);
}
