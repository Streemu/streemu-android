using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streemu.Filme;

/// <summary>O que se sabe do arquivo do filme para decidir se a transmissão serve a quem assiste (no Windows vem do ffmpeg, aqui do MediaExtractor). Os nomes dos codecs são os do ffprobe.</summary>
public sealed record FonteInfo(string Caminho, double DuracaoS, string Video, int Largura, int Altura, double Mbps, bool Hdr, string[] Audios, string[] Legendas, int Capitulos, int Bits = 8, string? TipoHdr = null)
{
    public string Resumo => $"{Largura}x{Altura} {Video}{(Hdr ? " HDR" : "")} · {Mbps:0.#} Mbps · {string.Join("/", Audios.Take(2))}";
}

/// <summary>Um decodificador de vídeo de quem assiste: codec ("h264", "hevc"), bits por canal (8 ou 10) e o maior formato, no texto <c>"3840x2160@60"</c> (spec 028, seção 4).</summary>
public sealed record CapacidadeVideo(string Codec, int Bits, string Max);

/// <summary>
/// O que o aparelho de quem assiste pela transmissão decodifica (spec 028, seção 4); vai no campo <c>capacidade</c> de <c>filme_quero</c> e <c>filme_stream_quero</c>.
/// Cópia do <c>Capacidade.cs</c> do app Windows (que manda só <c>{"plataforma":"windows"}</c>: o mpv toca tudo). Sem o campo (versão antiga) o anfitrião trata como Windows.
/// </summary>
public sealed record Capacidade(string Plataforma, CapacidadeVideo[]? Video = null, string[]? Hdr = null, string[]? Audio = null, bool TelaHdr = false)
{
    public static Capacidade Windows { get; } = new("windows");

    /// <summary>O mpv do Windows toca qualquer arquivo; para ele nenhuma conta é feita.</summary>
    public bool TocaTudo => string.Equals(Plataforma, "windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lê o campo vindo da rede. Nulo se não veio (versão antiga); campo torto não derruba nada: vira o que deu para ler.</summary>
    public static Capacidade? De(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        try
        {
            string plataforma = o["plataforma"]?.GetValue<string>() ?? "";
            if (string.Equals(plataforma, "windows", StringComparison.OrdinalIgnoreCase)) return Windows;
            var video = (o["video"] as JsonArray)?.OfType<JsonObject>()
                .Select(v => new CapacidadeVideo(v["codec"]?.GetValue<string>() ?? "", v["bits"]?.GetValue<int>() ?? 8, v["max"]?.GetValue<string>() ?? "")).ToArray();
            return new Capacidade(plataforma, video, Textos(o["hdr"]), Textos(o["audio"]), o["telaHdr"]?.GetValue<bool>() ?? false);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return new Capacidade(o["plataforma"]?.ToString() ?? "?"); }
    }

    static string[]? Textos(JsonNode? n) => (n as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(s => s != "").ToArray();

    static readonly Regex Formato = new(@"^(\d+)x(\d+)", RegexOptions.Compiled);

    /// <summary>O vídeo do arquivo (codec, bits, tamanho e, se for HDR, a tela) toca neste aparelho?</summary>
    public bool TocaVideo(FonteInfo f)
    {
        if (TocaTudo) return true;
        bool decodifica = (Video ?? []).Any(v => string.Equals(v.Codec, f.Video, StringComparison.OrdinalIgnoreCase) && v.Bits >= f.Bits && Cabe(v.Max, f));
        if (!decodifica) return false;
        // HDR: só passa direto se a tela é HDR e o aparelho conhece o tipo (hdr10, hlg); numa tela comum o anfitrião recomprimiria com tone-map (o celular anfitrião não: avisa e manda baixar)
        return !f.Hdr || (TelaHdr && (f.TipoHdr == null || (Hdr ?? []).Contains(f.TipoHdr, StringComparer.OrdinalIgnoreCase)));
    }

    static bool Cabe(string max, FonteInfo f)
    {
        if (string.IsNullOrWhiteSpace(max)) return true;
        var m = Formato.Match(max);
        if (!m.Success) return false;
        return f.Largura <= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) && f.Altura <= int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>O áudio enviado em cópia direta (codec da faixa) toca neste aparelho? Nulo (sem áudio) sempre toca.</summary>
    public bool TocaAudio(string? codec) =>
        TocaTudo || codec == null || (Audio ?? []).Contains(codec, StringComparer.OrdinalIgnoreCase);

    /// <summary>A cópia direta do arquivo serve a este aparelho (vídeo e o áudio escolhido)? Quem não manda capacidade (nulo) é o Windows de versão antiga: toca.</summary>
    public static bool CopiaServe(Capacidade? c, FonteInfo f, string? codecAudio) => c == null || (c.TocaVideo(f) && c.TocaAudio(codecAudio));
}
