using System.Text.Json.Nodes;
using Android.Content;
using Android.Media;
using Android.Views;

namespace Streemu.Proto.Filme;

/// <summary>
/// O que este celular decodifica, no campo <c>capacidade</c> de <c>filme_quero</c> e <c>filme_stream_quero</c> (spec 028, seção 4; o Windows lê com <c>Capacidade.De</c>).
/// Os nomes dos codecs são os do ffprobe (h264, hevc, av1; aac, opus, mp3, ac3, eac3, dts), que é com o que o anfitrião compara o arquivo.
/// </summary>
static class CapacidadeAparelho
{
    static readonly (int W, int H)[] Tamanhos = { (7680, 4320), (3840, 2160), (2560, 1440), (1920, 1080), (1280, 720), (854, 480) };

    public static JsonObject Json(Context c) => Montar(Decodificadores(), TiposHdr(c));

    /// <summary>Separado só para o teste conferir o formato sem o aparelho.</summary>
    internal static JsonObject Montar(List<(string Codec, int Bits, string Max)> video, List<string> hdr, string[]? audio = null)
    {
        var o = new JsonObject
        {
            ["plataforma"] = "android",
            ["video"] = new JsonArray(video.Select(v => (JsonNode)new JsonObject { ["codec"] = v.Codec, ["bits"] = v.Bits, ["max"] = v.Max }).ToArray()),
            ["hdr"] = new JsonArray(hdr.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["audio"] = new JsonArray((audio ?? Audio()).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["telaHdr"] = hdr.Count > 0,
        };
        return o;
    }

    static List<(string Codec, int Bits, string Max)> Decodificadores()
    {
        var r = new List<(string, int, string)>();
        foreach (var (codec, mime) in new[] { ("h264", MediaFormat.MimetypeVideoAvc), ("hevc", MediaFormat.MimetypeVideoHevc), ("av1", "video/av01") })
        {
            var infos = DecodificadoresDe(mime);
            if (infos.Count == 0) continue;
            string? melhor = null; long area = 0; bool dezBits = false;
            foreach (var info in infos)
            {
                MediaCodecInfo.CodecCapabilities? cap;
                try { cap = info.GetCapabilitiesForType(mime); } catch { continue; }
                var v = cap?.VideoCapabilities;
                if (v == null) continue;
                foreach (var (w, h) in Tamanhos)
                {
                    if (!v.IsSizeSupported(w, h)) continue;
                    double fps = 0;
                    try { fps = N(v.GetSupportedFrameRatesFor(w, h)?.Upper); } catch { }
                    if (fps <= 0) continue;
                    if ((long)w * h > area) { area = (long)w * h; melhor = $"{w}x{h}@{Math.Min(120, (int)fps)}"; }
                    break;   // o maior tamanho deste decodificador; o fps é o dele
                }
                dezBits |= DezBits(cap!, mime);
            }
            if (melhor != null) r.Add((codec, dezBits ? 10 : 8, melhor));
        }
        return r;
    }

    /// <summary>Decodificadores com aceleração por hardware (o de software não aguenta filme; sem a informação do sistema, os que não são do Google).</summary>
    static List<MediaCodecInfo> DecodificadoresDe(string mime)
    {
        var todos = (new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? Array.Empty<MediaCodecInfo>())
            .Where(i => !i.IsEncoder && (i.GetSupportedTypes() ?? Array.Empty<string>()).Any(t => t.Equals(mime, StringComparison.OrdinalIgnoreCase))).ToList();
#pragma warning disable CA1416
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return todos.Where(i => i.IsHardwareAccelerated && !i.IsSoftwareOnly).ToList();
#pragma warning restore CA1416
        return todos.Where(i => !(i.Name ?? "").StartsWith("OMX.google.", StringComparison.OrdinalIgnoreCase) && !(i.Name ?? "").StartsWith("c2.android.", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static bool DezBits(MediaCodecInfo.CodecCapabilities cap, string mime)
    {
        var perfis = cap.ProfileLevels ?? Array.Empty<MediaCodecInfo.CodecProfileLevel>();
        foreach (var p in perfis)
        {
            var perfil = p.Profile;
            if (mime == MediaFormat.MimetypeVideoHevc && perfil == MediaCodecProfileType.Hevcprofilemain10) return true;
            if (mime == MediaFormat.MimetypeVideoHevc && perfil == MediaCodecProfileType.Hevcprofilemain10hdr10) return true;
            if (mime == MediaFormat.MimetypeVideoAvc && perfil == MediaCodecProfileType.Avcprofilehigh10) return true;
            if (mime == "video/av01" && perfil == MediaCodecProfileType.Av1profilemain10) return true;
        }
        return false;
    }

    /// <summary>aac, opus e mp3 o ExoPlayer toca sempre (software dele); ac3, eac3 e dts só com decodificador no aparelho.</summary>
    static string[] Audio()
    {
        var l = new List<string> { "aac", "opus", "mp3" };
        if (TemDecodificador("audio/ac3")) l.Add("ac3");
        if (TemDecodificador("audio/eac3")) l.Add("eac3");
        if (TemDecodificador("audio/vnd.dts") || TemDecodificador("audio/vnd.dts.hd")) l.Add("dts");
        if (TemDecodificador("audio/mpeg-L2")) l.Add("mp2");
        return l.ToArray();
    }

    static bool TemDecodificador(string mime)
    {
        try
        {
            return (new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? Array.Empty<MediaCodecInfo>())
                .Any(i => !i.IsEncoder && (i.GetSupportedTypes() ?? Array.Empty<string>()).Any(t => t.Equals(mime, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    /// <summary>Os tipos de HDR que a tela mostra (hdr10, hlg, hdr10plus, dolbyvision). Vazio numa tela comum.</summary>
    static List<string> TiposHdr(Context c)
    {
        var l = new List<string>();
        try
        {
            var tela = c.GetSystemService(Context.DisplayService) is Android.Hardware.Display.DisplayManager dm ? dm.GetDisplay(0) : null;
            if (tela == null || !OperatingSystem.IsAndroidVersionAtLeast(26)) return l;
            var tipos = new List<int>();
#pragma warning disable CA1416, CA1422
            if (tela.GetHdrCapabilities()?.GetSupportedHdrTypes() is { } b) foreach (var x in b) tipos.Add((int)x);
#pragma warning restore CA1416, CA1422
            foreach (int t in tipos)
            {
                string? nome = t switch { 1 => "dolbyvision", 2 => "hdr10", 3 => "hlg", 4 => "hdr10plus", _ => null };
                if (nome != null && !l.Contains(nome)) l.Add(nome);
            }
        }
        catch { }
        return l;
    }

    static double N(Java.Lang.Object? o) => o is Java.Lang.Number n ? n.DoubleValue() : 0;
}
