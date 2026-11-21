using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Media;
using Android.Views;
using Java.Nio;

namespace Streemu.Proto;

/// <summary>
/// H.264 (Annex-B, um quadro por chamada) -> MediaCodec -> Surface, com o menor atraso possível: cada quadro é mostrado assim que sai do decodificador,
/// sem esperar o PTS (quem segura o ritmo é o SRT, com a latência combinada). Começa no primeiro quadro-chave com SPS e PPS.
/// </summary>
sealed class Decodificador : IDisposable
{
    readonly Surface _surface;
    MediaCodec? _codec;
    readonly BlockingCollection<(byte[] Dados, long Pts)> _fila = new(new ConcurrentQueue<(byte[], long)>(), 60);
    readonly ConcurrentDictionary<long, long> _chegada = new();   // PTS em µs -> hora da chegada, para medir o tempo dentro do decodificador
    volatile bool _parar;
    bool _esperandoChave = true;
    byte[]? _sps;
    int _logEntrada;

    public string Nome { get; private set; } = "";
    /// <summary>As chaves de baixa latência do fabricante que foram ligadas (vazio se nenhuma é conhecida para este decodificador).</summary>
    public string ChavesDoFabricante { get; private set; } = "";
    public long Entregues, Mostrados, DescartadosAteChave, DescartadosFilaCheia;
    public double MsNoDecodificador { get; private set; }   // média móvel: entrada -> saída do MediaCodec
    public event Action<int, int>? Tamanho;
    /// <summary>Um quadro completo chegou do demux: PTS (90 kHz) e a hora no relógio do túnel (Tunnel.NowUs), em µs.</summary>
    public event Action<long, long>? Chegou;
    /// <summary>O quadro foi para a tela (OnFrameRendered): PTS (90 kHz) e a hora no relógio do túnel, em µs.</summary>
    public event Action<long, long>? NaTela;

    // OnFrameRendered dá a hora em System.nanoTime; o Tunnel.NowUs vem do Stopwatch do .NET. Os dois deveriam ser CLOCK_MONOTONIC, mas a diferença é
    // medida uma vez em vez de suposta.
    readonly long _nanoParaTunelUs = Streemu.Net.Tunnel.NowUs - Java.Lang.JavaSystem.NanoTime() / 1000;
    Android.OS.HandlerThread? _threadAvisos;

    public Decodificador(Surface surface)
    {
        _surface = surface;
        Streemu.Util.Log.Info($"Relógio: Tunnel.NowUs - nanoTime/1000 = {_nanoParaTunelUs} µs.");
    }

    public void Entregar(byte[] au, long pts90k)
    {
        Interlocked.Increment(ref Entregues);
        Chegou?.Invoke(pts90k, Streemu.Net.Tunnel.NowUs);
        if (_esperandoChave)
        {
            var (sps, pps, idr) = Nals(au);
            if (!idr || sps == null || pps == null) { Interlocked.Increment(ref DescartadosAteChave); return; }
            if (_codec == null || (_sps != null && !sps.AsSpan().SequenceEqual(_sps))) Configurar(sps, pps);
            _esperandoChave = false;
        }
        if (!_fila.TryAdd((au, pts90k)))
        {
            // decodificador atrasado: jogar fora até o próximo quadro-chave (um P sem o anterior só gera sujeira na tela)
            Interlocked.Increment(ref DescartadosFilaCheia);
            while (_fila.TryTake(out _)) { }
            _esperandoChave = true;
        }
    }

    void Configurar(byte[] sps, byte[] pps)
    {
        Parar();
        _sps = sps;
        var (w, h) = TamanhoDoSps(sps);
        var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, w, h);
        fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps));
        fmt.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps));
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) fmt.SetInteger(MediaFormat.KeyLowLatency, 1);
        fmt.SetInteger(MediaFormat.KeyPriority, 0);   // tempo real
        var codec = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc);
        Nome = codec.Name;
        ChavesDoFabricante = ChavesDeBaixaLatencia(fmt, Nome);
        codec.Configure(fmt, _surface, null, MediaCodecConfigFlags.None);
        // aviso de "quadro foi para a tela" numa thread própria (precisa de Looper)
        if (_threadAvisos == null) { _threadAvisos = new Android.OS.HandlerThread("dec-avisos"); _threadAvisos.Start(); }
        codec.SetOnFrameRenderedListener(new AvisoDeTela(this), new Android.OS.Handler(_threadAvisos.Looper!));
        codec.Start();
        _codec = codec;
        _parar = false;
        Streemu.Util.Log.Info($"Decodificador {Nome}: {w}x{h}, baixa latência do Android {(OperatingSystem.IsAndroidVersionAtLeast(30) ? "pedida" : "indisponível (antes do Android 11)")}, " +
                                         $"do fabricante: {(ChavesDoFabricante.Length > 0 ? ChavesDoFabricante : "nenhuma conhecida")}.");
        Tamanho?.Invoke(w, h);
        new Thread(() => LacoEntrada(codec)) { IsBackground = true, Name = "dec-entrada" }.Start();
        new Thread(() => LacoSaida(codec)) { IsBackground = true, Name = "dec-saida" }.Start();
    }

    /// <summary>
    /// A opção de baixa latência do Android 11 (KeyLowLatency) depende do driver de cada fabricante, e vários só obedecem às chaves próprias. Sem elas,
    /// o decodificador da Qualcomm, em especial, segura quadros antes de soltar (dezenas de ms). As chaves e os prefixos são os que o Moonlight usa
    /// (moonlight-android, MediaCodecHelper). O decodificador ignora chave que não conhece; mesmo assim, cada uma vai só para o fabricante dela.
    /// </summary>
    static string ChavesDeBaixaLatencia(MediaFormat fmt, string nome)
    {
        var n = nome.ToLowerInvariant();
        var ligadas = new List<string>();
        void Ligar(string chave, int valor) { fmt.SetInteger(chave, valor); ligadas.Add(chave); }
        if (n.StartsWith("omx.qcom") || n.StartsWith("c2.qti"))
        {
            Ligar("vendor.qti-ext-dec-picture-order.enable", 1);   // solta cada quadro na ordem de decodificação, sem esperar reordenar (o PC não manda quadros B)
            Ligar("vendor.qti-ext-dec-low-latency.enable", 1);
        }
        else if (n.StartsWith("omx.exynos") || n.StartsWith("c2.exynos"))
            Ligar("vendor.rtc-ext-dec-low-latency.enable", 1);
        else if (n.StartsWith("omx.hisi") || n.StartsWith("c2.hisi"))
        {
            Ligar("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1);
            Ligar("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1);
        }
        else if (n.StartsWith("omx.amlogic") || n.StartsWith("c2.amlogic"))
            Ligar("vendor.low-latency.enable", 1);
        // MediaTek e Unisoc: nenhuma chave pública conhecida; fica só a do Android 11
        return string.Join(", ", ligadas);
    }

    void LacoEntrada(MediaCodec codec)
    {
        try
        {
            while (!_parar)
            {
                if (!_fila.TryTake(out var q, 100)) continue;
                int i;
                while ((i = codec.DequeueInputBuffer(10_000)) < 0) if (_parar) return;
                var buf = codec.GetInputBuffer(i)!;
                buf.Clear();
                buf.Put(q.Dados);
                long us = q.Pts >= 0 ? q.Pts * 100 / 9 : 0;
                if (Interlocked.Increment(ref _logEntrada) <= 3) Android.Util.Log.Info("Streemu", $"Entrada crua: pts90k {q.Pts}, enviado {us} µs");
                _chegada[us] = Stopwatch.GetTimestamp();
                codec.QueueInputBuffer(i, 0, q.Dados.Length, us, MediaCodecBufferFlags.None);
            }
        }
        catch (Exception e) when (_parar) { _ = e; }
        catch (Exception e) { Streemu.Util.Log.Info("ERRO: " + "entrada do decodificador: " + e); }
    }

    void LacoSaida(MediaCodec codec)
    {
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (!_parar)
            {
                int i = codec.DequeueOutputBuffer(info, 10_000);
                if (i >= 0)
                {
                    // Com a hora de agora, e não "true": com true o Android usa o PTS como hora desejada de exibição (um PTS "no futuro" seguraria o
                    // quadro) e o OnFrameRendered devolve esse PTS em vez da hora em que o quadro foi para a tela (medido: nanoTime == pts * 1000).
                    codec.ReleaseOutputBuffer(i, Java.Lang.JavaSystem.NanoTime());
                    Interlocked.Increment(ref Mostrados);
                    if (_chegada.TryRemove(info.PresentationTimeUs, out long t0))
                    {
                        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        MsNoDecodificador = MsNoDecodificador <= 0 ? ms : MsNoDecodificador * 0.95 + ms * 0.05;
                    }
                    if (_chegada.Count > 300) _chegada.Clear();
                }
                else if (i == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var f = codec.OutputFormat;
                    int w = f.GetInteger(MediaFormat.KeyWidth), h = f.GetInteger(MediaFormat.KeyHeight);
                    if (f.ContainsKey("crop-right")) { w = f.GetInteger("crop-right") - f.GetInteger("crop-left") + 1; h = f.GetInteger("crop-bottom") - f.GetInteger("crop-top") + 1; }
                    Tamanho?.Invoke(w, h);
                }
            }
        }
        catch (Exception e) when (_parar) { _ = e; }
        catch (Exception e) { Streemu.Util.Log.Info("ERRO: " + "saída do decodificador: " + e); }
    }

    void Parar()
    {
        _parar = true;
        var c = _codec; _codec = null;
        if (c == null) return;
        Thread.Sleep(50);
        try { c.Stop(); } catch { }
        try { c.Release(); } catch { }
    }

    public void Dispose() { Parar(); _threadAvisos?.QuitSafely(); }

    sealed class AvisoDeTela(Decodificador d) : Java.Lang.Object, MediaCodec.IOnFrameRenderedListener
    {
        int _logados;
        public void OnFrameRendered(MediaCodec codec, long presentationTimeUs, long nanoTime)
        {
            if (_logados++ < 3)
                Android.Util.Log.Info("Streemu", $"OnFrameRendered cru: pts {presentationTimeUs} µs, nanoTime {nanoTime}, agora nanoTime {Java.Lang.JavaSystem.NanoTime()}");
            d.NaTela?.Invoke(presentationTimeUs * 9 / 100, nanoTime / 1000 + d._nanoParaTunelUs);
        }
    }

    /// <summary>SPS e PPS (com o código de início) e se o quadro tem fatia IDR.</summary>
    static (byte[]? Sps, byte[]? Pps, bool Idr) Nals(byte[] au)
    {
        byte[]? sps = null, pps = null; bool idr = false;
        var inicios = new List<(int Pos, int Cab)>();
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0)
            {
                if (au[i + 2] == 1) { inicios.Add((i, 3)); i += 2; }
                else if (au[i + 2] == 0 && au[i + 3] == 1) { inicios.Add((i, 4)); i += 3; }
            }
        }
        for (int k = 0; k < inicios.Count; k++)
        {
            var (pos, cab) = inicios[k];
            int fim = k + 1 < inicios.Count ? inicios[k + 1].Pos : au.Length;
            if (pos + cab >= au.Length) continue;
            int tipo = au[pos + cab] & 0x1F;
            if (tipo == 7) sps = au[pos..fim];
            else if (tipo == 8) pps = au[pos..fim];
            else if (tipo == 5) idr = true;
        }
        return (sps, pps, idr);
    }

    /// <summary>Largura e altura lidas do SPS (com o recorte). Se algo não for o esperado, 1920x1080: o decodificador acerta pelo próprio SPS.</summary>
    static (int W, int H) TamanhoDoSps(byte[] sps)
    {
        try
        {
            int ini = sps[2] == 1 ? 3 : 4;
            // tira os bytes de prevenção (00 00 03)
            var rbsp = new List<byte>();
            for (int i = ini + 1; i < sps.Length; i++)
            {
                if (i >= 2 && sps[i] == 3 && sps[i - 1] == 0 && sps[i - 2] == 0) continue;
                rbsp.Add(sps[i]);
            }
            var b = new Bits(rbsp.ToArray());
            int perfil = b.U(8); b.U(16);
            b.Ue();
            int croma = 1;
            if (perfil is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                croma = b.Ue(); if (croma == 3) b.U(1);
                b.Ue(); b.Ue(); b.U(1);
                if (b.U(1) == 1) for (int i = 0; i < (croma != 3 ? 8 : 12); i++) if (b.U(1) == 1) { int ult = 8, prox = 8; for (int j = 0; j < (i < 6 ? 16 : 64); j++) { if (prox != 0) prox = (ult + b.Se() + 256) % 256; ult = prox == 0 ? ult : prox; } }
            }
            b.Ue();
            int poc = b.Ue();
            if (poc == 0) b.Ue();
            else if (poc == 1) { b.U(1); b.Se(); b.Se(); int n = b.Ue(); for (int i = 0; i < n; i++) b.Se(); }
            b.Ue(); b.U(1);
            int wMb = b.Ue() + 1, hMap = b.Ue() + 1;
            int soQuadros = b.U(1);
            if (soQuadros == 0) b.U(1);
            b.U(1);
            int w = wMb * 16, h = (2 - soQuadros) * hMap * 16;
            if (b.U(1) == 1)
            {
                int l = b.Ue(), r = b.Ue(), t = b.Ue(), bo = b.Ue();
                int cx = croma == 0 ? 1 : 2, cy = (croma == 1 ? 2 : 1) * (2 - soQuadros);
                w -= (l + r) * cx; h -= (t + bo) * cy;
            }
            return w > 0 && h > 0 ? (w, h) : (1920, 1080);
        }
        catch { return (1920, 1080); }
    }

    sealed class Bits(byte[] d)
    {
        int _p;
        public int U(int n) { int v = 0; for (int i = 0; i < n; i++, _p++) v = (v << 1) | ((d[_p >> 3] >> (7 - (_p & 7))) & 1); return v; }
        public int Ue() { int z = 0; while (U(1) == 0) z++; return (1 << z) - 1 + U(z); }
        public int Se() { int k = Ue(); return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2); }
    }
}
