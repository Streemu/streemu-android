using System.Net;
using System.Text;

namespace Streemu.Net;

/// <summary>
/// Código de pareamento, em base32 sem caracteres ambíguos (sem 0/O, 1/I).
/// v1 (14 bytes, 23 caracteres): versão + IPv4 público + porta + segredo(6) + verificação.
/// v2 (20 bytes, 32 caracteres): v1 + endereço alternativo (IPv4 + porta), usado quando a operadora
///     entrega os pacotes entre clientes dela com o endereço INTERNO do CGNAT (ex.: 100.90.x.x).
/// v3 (26 bytes, 42 caracteres): v2 + endereço de relay (TURN), para quem não consegue receber direto.
/// </summary>
public sealed class PeerCode
{
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public IPAddress Address { get; }
    public int Port { get; }
    public byte[] Secret { get; }
    public IPEndPoint? Alt { get; }
    public IPEndPoint? Relay { get; }

    public PeerCode(IPAddress address, int port, byte[] secret, IPEndPoint? alt = null, IPEndPoint? relay = null)
    {
        Address = address; Port = port; Secret = secret; Alt = alt; Relay = relay;
    }

    public IPEndPoint Endpoint => new(Address, Port);

    public string Encode()
    {
        int version = Relay != null ? 3 : Alt != null ? 2 : 1;
        var b = new byte[version switch { 3 => 26, 2 => 20, _ => 14 }];
        b[0] = (byte)version;
        Address.GetAddressBytes().CopyTo(b, 1);
        b[5] = (byte)(Port >> 8); b[6] = (byte)Port;
        Secret.CopyTo(b, 7);
        if (version >= 2 && Alt != null) WriteEp(b, 13, Alt);
        if (version == 3) WriteEp(b, 19, Relay!);
        b[^1] = Checksum(b.AsSpan(0, b.Length - 1));
        string raw = ToBase32(b);
        var sb = new StringBuilder("LX");
        for (int i = 0; i < raw.Length; i += 5) sb.Append('-').Append(raw.AsSpan(i, Math.Min(5, raw.Length - i)));
        return sb.ToString();
    }

    static void WriteEp(byte[] b, int at, IPEndPoint ep)
    {
        ep.Address.GetAddressBytes().CopyTo(b, at);
        b[at + 4] = (byte)(ep.Port >> 8); b[at + 5] = (byte)ep.Port;
    }

    static IPEndPoint? ReadEp(byte[] b, int at)
    {
        int port = (b[at + 4] << 8) | b[at + 5];
        return port == 0 ? null : new IPEndPoint(new IPAddress(b.AsSpan(at, 4)), port);
    }

    /// <summary>Lê um código colado (aceita espaços, minúsculas e sem traços). Lança FormatException com o motivo.</summary>
    public static PeerCode Parse(string text)
    {
        string clean = new string(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (clean.StartsWith("LX")) clean = clean[2..];
        clean = clean.Replace('0', 'O').Replace('1', 'I');
        int len = clean.Length switch { 23 => 14, 32 => 20, 42 => 26, _ => 0 };
        if (len == 0)
            throw new FormatException($"o código precisa ter 23, 32 ou 42 letras/números depois de \"LX\"; o colado tem {clean.Length} (copiou pela metade?)");
        foreach (char c in clean)
            if (!Alphabet.Contains(c)) throw new FormatException($"o caractere '{c}' não existe nos códigos (não usamos 0, 1, O nem I)");
        byte[] b = FromBase32(clean, len);
        int expectedVersion = len switch { 14 => 1, 20 => 2, _ => 3 };
        if (b[0] != expectedVersion) throw new FormatException($"versão de código {b[0]} desconhecida; os dois precisam usar a mesma versão do app");
        if (Checksum(b.AsSpan(0, len - 1)) != b[len - 1]) throw new FormatException("o código tem algum caractere errado (falhou a verificação)");
        var ip = new IPAddress(b.AsSpan(1, 4));
        int port = (b[5] << 8) | b[6];
        if (port == 0) throw new FormatException("porta 0 no código");
        var alt = len >= 20 ? ReadEp(b, 13) : null;
        var relay = len == 26 ? ReadEp(b, 19) : null;
        return new PeerCode(ip, port, b.AsSpan(7, 6).ToArray(), alt, relay);
    }

    static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0x5A;
        foreach (byte x in data) sum = (sum * 31 + x) & 0xFF;
        return (byte)sum;
    }

    static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte x in data)
        {
            buffer = ((buffer << 8) | x) & 0xFFFFF; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    static byte[] FromBase32(string text, int length)
    {
        var result = new byte[length];
        int buffer = 0, bits = 0, index = 0;
        foreach (char c in text)
        {
            buffer = ((buffer << 5) | Alphabet.IndexOf(c)) & 0xFFFFF; bits += 5;
            if (bits >= 8 && index < length) { result[index++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        return result;
    }
}
