namespace QD.Core.Application.Networking;

/// <summary>Picks the platform implementation at runtime.</summary>
public static class NetworkAdapterServiceFactory
{
    public static INetworkAdapterService Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsNetworkAdapterService();

        if (OperatingSystem.IsLinux())
            return new LinuxNetworkAdapterService();

        throw new PlatformNotSupportedException("QuickDNS supports Windows and Linux (macOS may be added later).");
    }
}
