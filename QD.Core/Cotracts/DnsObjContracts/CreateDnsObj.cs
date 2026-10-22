namespace QD.Core.Cotracts.DnsObjContracts;

public class CreateDnsObj
{
    public string FirstDns { get; set; } = string.Empty;
    public string SecondDns { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public CreateDnsObj()
    {
        
    }
    public CreateDnsObj(string firstDns,string secondDns, string name)
    {
        FirstDns = firstDns;
        SecondDns = secondDns;
        Name = name;
    }
}