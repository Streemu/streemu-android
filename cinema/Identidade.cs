using System.Security.Cryptography;

namespace Streemu.Filme;

/// <summary>
/// Identidade do arquivo (spec 010, 4.1): tamanho + SHA-256 de três pedaços de 4 MB (começo, meio, fim). Menos de 1 s mesmo para 60 GB
/// e não depende do nome; outro corte ou outro release dá outra identidade.
/// </summary>
public static class Identidade
{
    const int Pedaco = 4 << 20;

    public static string De(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return De(f);
    }

    /// <summary>Mesma conta, sobre um <see cref="Stream"/> com seek (no Android, o arquivo do seletor do sistema chega como descritor, não como caminho). Só esta sobrecarga é nova em relação ao Windows.</summary>
    public static string De(Stream f)
    {
        long n = f.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(n));
        var buf = new byte[Pedaco];
        foreach (long off in new[] { 0L, Math.Max(0, n / 2 - Pedaco / 2), Math.Max(0, n - Pedaco) })
        {
            f.Position = off;
            int lido = 0, r;
            while (lido < buf.Length && (r = f.Read(buf, lido, buf.Length - lido)) > 0) lido += r;
            sha.AppendData(buf, 0, lido);
        }
        return $"{n}-{Convert.ToHexString(sha.GetHashAndReset())[..32].ToLowerInvariant()}";
    }

    /// <summary>
    /// A identidade vem do outro PC e vira nome de arquivo (o <c>.streemuparcial</c>): só passa no formato que <see cref="De"/> gera.
    /// Ex.: <c>Identidade.Valida("1048576-0123456789abcdef0123456789abcdef")</c> → true; <c>"..\\x"</c> → false.
    /// </summary>
    public static bool Valida(string? identidade) =>
        identidade != null && System.Text.RegularExpressions.Regex.IsMatch(identidade, "^[0-9]{1,19}-[0-9a-f]{32}$");

    /// <summary>O tamanho em bytes que está na identidade ("tamanho-hash"); 0 se não der para ler. Ex.: <c>Tamanho("4200000-…")</c> → 4200000.</summary>
    public static long Tamanho(string identidade) => long.TryParse(identidade.Split('-')[0], out var n) ? n : 0;
}
