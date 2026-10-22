using QD.Core.Domain.DnsObj;

namespace QuickDNSConsole;

/// <summary>Seeds popular public DNS presets into an empty store (first run only).</summary>
public static class PresetSeeder
{
    private static readonly (string Name, string Primary, string Secondary)[] Defaults =
    [
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112"),
        ("OpenDNS", "208.67.222.222", "208.67.220.220"),
        ("AdGuard", "94.140.14.14", "94.140.15.15"),
    ];

    public static void SeedIfEmpty(IDnsObjRepository repository)
    {
        if (repository.Count() > 0)
            return;

        foreach (var (name, primary, secondary) in Defaults)
            repository.Create(name, primary, secondary);
    }
}
