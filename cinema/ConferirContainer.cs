namespace Streemu.Filme;

/// <summary>
/// Um arquivo que veio de outro aparelho só vai ao player se o começo dele for de um contêiner de vídeo conhecido (spec 028, 5.3): uma lista
/// (.m3u8, .sdp...) com nome de .mp4 faria o player buscar endereços de rede. Reconhece MP4/MOV ('ftyp'), Matroska/WebM (0x1A45DFA3),
/// MPEG-TS (0x47 a cada 188 bytes) e AVI ('RIFF....AVI ').
/// Ex.: <c>ConferirContainer.Motivo(caminho)</c> → null (pode abrir) ou "o arquivo não é um vídeo MP4, MKV, WebM, TS ou AVI".
/// </summary>
public static class ConferirContainer
{
    const int Ler = 188 * 5;

    public static string? Motivo(string caminho)
    {
        try
        {
            using var f = File.OpenRead(caminho);
            var b = new byte[Ler];
            int n = 0, r;
            while (n < Ler && (r = f.Read(b, n, Ler - n)) > 0) n += r;
            return Motivo(b.AsSpan(0, n));
        }
        catch (Exception e) { return $"não consegui ler o começo do arquivo ({e.GetType().Name})"; }
    }

    public static string? Motivo(ReadOnlySpan<byte> b)
    {
        if (Formato(b) != null) return null;
        return "o arquivo não é um vídeo MP4, MKV, WebM, TS ou AVI";
    }

    /// <summary>O nome do contêiner, ou nulo se o começo não bate com nenhum.</summary>
    public static string? Formato(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return "mp4";
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return "matroska";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'A' && b[9] == 'V' && b[10] == 'I' && b[11] == ' ') return "avi";
        if (b.Length >= 188 * 3 && b[0] == 0x47 && b[188] == 0x47 && b[376] == 0x47) return "ts";
        return null;
    }
}
