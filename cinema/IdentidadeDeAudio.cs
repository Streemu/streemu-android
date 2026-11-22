namespace Streemu.Filme;

/// <summary>
/// Quem é uma faixa de áudio, para achar a MESMA faixa em duas listas que não têm a mesma ordem (o ExoPlayer do anfitrião e o MediaExtractor do envio). Campo de 22/11: num MKV com
/// "PT E-AC-3 5.1" e "EN AAC" o MediaExtractor listou o AAC primeiro e o ExoPlayer o E-AC-3; o menu trocava a faixa do player e o envio continuava na outra. Por posição não dá: por identidade sim.
/// <see cref="TrackId"/> (o número da faixa no arquivo, quando as duas listas o dão) vale só se o codec e os canais conferem; senão casa por codec e canais, e entre as iguais pela ordem.
/// </summary>
public sealed record IdentidadeDeAudio(string Mime, int Canais, string? TrackId, int Ordinal)
{
    /// <summary>O mesmo codec com nomes diferentes entre as duas bibliotecas (E-AC-3 com Atmos vem como "audio/eac3-joc").</summary>
    public static string Normalizar(string? mime) => (mime ?? "").Replace("-joc", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    /// <summary>Numera cada faixa entre as de mesmo codec e canais (a 1ª "ac3 6 canais" é 0, a 2ª é 1...), na ordem da lista.</summary>
    public static List<IdentidadeDeAudio> Numerar(IEnumerable<(string? Mime, int Canais, string? Id)> faixas)
    {
        var vistas = new Dictionary<(string, int), int>();
        var r = new List<IdentidadeDeAudio>();
        foreach (var (mime, canais, id) in faixas)
        {
            var chave = (Normalizar(mime), canais);
            int n = vistas.GetValueOrDefault(chave);
            vistas[chave] = n + 1;
            r.Add(new IdentidadeDeAudio(chave.Item1, canais, string.IsNullOrEmpty(id) ? null : id, n));
        }
        return r;
    }

    /// <summary>
    /// O índice, em <paramref name="lista"/>, da faixa que é esta; -1 se nenhuma. Ordem de tentativas: mesmo número de faixa com mesmo codec e canais; mesmo codec e canais (pela ordem entre as iguais);
    /// só o mesmo codec (um lado pode contar os canais diferente, como o E-AC-3).
    /// </summary>
    public int ProcurarEm(IReadOnlyList<IdentidadeDeAudio> lista)
    {
        if (TrackId != null)
            for (int i = 0; i < lista.Count; i++)
                if (lista[i].TrackId == TrackId && lista[i].Mime == Mime && lista[i].Canais == Canais) return i;
        int porCodecECanais = Escolher(lista, o => o.Mime == Mime && o.Canais == Canais);
        return porCodecECanais >= 0 ? porCodecECanais : Escolher(lista, o => o.Mime == Mime);
    }

    int Escolher(IReadOnlyList<IdentidadeDeAudio> lista, Func<IdentidadeDeAudio, bool> serve)
    {
        var indices = Enumerable.Range(0, lista.Count).Where(i => serve(lista[i])).ToList();
        return indices.Count == 0 ? -1 : indices[Math.Min(Ordinal, indices.Count - 1)];
    }
}
