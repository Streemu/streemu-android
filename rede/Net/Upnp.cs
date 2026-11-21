using System.Net;

namespace Streemu.Net;

/// <summary>
/// Só a parte pura do Upnp.cs do app Windows: classificar endereços. A descoberta do roteador por UPnP (COM, "UPnP.UPnPDeviceFinder") é só do
/// Windows e fica de fora. O nome da classe fica igual para o Tunnel.cs não mudar.
/// </summary>
public static class Upnp
{
    /// <summary>IP da faixa de CGNAT (100.64/10) ou privado: não é alcançável pela internet, mas pode ser dentro da operadora.</summary>
    public static bool IsCarrierOrPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsCarrier(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}
