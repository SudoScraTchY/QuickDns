namespace QD.Core.Cotracts.DnsObjContracts;

public class DnsObjViewModel
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string FirstDns { get; set; } = string.Empty;
    public string SecondDns { get; set; } = string.Empty;

    /// <summary>Display form: "primary | secondary", or just "primary" when there is no secondary.</summary>
    public string DnsAddresses => string.IsNullOrWhiteSpace(SecondDns) ? FirstDns : $"{FirstDns} | {SecondDns}";

    public DnsObjViewModel()
    {
    }

    public DnsObjViewModel(int id, string? name, string? firstDns, string? secondDns)
    {
        Id = id;
        Name = name;
        FirstDns = firstDns ?? string.Empty;
        SecondDns = secondDns ?? string.Empty;
    }
}
