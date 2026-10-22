using System.Text.Json.Serialization;

namespace QD.Core.Cotracts.DnsObjContracts;

/// <summary>Portable export/import container (schema versioned for future changes).</summary>
public sealed class PresetTransferFile
{
    public string App { get; set; } = "QuickDNS";
    public int Schema { get; set; } = 1;
    public DateTime ExportedUtc { get; set; }
    public List<PresetTransferItem> Presets { get; set; } = [];
}

public sealed class PresetTransferItem
{
    public string Name { get; set; } = string.Empty;
    public string PrimaryDns { get; set; } = string.Empty;
    public string SecondaryDns { get; set; } = string.Empty;
}

/// <summary>Source-generated JSON contracts — reflection-free, safe under IL trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PresetTransferFile))]
internal sealed partial class PresetTransferJsonContext : JsonSerializerContext
{
}
