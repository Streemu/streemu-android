using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Log = Streemu.Util.Log;

// O que a captura e o serviço de transmissão precisam declarar (vale no Debug e no Release): o manifesto sai dos atributos, não do AndroidManifest.xml.
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION")]
[assembly: UsesPermission(Android.Manifest.Permission.RecordAudio)]
[assembly: UsesPermission(Android.Manifest.Permission.PostNotifications)]

namespace Streemu.Proto.Transmissao;

/// <summary>Qualidade pedida. A captura pode ajustar (largura/altura múltiplas de 16, teto do encoder): o que valeu sai em <see cref="Captura.Largura"/>/<see cref="Captura.Altura"/>.</summary>
sealed record ConfigCaptura(int Largura, int Altura, int Fps, int Kbps, bool Audio);

/// <summary>
/// Tela do celular (MediaProjection → VirtualDisplay → MediaCodec H.264 pela Surface de entrada) e, com <see cref="ConfigCaptura.Audio"/>,
/// o som dos apps (AudioPlaybackCapture → PCM 48 kHz → MediaCodec Opus). Baixa latência como o app Windows: CBR, sem quadros B, IDR a cada 2 s,
/// SPS/PPS antes de cada IDR, fps constante (um passo OpenGL num relógio fixo redesenha o último quadro quando a tela fica parada).
///
/// Os dois relógios são o mesmo: µs de <c>System.nanoTime()</c>/1000 (o mesmo do presentationTimeUs da Surface).
/// Os eventos saem nas threads da captura; quem recebe não deve demorar.
/// Quem cria a <see cref="MediaProjection"/> (o serviço em primeiro plano) é dono dela; a captura só usa. Ela registra um callback próprio na projeção
/// (o Android 14 exige um antes do VirtualDisplay) e o tira no Dispose.
///
/// Rotação: ao girar o celular a captura cria um encoder novo com o tamanho novo (o primeiro quadro dele é IDR, com SPS/PPS novos), troca a Surface do
/// VirtualDisplay e solta o encoder velho. Quem recebe o vídeo vê o SPS mudar (o Decodificador já reconfigura sozinho) e o evento <see cref="Tamanho"/>.
/// </summary>
sealed class Captura : IDisposable
{
    const string Tag = "Captura";
    const int AudioHz = 48000;
    const int AudioKbps = 128;

    readonly Context _contexto;
    readonly MediaProjection _projecao;
    readonly ConfigCaptura _config;
    readonly object _gate = new();
    int _descartado;
    bool _iniciado;

    // ---- vídeo
    /// <summary>Um encoder com a thread de saída dele. Na rotação nasce outro; o velho para antes de o novo soltar quadros.</summary>
    sealed class Vida(MediaCodec codec, Surface entrada)
    {
        public readonly MediaCodec Codec = codec;
        public readonly Surface Entrada = entrada;
        public volatile bool Parar;
        public Thread? Saida;
        public byte[]? Config;   // SPS+PPS (codec config), na frente de todo IDR que vier sem eles
    }
    Vida? _vida;
    VirtualDisplay? _vd;
    MediaCodecInfo? _info;
    string _vendorChaves = "";
    int _dpi;
    HandlerThread? _threadAvisos;
    Handler? _avisos;
    Callback? _callbackProjecao;
    ListenerTela? _listenerTela;
    Android.Hardware.Display.DisplayManager? _dm;
    CapturaGl? _gl;

    // ---- áudio
    MediaCodec? _opus;
    AudioRecord? _gravador;
    Thread? _threadAudioEntrada, _threadAudioSaida;
    volatile bool _pararAudio;

    // ---- números (a cada 2 s em CAPTURA-NUMEROS)
    long _nQuadros, _nBytes, _nIdr, _nAudio, _maxEsperaUs;
    long _tUltimo;
    Thread? _threadNumeros;
    readonly ManualResetEventSlim _fim = new(false);

    public Captura(Context contexto, MediaProjection projecao, ConfigCaptura config)
    {
        _contexto = contexto;
        _projecao = projecao;
        _config = config;
        Fps = config.Fps;
        Largura = config.Largura;
        Altura = config.Altura;
        // decidido já aqui (o serviço lê antes de Iniciar para criar o mux com ou sem áudio); se o AudioRecord falhar depois, só deixa de emitir Audio
        CanaisAudio = config.Audio && TemAudio() ? 2 : 0;
    }

    bool TemAudio()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) { Log.Info($"{Tag}: sem áudio (precisa do Android 10)."); return false; }
        if (_contexto.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        { Log.Info($"{Tag}: sem áudio (permissão RECORD_AUDIO não concedida)."); return false; }
        bool encoder = new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos()!.Any(ci => ci.IsEncoder && ci.GetSupportedTypes()!.Any(t => t.Equals("audio/opus", StringComparison.OrdinalIgnoreCase)));
        if (!encoder) Log.Info($"{Tag}: sem áudio (o celular não tem encoder Opus).");
        return encoder;
    }

    /// <summary>Um quadro: Annex-B, PTS em µs, se é quadro-chave (IDR, já com SPS/PPS na frente).</summary>
    public event Action<byte[], long, bool>? Video;
    /// <summary>Um pacote Opus cru e o PTS em µs.</summary>
    public event Action<byte[], long>? Audio;
    /// <summary>Parou sozinha (a pessoa tirou a permissão, o encoder morreu...). Texto para a tela.</summary>
    public event Action<string>? Falhou;
    /// <summary>O tamanho do vídeo mudou (rotação). Largura e altura novas.</summary>
    public event Action<int, int>? Tamanho;

    public int Largura { get; private set; }
    public int Altura { get; private set; }
    public int Fps { get; private set; }
    /// <summary>Canais do Opus (1 ou 2); 0 sem áudio (sem permissão ou o Android não deixou).</summary>
    public int CanaisAudio { get; private set; }
    /// <summary>Nome do encoder de vídeo escolhido (para log e telemetria).</summary>
    public string Encoder { get; private set; } = "";

    static long AgoraUs() => Java.Lang.JavaSystem.NanoTime() / 1000;

    // ------------------------------------------------------------------ início

    public void Iniciar()
    {
        lock (_gate)
        {
            if (_descartado != 0) throw new ObjectDisposedException(nameof(Captura));
            if (_iniciado) return;
            _iniciado = true;
            try { IniciarSob(); }
            catch { Dispose(); throw; }
        }
    }

    void IniciarSob()
    {
        _threadAvisos = new HandlerThread("captura-avisos");
        _threadAvisos.Start();
        _avisos = new Handler(_threadAvisos.Looper!);

        _dm = (Android.Hardware.Display.DisplayManager)_contexto.GetSystemService(Context.DisplayService)!;
        var (telaW, telaH, dpi) = TamanhoDaTela();
        _dpi = dpi;
        _info = EscolherEncoder(telaW, telaH, out int w, out int h, out int fps)
            ?? throw new InvalidOperationException("Este celular não tem encoder H.264 que aceite esta resolução.");
        Encoder = _info.Name!;
        Largura = w; Altura = h; Fps = fps;

        // o Android 14+ exige um callback registrado antes de criar o VirtualDisplay
        _callbackProjecao = new Callback(this);
        _projecao.RegisterCallback(_callbackProjecao, _avisos);

        var vida = AbrirEncoder(w, h);
        _vida = vida;
        // VirtualDisplay -> SurfaceTexture -> GL num relógio fixo -> encoder: fps constante mesmo com a tela parada (o encoder da Qualcomm não solta nada sem quadro novo)
        _gl = new CapturaGl(vida.Entrada, w, h, fps, _avisos, Falha);
        _gl.Iniciar();
        _vd = _projecao.CreateVirtualDisplay("Streemu", w, h, _dpi, (DisplayFlags)16 /* AUTO_MIRROR */, _gl.EntradaVd, null, _avisos)
            ?? throw new InvalidOperationException("O Android não criou o VirtualDisplay da captura.");
        IniciarSaida(vida);

        _listenerTela = new ListenerTela(this);
        _dm.RegisterDisplayListener(_listenerTela, _avisos);

        IniciarAudio();

        Log.Info($"CAPTURA encoder {Encoder}, {Largura}x{Altura}, {Fps} fps, {_config.Kbps} kbps, áudio {(_gravador != null ? $"opus {CanaisAudio} canais {AudioKbps} kbps" : "desligado")}" +
                 (_vendorChaves.Length > 0 ? $", chaves do fabricante: {_vendorChaves}" : ""));

        _tUltimo = AgoraUs();
        _threadNumeros = new Thread(LacoNumeros) { IsBackground = true, Name = "captura-numeros" };
        _threadNumeros.Start();
    }

    /// <summary>Tamanho real da tela agora (já na orientação atual) e a densidade.</summary>
    (int W, int H, int Dpi) TamanhoDaTela()
    {
        var dm = new DisplayMetrics();
#pragma warning disable CA1422
        _dm!.GetDisplay(0)!.GetRealMetrics(dm);
#pragma warning restore CA1422
        return (dm.WidthPixels, dm.HeightPixels, (int)dm.DensityDpi);
    }

    // ------------------------------------------------------------------ escolha do encoder e da resolução

    MediaCodecInfo? EscolherEncoder(int telaW, int telaH, out int w, out int h, out int fps)
    {
        w = h = fps = 0;
        var candidatos = new List<(MediaCodecInfo Info, bool Hw)>();
        foreach (var ci in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos()!)
        {
            if (!ci.IsEncoder) continue;
            if (!ci.GetSupportedTypes()!.Any(t => string.Equals(t, MediaFormat.MimetypeVideoAvc, StringComparison.OrdinalIgnoreCase))) continue;
            string n = ci.Name!.ToLowerInvariant();
            bool hw = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? ci.IsHardwareAccelerated && !ci.IsSoftwareOnly
                : !n.StartsWith("omx.google.") && !n.StartsWith("c2.android.");
            candidatos.Add((ci, hw));
        }
        foreach (var (ci, hw) in candidatos.OrderByDescending(c => c.Hw))
        {
            var caps = ci.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)!.VideoCapabilities!;
            if (!TamanhoPara(telaW, telaH, caps, out w, out h)) continue;
            fps = _config.Fps;
            var faixa = caps.GetSupportedFrameRatesFor(w, h);
            if (faixa?.Upper is Java.Lang.Number teto && teto.DoubleValue() > 0 && fps > teto.DoubleValue()) fps = (int)teto.DoubleValue();
            if (!hw) Log.Info($"{Tag}: sem encoder H.264 de hardware, usando {ci.Name} (software).");
            return ci;
        }
        return null;
    }

    /// <summary>Mantém a proporção da tela, dentro da caixa pedida (lado maior = o maior de Largura/Altura), em múltiplos de 16 e no que o encoder aceita.</summary>
    bool TamanhoPara(int telaW, int telaH, MediaCodecInfo.VideoCapabilities caps, out int w, out int h)
    {
        bool retrato = telaH > telaW;
        int longo = Math.Max(_config.Largura, _config.Altura), curto = Math.Min(_config.Largura, _config.Altura);
        double caixaW = retrato ? curto : longo, caixaH = retrato ? longo : curto;
        double escala = Math.Min(1.0, Math.Min(caixaW / telaW, caixaH / telaH));
        for (int i = 0; i < 40; i++, escala *= 0.92)
        {
            w = Math.Max(16, (int)Math.Round(telaW * escala / 16.0) * 16);
            h = Math.Max(16, (int)Math.Round(telaH * escala / 16.0) * 16);
            if (caps.IsSizeSupported(w, h)) return true;
        }
        w = h = 0;
        return false;
    }

    // ------------------------------------------------------------------ encoder de vídeo

    Vida AbrirEncoder(int w, int h)
    {
        // 1ª tentativa com todas as chaves; se o encoder recusar alguma na configuração, tenta de novo só com o essencial
        for (int nivel = 0; ; nivel++)
        {
            MediaCodec? codec = null;
            try
            {
                codec = MediaCodec.CreateByCodecName(Encoder)!;
                var fmt = FormatoVideo(w, h, nivel);
                codec.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
                var entrada = codec.CreateInputSurface()!;
                codec.Start();
                return new Vida(codec, entrada);
            }
            catch (Exception e) when (nivel == 0)
            {
                Log.Info($"{Tag}: o encoder {Encoder} recusou a configuração completa ({e.Message}); tentando só o essencial.");
                try { codec?.Release(); } catch { }
                codec?.Dispose();
            }
            catch
            {
                try { codec?.Release(); } catch { }
                codec?.Dispose();
                throw;
            }
        }
    }

    MediaFormat FormatoVideo(int w, int h, int nivel)
    {
        var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, w, h);
        fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        fmt.SetInteger(MediaFormat.KeyBitRate, _config.Kbps * 1000);
        fmt.SetInteger(MediaFormat.KeyFrameRate, Fps);
        fmt.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        fmt.SetInteger(MediaFormat.KeyBitrateMode, 2 /* BITRATE_MODE_CBR */);
        if (nivel > 0) return fmt;

        fmt.SetInteger(MediaFormat.KeyPriority, 0);   // tempo real
        fmt.SetInteger(MediaFormat.KeyOperatingRate, Fps);
        fmt.SetInteger("max-bframes", 0);   // MediaFormat.KEY_MAX_B_FRAMES (API 29)
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) fmt.SetInteger("prepend-sps-pps-to-idr-frames", 1);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            fmt.SetInteger(MediaFormat.KeyLowLatency, 1);
            fmt.SetInteger("latency", 1);   // MediaFormat.KEY_LATENCY
        }
        // perfil High, senão Main (o nível o encoder escolhe)
        var perfis = _info!.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)!.ProfileLevels!.Select(p => p.Profile).ToHashSet();
        if (perfis.Contains(MediaCodecProfileType.Avcprofilehigh)) fmt.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilehigh);
        else if (perfis.Contains(MediaCodecProfileType.Avcprofilemain)) fmt.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilemain);
        _vendorChaves = ChavesDoFabricante(fmt, Encoder);
        return fmt;
    }

    /// <summary>Baixa latência própria do fabricante no encoder (como no Decodificador: cada chave só vai para o dono dela, o resto ignora).</summary>
    static string ChavesDoFabricante(MediaFormat fmt, string nome)
    {
        var n = nome.ToLowerInvariant();
        var ligadas = new List<string>();
        void Ligar(string k, int v) { fmt.SetInteger(k, v); ligadas.Add(k); }
        if (n.StartsWith("omx.qcom") || n.StartsWith("c2.qti")) Ligar("vendor.qti-ext-enc-low-latency.enable", 1);
        else if (n.StartsWith("c2.exynos") || n.StartsWith("omx.exynos")) Ligar("vendor.rtc-ext-enc-low-latency.enable", 1);
        return string.Join(", ", ligadas);
    }

    void IniciarSaida(Vida v)
    {
        v.Saida = new Thread(() => LacoVideo(v)) { IsBackground = true, Name = "captura-video" };
        v.Saida.Start();
    }

    void PararVida(Vida v)
    {
        v.Parar = true;
        try { if (v.Saida != null && v.Saida != Thread.CurrentThread) v.Saida.Join(1500); } catch { }
        try { v.Codec.Stop(); } catch { }
        try { v.Codec.Release(); } catch { }
        try { v.Entrada.Release(); } catch { }
        v.Codec.Dispose();
    }

    void LacoVideo(Vida v)
    {
        Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentDisplay);
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (!v.Parar && _descartado == 0)
            {
                int i = v.Codec.DequeueOutputBuffer(info, 100_000);
                if (i < 0) continue;   // sem saída ainda / formato mudou
                try
                {
                    bool config = (info.Flags & MediaCodecBufferFlags.CodecConfig) != 0;
                    if (info.Size <= 0) continue;
                    var buf = v.Codec.GetOutputBuffer(i)!;
                    buf.Position(info.Offset);
                    buf.Limit(info.Offset + info.Size);
                    var dados = new byte[info.Size];
                    buf.Get(dados);
                    if (config) { v.Config = dados; continue; }
                    bool chave = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                    if (chave && v.Config != null && PrimeiroNal(dados) != 7) dados = [.. v.Config, .. dados];
                    long espera = AgoraUs() - info.PresentationTimeUs;
                    if (espera > Interlocked.Read(ref _maxEsperaUs)) Interlocked.Exchange(ref _maxEsperaUs, espera);
                    Interlocked.Increment(ref _nQuadros);
                    Interlocked.Add(ref _nBytes, dados.Length);
                    if (chave) Interlocked.Increment(ref _nIdr);
                    try { Video?.Invoke(dados, info.PresentationTimeUs, chave); }
                    catch (Exception e) { Log.Info($"{Tag}: quem recebe o vídeo falhou: {e.Message}"); }
                }
                finally { try { v.Codec.ReleaseOutputBuffer(i, false); } catch { } }
            }
        }
        catch (Exception e)
        {
            if (!v.Parar && _descartado == 0) Falha($"O encoder de vídeo parou: {e.Message}");
        }
    }

    /// <summary>Tipo do primeiro NAL de um Annex-B (7 = SPS), ou -1.</summary>
    static int PrimeiroNal(byte[] d)
    {
        for (int i = 0; i + 3 < d.Length && i < 8; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) return d[i + 3] & 0x1F;
        return -1;
    }

    public void PedirChave()
    {
        var v = _vida;
        if (v == null || _descartado != 0) return;
        try
        {
            var b = new Bundle();
            b.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
            v.Codec.SetParameters(b);
        }
        catch (Exception e) { Log.Info($"{Tag}: não consegui pedir quadro-chave: {e.Message}"); }
    }

    // ------------------------------------------------------------------ rotação

    void TelaMudou()
    {
        if (_descartado != 0 || !_iniciado) return;
        var (telaW, telaH, dpi) = TamanhoDaTela();
        if ((telaH > telaW) == (Altura > Largura)) return;
        // fora da thread de avisos: criar o encoder demora
        new Thread(() => Reconfigurar(telaW, telaH, dpi)) { IsBackground = true, Name = "captura-gira" }.Start();
    }

    void Reconfigurar(int telaW, int telaH, int dpi)
    {
        lock (_gate)
        {
            if (_descartado != 0 || _vd == null || _info == null) return;
            if ((telaH > telaW) == (Altura > Largura)) return;
            try
            {
                var caps = _info.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc)!.VideoCapabilities!;
                if (!TamanhoPara(telaW, telaH, caps, out int w, out int h)) { Falha("A tela girou e o encoder não aceita o tamanho novo."); return; }
                var velha = _vida!;
                var nova = AbrirEncoder(w, h);
                _gl!.Trocar(nova.Entrada, w, h);   // o GL passa a desenhar no encoder novo (e a SurfaceTexture no tamanho novo)
                _vd.Resize(w, h, dpi);
                _vida = nova;
                PararVida(velha);
                Largura = w; Altura = h; _dpi = dpi;
                IniciarSaida(nova);
                Log.Info($"CAPTURA girou: {w}x{h}");
                Tamanho?.Invoke(w, h);
            }
            catch (Exception e) { Falha($"Não consegui refazer a captura depois de girar a tela: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ áudio

    void IniciarAudio()
    {
        if (CanaisAudio == 0 || !OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        MediaCodec? opus = null;
        AudioRecord? gravador = null;
        try
        {
            var cfg = new AudioPlaybackCaptureConfiguration.Builder(_projecao)
                .AddMatchingUsage(AudioUsageKind.Media)
                .AddMatchingUsage(AudioUsageKind.Game)
                .AddMatchingUsage(AudioUsageKind.Unknown)
                .Build();
            var af = new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit).SetSampleRate(AudioHz).SetChannelMask((ChannelOut)(int)ChannelIn.Stereo).Build();
            int min = AudioRecord.GetMinBufferSize(AudioHz, ChannelIn.Stereo, Encoding.Pcm16bit);
            gravador = new AudioRecord.Builder().SetAudioPlaybackCaptureConfig(cfg).SetAudioFormat(af).SetBufferSizeInBytes(Math.Max(min, AudioHz / 10 * 4)).Build();
            if (gravador.State != State.Initialized) throw new InvalidOperationException("o AudioRecord não inicializou");

            try { opus = MediaCodec.CreateByCodecName("c2.android.opus.encoder"); }
            catch { opus = MediaCodec.CreateEncoderByType("audio/opus"); }
            var fmt = MediaFormat.CreateAudioFormat("audio/opus", AudioHz, 2);
            fmt.SetInteger(MediaFormat.KeyBitRate, AudioKbps * 1000);
            fmt.SetInteger(MediaFormat.KeyMaxInputSize, AudioHz / 100 * 4 * 4);
            opus!.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
            opus.Start();
            gravador.StartRecording();
        }
        catch (Exception e)
        {
            Log.Info($"{Tag}: sem áudio ({e.GetType().Name}: {e.Message}).");
            try { gravador?.Release(); } catch { }
            try { opus?.Release(); } catch { }
            return;
        }
        _gravador = gravador; _opus = opus;
        _threadAudioEntrada = new Thread(LacoAudioEntrada) { IsBackground = true, Name = "captura-audio-in" };
        _threadAudioSaida = new Thread(LacoAudioSaida) { IsBackground = true, Name = "captura-audio-out" };
        _threadAudioEntrada.Start();
        _threadAudioSaida.Start();
    }

    /// <summary>Lê 10 ms de PCM por vez e entrega ao Opus, com o PTS do primeiro byte no relógio comum.</summary>
    void LacoAudioEntrada()
    {
        Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var gravador = _gravador!; var opus = _opus!;
        const int quadros = AudioHz / 100;   // 10 ms
        const long bloqueUs = quadros * 1_000_000L / AudioHz;
        var pcm = new byte[quadros * 4];
        long lidos = 0;   // quadros PCM lidos até aqui
        var ts = new AudioTimestamp();
        long baseUs = long.MinValue;   // PTS do quadro 0: o menor valor visto (o relógio do áudio anda liso, o atraso da leitura só empurra para cima)
        try
        {
            while (!_pararAudio && _descartado == 0)
            {
                int n = 0;
                while (n < pcm.Length && !_pararAudio)
                {
                    int r = gravador.Read(pcm, n, pcm.Length - n);
                    if (r < 0) throw new InvalidOperationException($"AudioRecord.Read devolveu {r}");
                    n += r;
                }
                if (_pararAudio) break;
                long agora = AgoraUs();
                // PTS do primeiro byte deste bloco: pelo timestamp do AudioRecord quando houver, senão a hora da leitura menos a duração lida
                long inicioBloco = agora - bloqueUs;
                if (gravador.GetTimestamp(ts, 0 /* TIMEBASE_MONOTONIC */) == 0 && ts.FramePosition > 0)
                    inicioBloco = Math.Min(inicioBloco, ts.NanoTime / 1000 + (lidos - ts.FramePosition) * 1_000_000L / AudioHz);
                long pts0 = inicioBloco - lidos * 1_000_000L / AudioHz;
                if (baseUs == long.MinValue || pts0 < baseUs) baseUs = pts0;
                long pts = baseUs + lidos * 1_000_000L / AudioHz;
                lidos += quadros;
                int idx = opus.DequeueInputBuffer(50_000);
                if (idx < 0) continue;   // encoder atrasado: pula este bloco
                var buf = opus.GetInputBuffer(idx)!;
                buf.Clear();
                buf.Put(pcm, 0, pcm.Length);
                opus.QueueInputBuffer(idx, 0, pcm.Length, pts, MediaCodecBufferFlags.None);
            }
        }
        catch (Exception e)
        {
            if (!_pararAudio && _descartado == 0) Log.Info($"{Tag}: o áudio parou ({e.Message}); o vídeo continua.");
        }
    }

    void LacoAudioSaida()
    {
        Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        var opus = _opus!;
        var info = new MediaCodec.BufferInfo();
        bool primeiro = true;
        try
        {
            while (!_pararAudio && _descartado == 0)
            {
                int i = opus.DequeueOutputBuffer(info, 100_000);
                if (i < 0) continue;
                try
                {
                    if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0 || info.Size <= 0) continue;   // OpusHead não vai
                    var buf = opus.GetOutputBuffer(i)!;
                    buf.Position(info.Offset);
                    buf.Limit(info.Offset + info.Size);
                    var dados = new byte[info.Size];
                    buf.Get(dados);
                    if (primeiro) { primeiro = false; Log.Info($"{Tag}: primeiro pacote Opus com {dados.Length} bytes, quadro de {DuracaoOpusMs(dados[0])} ms (TOC {dados[0]:x2})"); }
                    Interlocked.Increment(ref _nAudio);
                    try { Audio?.Invoke(dados, info.PresentationTimeUs); }
                    catch (Exception e) { Log.Info($"{Tag}: quem recebe o áudio falhou: {e.Message}"); }
                }
                finally { try { opus.ReleaseOutputBuffer(i, false); } catch { } }
            }
        }
        catch (Exception e)
        {
            if (!_pararAudio && _descartado == 0) Log.Info($"{Tag}: o encoder de áudio parou ({e.Message}); o vídeo continua.");
        }
    }

    /// <summary>Duração do quadro pela config do TOC do Opus (RFC 6716, 3.1).</summary>
    static string DuracaoOpusMs(byte toc)
    {
        int cfg = toc >> 3;
        if (cfg < 12) return new[] { "10", "20", "40", "60" }[cfg & 3];   // SILK
        if (cfg < 16) return new[] { "10", "20" }[cfg & 1];               // híbrido
        return new[] { "2,5", "5", "10", "20" }[cfg & 3];                 // CELT
    }

    // ------------------------------------------------------------------ números e falha

    void LacoNumeros()
    {
        while (!_fim.Wait(2000))
        {
            long agora = AgoraUs();
            double s = Math.Max(0.001, (agora - _tUltimo) / 1e6);
            _tUltimo = agora;
            long q = Interlocked.Exchange(ref _nQuadros, 0), b = Interlocked.Exchange(ref _nBytes, 0), idr = Interlocked.Exchange(ref _nIdr, 0);
            long a = Interlocked.Exchange(ref _nAudio, 0), esp = Interlocked.Exchange(ref _maxEsperaUs, 0);
            Log.Info($"CAPTURA-NUMEROS {q / s:F1} quadros/s, {b * 8 / s / 1e6:F2} Mbit/s, {idr} IDR, {a / s:F0} pacotes de áudio/s, maior espera no encoder {esp / 1000.0:F1} ms");
        }
    }

    void Falha(string texto)
    {
        if (_descartado != 0) return;
        Log.Info($"CAPTURA-FALHA {texto}");
        try { Falhou?.Invoke(texto); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _descartado, 1) != 0) return;
        _fim.Set();
        _pararAudio = true;
        // sem lock: Iniciar pode estar segurando e chamar Dispose de dentro; Reconfigurar vê _descartado e sai
        var vida = _vida; _vida = null;
        try { if (_listenerTela != null) _dm?.UnregisterDisplayListener(_listenerTela); } catch { }
        try { _vd?.Release(); } catch { }
        _vd = null;
        try { _gl?.Parar(); } catch { }
        if (vida != null) PararVida(vida);
        try { if (_callbackProjecao != null) _projecao.UnregisterCallback(_callbackProjecao); } catch { }
        var gravador = _gravador; _gravador = null;
        try { gravador?.Stop(); } catch { }
        foreach (var t in new[] { _threadAudioEntrada, _threadAudioSaida })
            try { if (t != null && t != Thread.CurrentThread) t.Join(1500); } catch { }
        try { gravador?.Release(); } catch { }
        var opus = _opus; _opus = null;
        try { opus?.Stop(); } catch { }
        try { opus?.Release(); } catch { }
        opus?.Dispose();
        try { _threadAvisos?.QuitSafely(); } catch { }
        Log.Info("CAPTURA parada.");
    }

    // ------------------------------------------------------------------ ouvintes do Android

    sealed class Callback(Captura c) : MediaProjection.Callback
    {
        public override void OnStop() => c.Falha("A captura de tela foi encerrada pelo Android.");
    }

    sealed class ListenerTela(Captura c) : Java.Lang.Object, Android.Hardware.Display.DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) { }
        public void OnDisplayRemoved(int displayId) { }
        public void OnDisplayChanged(int displayId) { if (displayId == 0) c.TelaMudou(); }
    }
}
