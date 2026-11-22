using Android.Content;
using Android.Media;
using AndroidX.Media3.ExoPlayer;
using Streemu.Filme;
using Streemu.Proto.Transmissao;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>Uma faixa de áudio do arquivo. <see cref="Indice"/> é a posição entre as faixas de áudio (a mesma ordem dos grupos de áudio do ExoPlayer); <see cref="Trilha"/>, o número da faixa no MediaExtractor.</summary>
sealed record FaixaDeAudio(int Indice, int Trilha, string Idioma, string NomeCodec, CodecAudioTs Ts, byte[]? Asc, int Canais, string Mime = "", string? TrackId = null)
{
    /// <summary>Vai em cópia direta? Só AAC (com o AudioSpecificConfig), AC-3, E-AC-3 e MP3; o resto (DTS, Opus, TrueHD...) pede recompressão.</summary>
    public bool PodeCopiar => Ts != CodecAudioTs.Nenhum && (Ts != CodecAudioTs.Aac || Asc != null);
}

/// <summary>
/// O que o <see cref="SondaFilme"/> achou no arquivo: o <see cref="FonteInfo"/> (para a checagem de capacidade) e o que o <see cref="FonteExtrator"/> precisa para montar o mpegts.
/// <see cref="Escolhida"/> é a faixa de áudio enviada (índice em <see cref="Audios"/>, -1 sem áudio): o anfitrião toca a mesma, senão ele ouve uma língua e a transmissão leva outra.
/// </summary>
sealed record FaixasDoFilme(FonteInfo InfoBase, int Video, bool Hevc, byte[] ParametrosVideo, long QuadroUs, IReadOnlyList<FaixaDeAudio> Audios, int Escolhida)
{
    public FaixaDeAudio? Audio => Escolhida >= 0 ? Audios[Escolhida] : null;
    public int TrilhaAudio => Audio?.Trilha ?? -1;
    public CodecAudioTs CodecAudio => Audio?.Ts ?? CodecAudioTs.Nenhum;
    public int Canais => Audio?.Canais ?? 2;
    public byte[]? AscAac => Audio?.Asc;
    /// <summary>O <see cref="FonteInfo"/> com a faixa de áudio escolhida (a capacidade de quem assiste é checada contra ela).</summary>
    public FonteInfo Info => InfoBase with { Audios = Audio is { } a ? new[] { a.NomeCodec } : Array.Empty<string>() };
    public FaixasDoFilme ComAudio(int indice) => this with { Escolhida = indice };
    /// <summary>Quem é cada faixa de áudio (codec, canais, número no arquivo), para casar com as do ExoPlayer, que vêm em outra ordem.</summary>
    public List<IdentidadeDeAudio> Identidades() => IdentidadeDeAudio.Numerar(Audios.Select(a => ((string?)a.Mime, a.Canais, a.TrackId)));
}

/// <summary>
/// Lê o arquivo do filme com o MediaExtractor e diz se ele pode ir em cópia direta (spec 028, 5.2): vídeo H.264 ou HEVC, e áudio AAC, AC-3, E-AC-3 ou MP3 (ou sem áudio). O resto (AV1, Opus, DTS)
/// pede recompressão, que o celular só faz na F5b: nesse caso devolve nulo e o anúncio sai com <c>stream=false</c> (quem entra baixa o arquivo).
/// O áudio enviado é o da faixa que casa com o idioma do aparelho (a primeira, se nenhuma casa), e o anfitrião é forçado a tocar a mesma (<see cref="PlayerFilme.ForcarAudio"/>);
/// <paramref name="indiceAudio"/> escolhe outra (menu de faixas).
/// </summary>
static class SondaFilme
{
    /// <summary>As faixas do arquivo, ou nulo se ele não vai em cópia direta (o motivo vai para o log).</summary>
    public static FaixasDoFilme? Sondar(Context contexto, Android.Net.Uri arquivo, string nome, int? indiceAudio = null)
    {
        MediaExtractorCompat? ex = null;
        try
        {
            ex = new MediaExtractorCompat(contexto);   // o extrator do Media3, o mesmo do ExoPlayer: o do Android não lista o E-AC-3 do MKV (campo 22/11) e põe as faixas em outra ordem
            ex.SetDataSource(contexto, arquivo, null);
            return Montar(ex, nome, TamanhoDo(contexto, arquivo), indiceAudio);
        }
        catch (Exception e) { Logcat.Warn("Streemu", "FILME sonda: " + e.Message); return null; }
        finally { ex?.Release(); }
    }

    static FaixasDoFilme? Montar(MediaExtractorCompat ex, string nome, long bytes, int? indiceAudio)
    {
        int v = -1;
        var audios = new List<FaixaDeAudio>();
        for (int i = 0; i < ex.TrackCount; i++)
        {
            var f = ex.GetTrackFormat(i);
            string mime = f.GetString(MediaFormat.KeyMime) ?? "";
            if (v < 0 && mime.StartsWith("video/")) v = i;
            else if (mime.StartsWith("audio/")) audios.Add(FaixaDe(f, audios.Count, i));
        }
        if (v < 0) return Recusa("sem faixa de vídeo");
        var fv = ex.GetTrackFormat(v);
        string mimeV = fv.GetString(MediaFormat.KeyMime) ?? "";
        bool hevc = mimeV == "video/hevc";
        if (!hevc && mimeV != "video/avc") return Recusa($"vídeo {mimeV} pede recompressão");
        int escolhida = EscolherAudio(audios, indiceAudio);
        if (audios.Count > 0 && escolhida < 0) return Recusa("nenhuma faixa de áudio vai em cópia direta (" + string.Join(", ", audios.Select(x => x.NomeCodec == "" ? "?" : x.NomeCodec)) + ")");
        var csd = ParametrosAnnexB(fv);
        var info = Info(fv, nome, bytes, hevc, csd);
        long quadroUs = fv.ContainsKey(MediaFormat.KeyFrameRate) && fv.GetInteger(MediaFormat.KeyFrameRate) > 0 ? 1_000_000L / fv.GetInteger(MediaFormat.KeyFrameRate) : 41_667;
        return new FaixasDoFilme(info, v, hevc, csd, quadroUs, audios, escolhida);
    }

    /// <summary>
    /// A faixa enviada: a pedida, ou a primeira que vai em cópia e casa com o idioma do aparelho (ISO 639-2, como o MKV marca), ou a primeira que vai em cópia. -1 se não há faixa de áudio
    /// ou a pedida não vai em cópia direta.
    /// </summary>
    static int EscolherAudio(IReadOnlyList<FaixaDeAudio> audios, int? pedida)
    {
        if (pedida is int p) return p >= 0 && p < audios.Count && audios[p].PodeCopiar ? p : -1;
        string idiomaDoAparelho = "";
        try { idiomaDoAparelho = Java.Util.Locale.Default?.ISO3Language ?? ""; } catch (Exception e) { Logcat.Warn("Streemu", "FILME idioma do aparelho: " + e.Message); }
        var lista = audios.ToList();
        int casa = idiomaDoAparelho == "" ? -1 : lista.FindIndex(a => a.PodeCopiar && a.Idioma == idiomaDoAparelho);
        return casa >= 0 ? casa : lista.FindIndex(a => a.PodeCopiar);
    }

    static FaixaDeAudio FaixaDe(MediaFormat f, int indice, int trilha)
    {
        string mime = f.GetString(MediaFormat.KeyMime) ?? "";
        int canais = f.ContainsKey(MediaFormat.KeyChannelCount) ? f.GetInteger(MediaFormat.KeyChannelCount) : 2;
        var (codec, nome) = mime switch
        {
            "audio/mp4a-latm" => (CodecAudioTs.Aac, "aac"),
            "audio/ac3" => (CodecAudioTs.Ac3, "ac3"),
            "audio/eac3" => (CodecAudioTs.Eac3, "eac3"),
            "audio/mpeg" => (CodecAudioTs.Mp3, "mp3"),
            _ => (CodecAudioTs.Nenhum, mime.Replace("audio/", "")),
        };
        string idioma = f.ContainsKey("language") ? f.GetString("language") ?? "" : "";
        string? idDaFaixa = f.ContainsKey("track-id") ? f.GetInteger("track-id").ToString() : null;   // número da faixa no arquivo (Android 9+)
        return new FaixaDeAudio(indice, trilha, idioma, nome, codec, codec == CodecAudioTs.Aac ? Bytes(f, "csd-0") : null, canais, mime, idDaFaixa);
    }

    static FaixasDoFilme? Recusa(string motivo) { Logcat.Info("Streemu", "FILME cópia direta indisponível: " + motivo); return null; }

    /// <summary>VPS/SPS/PPS do arquivo em Annex B (o csd-0 e o csd-1 do MediaFormat já vêm com 00 00 00 01): o que vai na frente de cada quadro-chave.</summary>
    static byte[] ParametrosAnnexB(MediaFormat f) => (Bytes(f, "csd-0") ?? Array.Empty<byte>()).Concat(Bytes(f, "csd-1") ?? Array.Empty<byte>()).ToArray();

    static byte[]? Bytes(MediaFormat f, string chave)
    {
        if (!f.ContainsKey(chave)) return null;
        var b = f.GetByteBuffer(chave)?.Duplicate();
        if (b == null) return null;
        var r = new byte[b.Remaining()];
        b.Get(r);
        return r;
    }

    static FonteInfo Info(MediaFormat fv, string nome, long bytes, bool hevc, byte[] csd)
    {
        double dur = fv.ContainsKey(MediaFormat.KeyDuration) ? fv.GetLong(MediaFormat.KeyDuration) / 1e6 : 0;
        int w = fv.GetInteger(MediaFormat.KeyWidth), h = fv.GetInteger(MediaFormat.KeyHeight);
        int transfer = fv.ContainsKey(MediaFormat.KeyColorTransfer) ? fv.GetInteger(MediaFormat.KeyColorTransfer) : 0;
        string? tipoHdr = transfer == 6 ? "hdr10" : transfer == 7 ? "hlg" : null;   // ST2084 e HLG
        double mbps = dur > 0 && bytes > 0 ? bytes * 8 / dur / 1e6 : 8;
        return new FonteInfo(nome, dur, hevc ? "hevc" : "h264", w, h, mbps, tipoHdr != null, Array.Empty<string>(), Array.Empty<string>(), 0,
            FilmeTs.BitsDoCsd(csd, hevc), tipoHdr);
    }

    static long TamanhoDo(Context c, Android.Net.Uri uri)
    {
        try { using var pfd = c.ContentResolver!.OpenFileDescriptor(uri, "r"); return pfd?.StatSize ?? 0; }
        catch (Exception e) { Logcat.Warn("Streemu", "FILME tamanho do arquivo: " + e.Message); return 0; }
    }
}
