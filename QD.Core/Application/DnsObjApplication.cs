using System.Text.Json;
using QD.Core.Application.Networking;
using QD.Core.Cotracts.DnsObjContracts;
using QD.Core.Domain.DnsObj;
using QD.Core.Helper;

namespace QD.Core.Application;

public class DnsObjApplication(
    IDnsObjRepository dnsObjRepository,
    INetworkAdapterService adapterService) : IDnsObjApplication
{
    private readonly IDnsObjRepository _dnsObjRepository = dnsObjRepository;
    private readonly INetworkAdapterService _adapterService = adapterService;

    public IReadOnlyList<DnsObjViewModel> GetAll() => _dnsObjRepository.GetAll();

    public OperationResult Create(CreateDnsObj command)
    {
        var result = new OperationResult();

        var validation = Validate(command.Name, command.FirstDns, command.SecondDns);
        if (validation is not null)
            return result.Failed(validation);

        if (_dnsObjRepository.NameExists(command.Name))
            return result.Failed(ApplicationMessages.DuplicatedRecord);

        if (_dnsObjRepository.ServersExist(command.FirstDns, command.SecondDns))
            return result.Failed(ApplicationMessages.DuplicatedRecord);

        _dnsObjRepository.Create(command.Name.Trim(), command.FirstDns.Trim(), command.SecondDns.Trim());
        return result.Succeeded($"Preset '{command.Name.Trim()}' created");
    }

    public OperationResult Edit(EditDnsObj command)
    {
        var result = new OperationResult();

        var validation = Validate(command.Name, command.FirstDns, command.SecondDns);
        if (validation is not null)
            return result.Failed(validation);

        if (_dnsObjRepository.NameExists(command.Name, excludeId: command.Id))
            return result.Failed(ApplicationMessages.DuplicatedRecord);

        var entity = _dnsObjRepository.FindById(command.Id);
        if (entity is null)
            return result.Failed(ApplicationMessages.RequestedRecordNotExists);

        return _dnsObjRepository.Update(command.Id, command.Name.Trim(), command.FirstDns.Trim(), command.SecondDns.Trim())
            ? result.Succeeded($"Preset '{command.Name.Trim()}' updated")
            : result.Failed(ApplicationMessages.RequestedRecordNotExists);
    }

    public OperationResult Delete(int id)
    {
        var result = new OperationResult();

        var entity = _dnsObjRepository.FindById(id);
        if (entity is null)
            return result.Failed(ApplicationMessages.RequestedRecordNotExists);

        return _dnsObjRepository.Delete(id)
            ? result.Succeeded($"Preset '{entity.Name}' deleted")
            : result.Failed(ApplicationMessages.RequestedRecordNotExists);
    }

    public EditDnsObj? GetDetail(int id)
    {
        var entity = _dnsObjRepository.FindById(id);
        return entity is null
            ? null
            : new EditDnsObj(entity.FirstDns, entity.SecondDns, entity.Name ?? string.Empty, entity.Id);
    }

    public OperationResult SetDns(int id, string? preferredAdapterId)
    {
        var result = new OperationResult();

        var dnsObj = _dnsObjRepository.FindById(id);
        if (dnsObj is null)
            return result.Failed(ApplicationMessages.RequestedRecordNotExists);

        var adapter = _adapterService.ResolveTarget(preferredAdapterId);
        if (adapter is null)
            return result.Failed(ApplicationMessages.NoSuitableAdapter);

        return _adapterService.ApplyDns(adapter, dnsObj.FirstDns, NullIfEmpty(dnsObj.SecondDns));
    }

    public OperationResult UnSetDns(string? preferredAdapterId)
    {
        var result = new OperationResult();

        var adapter = _adapterService.ResolveTarget(preferredAdapterId);
        if (adapter is null)
            return result.Failed(ApplicationMessages.NoSuitableAdapter);

        return _adapterService.ClearDns(adapter);
    }

    public DnsObjViewModel? GetCurrentDns(string? preferredAdapterId)
    {
        var adapter = _adapterService.ResolveTarget(preferredAdapterId);
        if (adapter is null || adapter.DnsServers.Count == 0)
            return null;

        // NOTE: fixed lookup — the 1.x implementation searched for an empty
        // view-model string and could therefore never resolve a preset.
        var first = adapter.DnsServers[0];
        var second = adapter.DnsServers.Count > 1 ? adapter.DnsServers[1] : string.Empty;

        foreach (var preset in _dnsObjRepository.GetAll())
        {
            var forward = Matches(preset.FirstDns, first) && Matches(preset.SecondDns, second);
            var reverse = Matches(preset.FirstDns, second) && Matches(preset.SecondDns, first);
            if (forward || reverse)
                return preset;
        }

        return null;
    }

    // ---- export / import / auto-backup --------------------------------------

    public OperationResult ExportPresets(string filePath)
    {
        var result = new OperationResult();

        try
        {
            var presets = _dnsObjRepository.GetAll();
            var transfer = new PresetTransferFile
            {
                ExportedUtc = DateTime.UtcNow,
                Presets = [.. presets.Select(p => new PresetTransferItem
                {
                    Name = p.Name ?? string.Empty,
                    PrimaryDns = p.FirstDns,
                    SecondaryDns = p.SecondDns,
                })],
            };

            WriteAtomically(filePath, transfer);
            return result.Succeeded($"Exported {transfer.Presets.Count} preset(s) → {filePath}");
        }
        catch (Exception ex)
        {
            return result.Failed($"Export failed: {ex.Message}");
        }
    }

    public OperationResult ImportPresets(string filePath)
    {
        var result = new OperationResult();

        try
        {
            if (!File.Exists(filePath))
                return result.Failed($"Import file not found: {filePath}");

            var transfer = JsonSerializer.Deserialize(File.ReadAllText(filePath), PresetTransferJsonContext.Default.PresetTransferFile);
            if (transfer is null || transfer.Presets.Count == 0)
                return result.Failed("Import file contains no presets.");

            int imported = 0, skipped = 0, invalid = 0;
            foreach (var item in transfer.Presets)
            {
                var name = (item.Name ?? string.Empty).Trim();
                var primary = (item.PrimaryDns ?? string.Empty).Trim();
                var secondary = (item.SecondaryDns ?? string.Empty).Trim();

                if (name.Length == 0
                    || !DnsValidator.IsValidIpAddress(primary)
                    || (!string.IsNullOrWhiteSpace(secondary) && !DnsValidator.IsValidIpAddress(secondary)))
                {
                    invalid++;
                    continue;
                }

                if (_dnsObjRepository.NameExists(name) || _dnsObjRepository.ServersExist(primary, secondary))
                {
                    skipped++;
                    continue;
                }

                _dnsObjRepository.Create(name, primary, secondary);
                imported++;
            }

            var summary = $"Imported {imported} preset(s)";
            if (skipped > 0) summary += $", skipped {skipped} duplicate(s)";
            if (invalid > 0) summary += $", ignored {invalid} invalid row(s)";
            return result.Succeeded($"{summary} from {Path.GetFileName(filePath)}");
        }
        catch (JsonException)
        {
            return result.Failed("Import failed: the file is not a valid QuickDNS export.");
        }
        catch (Exception ex)
        {
            return result.Failed($"Import failed: {ex.Message}");
        }
    }

    public OperationResult WriteAutoBackup(string filePath)
    {
        // Never clobber a good backup with the empty store of a fresh/corrupted start.
        if (_dnsObjRepository.Count() == 0)
            return new OperationResult().Failed("Store is empty — backup skipped.");

        return ExportPresets(filePath);
    }

    // ---- helpers -------------------------------------------------------------

    private static void WriteAtomically(string filePath, PresetTransferFile transfer)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(transfer, PresetTransferJsonContext.Default.PresetTransferFile));
        File.Move(temp, fullPath, overwrite: true);
    }

    private static string? Validate(string? name, string? firstDns, string? secondDns)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ApplicationMessages.NameRequired;

        if (!DnsValidator.IsValidIpAddress(firstDns))
            return ApplicationMessages.InvalidIpAddress;

        if (!string.IsNullOrWhiteSpace(secondDns) && !DnsValidator.IsValidIpAddress(secondDns))
            return ApplicationMessages.InvalidIpAddress;

        return null;
    }

    private static bool Matches(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
