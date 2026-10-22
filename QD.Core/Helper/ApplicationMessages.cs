namespace QD.Core.Helper;

public static class ApplicationMessages
{
    public const string DuplicatedRecord = "Error,Duplication Found";
    public const string RequestedRecordNotExists = "The Dns Index you Requested Does Not Exists";
    public const string DnsUnSetFailed = "An Error Occured while Clearing DNS";
    public const string DnsChangeFailed = "An Error Occured while Changing DNS";
    public const string DnsChanged = "DNS updated successfully";
    public const string DnsCleared = "DNS cleared — back to automatic (DHCP)";
    public const string NotElevated = "Administrator/root privileges are required to change DNS. Restart elevated (Windows: Run as administrator) or with sudo (Linux).";
    public const string NoSuitableAdapter = "No usable network adapter was found.";
    public const string AdapterNotFound = "The target network adapter could not be matched on this system.";
    public const string LinuxDeviceUnmanaged = "Device is not managed by NetworkManager (no connection profile found). Try resolvectl mode or edit /etc/resolv.conf manually.";
    public const string ResolvConfIsSymlink = "/etc/resolv.conf is a symlink (likely managed by systemd-resolved) and the direct-write fallback is not safe. Install NetworkManager or write a drop-in under /etc/systemd/resolved.conf.d/ instead.";
    public const string InvalidIpAddress = "One of the DNS addresses is not a valid IP address.";
    public const string NameRequired = "DNS preset name cannot be empty.";
    public const string StoreQuarantined = "The preset store was corrupted; it was quarantined and a fresh store was created.";
    public const string StoreOpenFailed = "The preset store could not be opened.";
}
