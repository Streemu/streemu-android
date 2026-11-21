using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;

namespace Streemu.Proto;

/// <summary>
/// O que o celular diz de si: modelo, chip, Android, memória, tela e, principalmente, o que o decodificador de H.264 promete aguentar.
/// O decodificador de vídeo é o que mais muda de um celular para outro: chips de entrada (Helio G85/G99, Dimensity 6100+/6300, Snapdragon 680/685)
/// prometem 1440p a 30 ou 1080p a 60, menos que uma live de 1440p60. A resposta é uma estimativa do fabricante, não uma garantia, mas serve para avisar
/// antes e para juntar dados de aparelhos diferentes (vai para o logcat como "APARELHO" e "CAPACIDADE").
/// </summary>
static class Aparelho
{
    public enum Aguenta { Sim, Nao, NaoSei }

    public static string Resumo(Context c)
    {
        string chip = OperatingSystem.IsAndroidVersionAtLeast(31) ? $"{Build.SocManufacturer} {Build.SocModel}" : Build.Hardware ?? "";
        long ramMb = 0;
        try
        {
            var mi = new ActivityManager.MemoryInfo();
            ((ActivityManager)c.GetSystemService(Context.ActivityService)!).GetMemoryInfo(mi);
            ramMb = mi.TotalMem / (1024 * 1024);
        }
        catch { }
        var dm = c.Resources!.DisplayMetrics!;
        int telaW = Math.Max(dm.WidthPixels, dm.HeightPixels), telaH = Math.Min(dm.WidthPixels, dm.HeightPixels);
        return $"{Build.Manufacturer} {Build.Model}, chip {chip}, Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), " +
               $"{string.Join("/", Build.SupportedAbis ?? Array.Empty<string>())}, {ramMb} MB de RAM, tela {telaW}x{telaH}";
    }

    // ------------------------------------------------------------------ perfil para a telemetria (o "perfil-<hash>.json" do Windows, com as peças do celular)

    /// <summary>O que não muda de uma abertura para outra (o hash dele diz quando o celular mudou: Android atualizado, por exemplo).</summary>
    public static JsonObject PerfilEstavel(Context c)
    {
        var dm = c.Resources!.DisplayMetrics!;
        var cfg = c.Resources.Configuration!;
        float hz = 0;
        try { hz = ((Android.Hardware.Display.DisplayManager)c.GetSystemService(Context.DisplayService)!).GetDisplay(0)?.RefreshRate ?? 0; } catch { }
        var o = new JsonObject
        {
            ["plataforma"] = "android",
            ["app"] = new JsonObject { ["versao"] = Streemu.Program.Version, ["canal"] = Telemetria.Canal, ["arquitetura_do_processo"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() },
            ["android"] = new JsonObject
            {
                ["versao"] = Build.VERSION.Release, ["api"] = (int)Build.VERSION.SdkInt, ["patch_seguranca"] = Build.VERSION.SecurityPatch,
                ["abis"] = new JsonArray((Build.SupportedAbis ?? Array.Empty<string>()).Select(a => (JsonNode)a).ToArray()),
            },
            ["aparelho"] = new JsonObject { ["fabricante"] = Build.Manufacturer, ["marca"] = Build.Brand, ["modelo"] = Build.Model, ["codigo"] = Build.Device },
            ["chip"] = new JsonObject
            {
                ["fabricante"] = OperatingSystem.IsAndroidVersionAtLeast(31) ? Build.SocManufacturer : null,
                ["modelo"] = OperatingSystem.IsAndroidVersionAtLeast(31) ? Build.SocModel : null,
                ["hardware"] = Build.Hardware, ["nucleos"] = System.Environment.ProcessorCount,
            },
            ["ram"] = new JsonObject { ["total_mb"] = MemoriaMb(c).Total, ["pouca_memoria"] = ((ActivityManager)c.GetSystemService(Context.ActivityService)!).IsLowRamDevice },
            ["tela"] = new JsonObject
            {
                ["resolucao"] = $"{Math.Max(dm.WidthPixels, dm.HeightPixels)}x{Math.Min(dm.WidthPixels, dm.HeightPixels)}", ["dpi"] = (int)dm.DensityDpi,
                ["hz"] = Math.Round(hz, 1), ["menor_largura_dp"] = cfg.SmallestScreenWidthDp,
            },
            ["decodificadores"] = Decodificadores(),
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) o["android"]!["media_performance_class"] = (int)Build.VERSION.MediaPerformanceClass;
        return o;
    }

    /// <summary>O que muda a toda hora: a rede, a bateria, a temperatura e a memória livre.</summary>
    public static JsonObject PerfilMomento(Context c) => new()
    {
        ["rede"] = Rede(c), ["energia"] = Energia(c), ["termico"] = Termico(c), ["ram_livre_mb"] = MemoriaMb(c).Livre,
        ["hora_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["fuso"] = TimeZoneInfo.Local.BaseUtcOffset.ToString(),
    };

    static (long Total, long Livre) MemoriaMb(Context c)
    {
        try
        {
            var mi = new ActivityManager.MemoryInfo();
            ((ActivityManager)c.GetSystemService(Context.ActivityService)!).GetMemoryInfo(mi);
            return (mi.TotalMem >> 20, mi.AvailMem >> 20);
        }
        catch { return (0, 0); }
    }

    /// <summary>Os decodificadores de H.264, HEVC, AV1 e Opus, com o que cada um promete (o que mais decide como a live roda no celular).</summary>
    static JsonArray Decodificadores()
    {
        var saida = new JsonArray();
        string[] tipos = { MediaFormat.MimetypeVideoAvc, MediaFormat.MimetypeVideoHevc, "video/av01", MediaFormat.MimetypeAudioOpus };
        foreach (var info in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? Array.Empty<MediaCodecInfo>())
        {
            if (info.IsEncoder) continue;
            foreach (var tipo in (info.GetSupportedTypes() ?? Array.Empty<string>()).Where(t => tipos.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                var d = new JsonObject { ["nome"] = info.Name, ["tipo"] = tipo };
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) { d["hardware"] = info.IsHardwareAccelerated; d["so_software"] = info.IsSoftwareOnly; }
                try
                {
                    var cap = info.GetCapabilitiesForType(tipo);
                    if (cap?.VideoCapabilities is { } v)
                    {
                        d["max"] = $"{N(v.SupportedWidths?.Upper):0}x{N(v.SupportedHeights?.Upper):0}";
                        if (OperatingSystem.IsAndroidVersionAtLeast(29) && v.SupportedPerformancePoints is { Count: > 0 } pp)
                            d["pontos_de_desempenho"] = new JsonArray(pp.Select(x => (JsonNode)x.ToString()).ToArray());
                        if (v.IsSizeSupported(1920, 1080) && v.GetAchievableFrameRatesFor(1920, 1080) is { } m1080) d["medido_1080p"] = $"{N(m1080.Lower):0}-{N(m1080.Upper):0}";
                        if (v.IsSizeSupported(2560, 1440) && v.GetAchievableFrameRatesFor(2560, 1440) is { } m1440) d["medido_1440p"] = $"{N(m1440.Lower):0}-{N(m1440.Upper):0}";
                    }
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                        d["baixa_latencia"] = cap?.IsFeatureSupported("low-latency") ?? false;
                }
                catch { }
                saida.Add((JsonNode)d);
            }
        }
        return saida;
    }

    static JsonObject Rede(Context c)
    {
        try
        {
            var cm = (Android.Net.ConnectivityManager)c.GetSystemService(Context.ConnectivityService)!;
            var caps = cm.GetNetworkCapabilities(cm.ActiveNetwork);
            if (caps == null) return new JsonObject { ["tipo"] = "nenhuma" };
            string tipo = caps.HasTransport(Android.Net.TransportType.Wifi) ? "wifi" : caps.HasTransport(Android.Net.TransportType.Cellular) ? "celular"
                        : caps.HasTransport(Android.Net.TransportType.Ethernet) ? "cabo" : "outra";
            var o = new JsonObject
            {
                ["tipo"] = tipo, ["vpn"] = caps.HasTransport(Android.Net.TransportType.Vpn),
                ["descida_estimada_kbps"] = caps.LinkDownstreamBandwidthKbps, ["subida_estimada_kbps"] = caps.LinkUpstreamBandwidthKbps,
                ["limitada"] = !caps.HasCapability(Android.Net.NetCapability.NotMetered),
            };
            if (OperatingSystem.IsAndroidVersionAtLeast(29) && caps.SignalStrength != int.MinValue) o["sinal_dbm"] = caps.SignalStrength;
            if (tipo == "celular")
                try { o["operadora"] = ((Android.Telephony.TelephonyManager)c.GetSystemService(Context.TelephonyService)!).NetworkOperatorName; } catch { }
            return o;
        }
        catch (Exception e) { return new JsonObject { ["erro"] = e.Message }; }
    }

    static JsonObject Energia(Context c)
    {
        try
        {
            var bm = (BatteryManager)c.GetSystemService(Context.BatteryService)!;
            var pm = (PowerManager)c.GetSystemService(Context.PowerService)!;
            return new JsonObject { ["bateria_pct"] = bm.GetIntProperty((int)BatteryProperty.Capacity), ["na_tomada"] = bm.IsCharging, ["economia_de_bateria"] = pm.IsPowerSaveMode };
        }
        catch (Exception e) { return new JsonObject { ["erro"] = e.Message }; }
    }

    /// <summary>Estado térmico do Android 10+ (none, light, moderate, severe, critical...): celular quente estrangula o chip.</summary>
    public static string? Termico(Context c)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return null;
        try { return ((PowerManager)c.GetSystemService(Context.PowerService)!).CurrentThermalStatus.ToString().ToLowerInvariant(); }
        catch { return null; }
    }

    /// <summary>Bateria em %, para a série da live.</summary>
    public static int Bateria(Context c)
    {
        try { return ((BatteryManager)c.GetSystemService(Context.BatteryService)!).GetIntProperty((int)BatteryProperty.Capacity); } catch { return -1; }
    }

    /// <summary>Os Range do Android chegam como Java.Lang.Object: o número dentro deles.</summary>
    static double N(Java.Lang.Object? o) => o is Java.Lang.Number n ? n.DoubleValue() : 0;

    /// <summary>"1440p 60 FPS" (o texto da qualidade que a sala manda) -> 2560x1440 a 60. Nulo se não der para ler.</summary>
    public static (int W, int H, int Fps)? LerQualidade(string qualidade)
    {
        var m = Regex.Match(qualidade, @"(\d{3,4})p\D*(\d{2,3})", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), fps = int.Parse(m.Groups[2].Value);
        int w = (h * 16 / 9 + 1) & ~1;   // as lives são 16:9
        return (w, h, fps);
    }

    /// <summary>O decodificador de H.264 que o Android escolheria (o primeiro da lista, que é a ordem de preferência do fabricante).</summary>
    static MediaCodecInfo? DecodificadorH264()
    {
        var lista = new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? Array.Empty<MediaCodecInfo>();
        var candidatos = lista.Where(i => !i.IsEncoder && (i.GetSupportedTypes() ?? Array.Empty<string>())
                                     .Any(t => t.Equals(MediaFormat.MimetypeVideoAvc, StringComparison.OrdinalIgnoreCase))).ToList();
#pragma warning disable CA1416 // dentro do if da versão (o analisador não enxerga dentro do lambda)
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) return candidatos.FirstOrDefault(i => !i.IsSoftwareOnly) ?? candidatos.FirstOrDefault();
#pragma warning restore CA1416
        return candidatos.FirstOrDefault(i => !(i.Name ?? "").StartsWith("OMX.google.", StringComparison.OrdinalIgnoreCase)
                                             && !(i.Name ?? "").StartsWith("c2.android.", StringComparison.OrdinalIgnoreCase)) ?? candidatos.FirstOrDefault();
    }

    /// <summary>
    /// O decodificador aguenta este tamanho e esta taxa? Confere o que o fabricante declara: os "pontos de desempenho" (Android 10+, quando o fabricante
    /// preenche) e, senão, as taxas de quadros declaradas para o tamanho. <paramref name="detalhe"/> diz o que foi visto, para o log.
    /// </summary>
    public static Aguenta AguentaH264(int w, int h, int fps, out string detalhe)
    {
        try
        {
            var info = DecodificadorH264();
            var cap = info?.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)?.VideoCapabilities;
            if (info == null || cap == null) { detalhe = "nenhum decodificador de H.264 encontrado"; return Aguenta.NaoSei; }
            string nome = info.Name ?? "?";
            string maximo = $"até {N(cap.SupportedWidths?.Upper):0}x{N(cap.SupportedHeights?.Upper):0}";
            if (!cap.IsSizeSupported(w, h)) { detalhe = $"{nome}: {w}x{h} fora do tamanho suportado ({maximo})"; return Aguenta.Nao; }

            if (OperatingSystem.IsAndroidVersionAtLeast(29) && cap.SupportedPerformancePoints is { Count: > 0 } pontos)
            {
                var pedido = new MediaCodecInfo.VideoCapabilities.PerformancePoint(w, h, fps);
#pragma warning disable CA1416 // idem
                bool cobre = pontos.Any(p => p.Covers(pedido));
#pragma warning restore CA1416
                detalhe = $"{nome}: pontos de desempenho {(cobre ? "cobrem" : "NÃO cobrem")} {w}x{h}@{fps} ({string.Join("; ", pontos.Take(4).Select(p => p.ToString()))})";
                return cobre ? Aguenta.Sim : Aguenta.Nao;
            }
            // sem pontos de desempenho: a taxa declarada para o tamanho (um teto teórico; o "achieved" é a medida do fabricante, quando existe)
            var taxas = cap.GetSupportedFrameRatesFor(w, h);
            var medidas = cap.GetAchievableFrameRatesFor(w, h);
            string m = medidas != null ? $", medido pelo fabricante {N(medidas.Lower):0}-{N(medidas.Upper):0}" : "";
            detalhe = $"{nome}: {w}x{h} até {N(taxas?.Upper):0} quadros/s declarados{m} ({maximo})";
            if (medidas != null) return N(medidas.Upper) >= fps * 0.9 ? Aguenta.Sim : Aguenta.Nao;
            return N(taxas?.Upper) >= fps ? Aguenta.Sim : Aguenta.Nao;
        }
        catch (Exception e) { detalhe = "não deu para perguntar ao decodificador: " + e.Message; return Aguenta.NaoSei; }
    }
}
