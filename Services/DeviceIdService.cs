using System.Net.NetworkInformation;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AutoPrint.Services;

/// <summary>
/// Стабильный идентификатор устройства для привязки кассы к аккаунту.
/// Основа — MachineGuid Windows: не меняется при переустановке/обновлении AutoPrint,
/// смене сетевых карт, VPN и т.п. (MAC для этого ненадёжен — адаптеры добавляются и
/// пропадают). Меняется только при переустановке самой Windows.
/// Сырой MachineGuid наружу не отдаётся — только его хеш в формате GUID.
/// </summary>
public static class DeviceIdService
{
    private const string Salt = "AutoPrint.DeviceId.v1";
    private static string? _cached;

    public static string GetDeviceId() => _cached ??= Compute();

    /// <summary>MAC первого физического адаптера (Ethernet/Wi-Fi) — справочно, не как ключ.</summary>
    public static string? GetPrimaryMac()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                   or NetworkInterfaceType.Wireless80211
                            && n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .OrderBy(n => n.GetPhysicalAddress().ToString(), StringComparer.Ordinal)
                .Select(n => string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string Compute()
    {
        string? raw = ReadMachineGuid() ?? LoadOrCreateFallback();
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + ":" + raw.ToLowerInvariant()));
        return new Guid(h.AsSpan(0, 16)).ToString();
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                       .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var v = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    /// <summary>Запасной вариант: GUID, сохранённый один раз в %AppData%\AutoPrint.</summary>
    private static string LoadOrCreateFallback()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPrint");
        string file = Path.Combine(dir, "device.id");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } s) return s;
            Directory.CreateDirectory(dir);
            string id = Guid.NewGuid().ToString();
            File.WriteAllText(file, id);
            return id;
        }
        catch { return Environment.MachineName; }
    }
}
