using QD.Core.Helper;

namespace QD.Core.Cotracts.DnsObjContracts;

/// <summary>
/// Application service over the DNS preset store and the platform network adapter service.
/// All operations are synchronous: the store is a tiny local SQLite database and the
/// adapter operations are short-lived calls, so the console TUI can stay single-threaded.
/// </summary>
public interface IDnsObjApplication
{
    IReadOnlyList<DnsObjViewModel> GetAll();

    OperationResult Create(CreateDnsObj command);
    OperationResult Edit(EditDnsObj command);

    /// <summary>Deletes a preset by id. Fails only when the id does not exist.</summary>
    OperationResult Delete(int id);

    EditDnsObj? GetDetail(int id);

    /// <summary>Applies the preset's DNS to the target adapter (preferred or auto-detected).</summary>
    OperationResult SetDns(int id, string? preferredAdapterId);

    /// <summary>Reverts the target adapter to automatic (DHCP) DNS.</summary>
    OperationResult UnSetDns(string? preferredAdapterId);

    /// <summary>
    /// The preset currently active on the target adapter, or null when the adapter has no
    /// DNS configured or the running configuration does not match any saved preset.
    /// </summary>
    DnsObjViewModel? GetCurrentDns(string? preferredAdapterId);

    /// <summary>Writes all presets to a portable JSON file (atomically).</summary>
    OperationResult ExportPresets(string filePath);

    /// <summary>Validates, de-duplicates and imports presets from an export file.</summary>
    OperationResult ImportPresets(string filePath);

    /// <summary>Refreshes the automatic backup file. No-op when the store is empty
    /// (so a fresh/corrupted store never overwrites a good backup).</summary>
    OperationResult WriteAutoBackup(string filePath);
}
