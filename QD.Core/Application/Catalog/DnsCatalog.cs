using System.Text.Json;
using System.Text.Json.Serialization;

namespace QD.Core.Application.Catalog;

/// <summary>One curated DNS server bundle entry from the embedded catalog.</summary>
public sealed class DnsCatalogEntry
{
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Primary { get; set; } = string.Empty;
    public string Secondary { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class DnsCatalogGroup
{
    public string Name { get; set; } = string.Empty;
    public List<DnsCatalogEntry> Entries { get; set; } = [];
}

public sealed class DnsCatalogFile
{
    public int Catalog { get; set; } = 1;
    public List<DnsCatalogGroup> Groups { get; set; } = [];
}

/// <summary>Source-generated JSON contracts — reflection-free, safe under IL trimming and AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DnsCatalogFile))]
internal sealed partial class DnsCatalogJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The built-in, optional catalog of free public DNS servers (global and Iranian providers
/// such as Shecan, Shatel, Electro, Begzar, Radar Game and 403.online). Loaded once from an
/// embedded resource; importing entries is always a deliberate user action.
/// </summary>
public interface IDnsCatalog
{
    IReadOnlyList<DnsCatalogEntry> Entries { get; }
}

public sealed class DnsCatalog : IDnsCatalog
{
    public static DnsCatalog Embedded { get; } = new();

    public IReadOnlyList<DnsCatalogEntry> Entries { get; }

    public DnsCatalog() : this(LoadEmbedded())
    {
    }

    public DnsCatalog(IReadOnlyList<DnsCatalogEntry> entries)
    {
        Entries = entries;
    }

    private static IReadOnlyList<DnsCatalogEntry> LoadEmbedded()
    {
        const string resourceName = "QD.Core.Resources.dns-catalog.json";

        var assembly = typeof(DnsCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);

        var file = JsonSerializer.Deserialize(reader.ReadToEnd(), DnsCatalogJsonContext.Default.DnsCatalogFile);
        if (file is null)
            return [];

        return
        [
            .. file.Groups.SelectMany(group => group.Entries.Select(entry => new DnsCatalogEntry
            {
                Name = entry.Name,
                Group = group.Name,
                Primary = entry.Primary,
                Secondary = entry.Secondary,
                Note = entry.Note,
            })),
        ];
    }
}
