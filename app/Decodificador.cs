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
    volatile Surface _surface;
    /// <summary>
    /// Um MediaCodec com a fila e as threads dele. Cada troca de SPS cria uma vida nova: as threads da anterior só enxergam a fila dela, então
    /// não roubam o primeiro quadro-chave do decodificador novo nem continuam rodando depois de o codec ser liberado.
    /// </summary>
    sealed class Vida(MediaCodec codec)
    {
        public readonly MediaCodec Codec = codec;
        public readonly BlockingCollection<(byte[] Dados, long Pts)> Fila = new(new ConcurrentQueue<(byte[], long)>(), 60);
        public volatile bool Parar;
        public Thread? Entrada, Saida;
    }
    Vida? _vida;
    readonly object _gate = new();
    volatile bool _descartado;
    readonly ConcurrentDictionary<long, long> _chegada = new();   // PTS em µs -> hora da chegada, para medir o tempo dentro do decodificador
    volatile bool _esperandoChave = true;   // escrito por Entregar (SRT) e pela thread de entrada: sem volatile uma thread podia não ver a outra
    byte[]? _sps;
    int _largura, _altura;   // o tamanho do SPS da vida atual, para a telemetria de queda
    string _baixaLatencia = "";   // como a chave de baixa latência do Android foi tratada na última tentativa de abrir (para o log)
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

    /// <summary>
    /// A peça passou de principal para miniatura (ou o contrário) e a tela dela é outra (spec 003): troca a saída sem reabrir o decodificador.
    /// Se o fabricante recusar o SetOutputSurface, reabre no próximo quadro-chave (a imagem daquela peça para por até 2 s).
    /// </summary>
    public void TrocarSurface(Surface nova)
    {
        _saindo = false;   // tela nova (ou a mesma de volta): o que lançar daqui em diante volta a ser falha de verdade
        lock (_gate)
        {
            if (_surface == nova) return;
            _surface = nova;
            if (_descartado || _vida is not { } vida) return;
            try { vida.Codec.SetOutputSurface(nova); }
            catch (Exception e)
            {
                Streemu.Util.Log.Info("Decodificador: o SetOutputSurface falhou, reabrindo no próximo quadro-chave: " + e.Message);
                _reabrir = true;
                _esperandoChave = true;
            }
        }
    }

    /// <summary>O que se sabe da falha de abertura: onde parou, qual decodificador, e o que o Android respondeu.</summary>
    public sealed record FalhaAbertura(string Etapa, string Codec, int Largura, int Altura, int Nivel, string Modo, string Tipo, string Mensagem,
                                       string? CodigoHex, string Diagnostico, bool Transitorio, bool Recuperavel, int Rodadas, string Tentativas);

    /// <summary>Nenhum nível de recuo abriu (nem na segunda rodada, se a falha parecia transitória). Depois disto o decodificador não aceita mais quadros.</summary>
    public event Action<FalhaAbertura>? NaoAbriu;
    public bool Desistiu => _desistiu;
    volatile bool _desistiu;

    /// <summary>
    /// O decodificador abriu mas uma das threads dele (entrada ou saída) caiu por exceção com o codec ainda em uso (Samsung A11, 2026-11-27: duas vezes
    /// IllegalStateException e o vídeo parou sem nenhum aviso). Sai uma vez por decodificador, e nunca quando já houve <see cref="NaoAbriu"/>.
    /// </summary>
    public sealed record QuedaDoDecodificador(string Etapa, string Codec, int Largura, int Altura, int Nivel, string Modo, string Tipo, string Mensagem,
                                              string? CodigoHex, string Diagnostico, bool Transitorio);
    public event Action<QuedaDoDecodificador>? Caiu;
    int _caiuAvisado;

    /// <summary>
    /// A tela do player está sendo destruída (surfaceDestroyed da SurfaceView atual, que vem antes do OnDestroy e do Dispose). Medido em emulador
    /// (API 29 e 34, 2026-11-27): a Surface some e os dois laços lançam IllegalStateException ~25 ms antes do Dispose, a cada saída do player; isso
    /// não é queda do decodificador e não pode virar midia_falha. Os laços tratam a exceção como fim normal enquanto isto estiver ligado.
    /// </summary>
    public void Saindo() => _saindo = true;
    volatile bool _saindo;
    int _logSaida;

    /// <summary>A exceção veio da saída da tela (marcada por <see cref="Saindo"/>, ou a Surface já inválida)? Então manda os laços pararem e loga uma vez, em info.</summary>
    bool InterrompidoPelaTela(Vida vida)
    {
        if (!_saindo && _surface.IsValid) return false;
        vida.Parar = true;
        if (Interlocked.Exchange(ref _logSaida, 1) == 0) Streemu.Util.Log.Info("Decodificador interrompido pela saída da tela.");
        return true;
    }

    /// <summary>Como o decodificador abriu: "hardware", "hardware_sem_tempo_real" ou "software" (vazio antes de abrir).</summary>
    public string Modo { get; private set; } = "";
    /// <summary>Nível de recuo que funcionou por último (0 = pedido completo). A próxima troca de SPS começa dele, em vez de falhar de novo nos níveis de cima.</summary>
    public int Nivel => _nivel;
    int _nivel;
    int _rodadasFalhas;   // escadas completas que falharam desde a última abertura que deu certo
    const int MaxRodadas = 2;

    static readonly string[] NomesSoftware = ["c2.android.avc.decoder", "OMX.google.h264.decoder"];
    const long MaxPixelsSoftware = 1920L * 1088;   // 1080p (o SPS de 1080p costuma vir com 1088 linhas antes do recorte)
    volatile bool _reabrir;

    /// <summary>
    /// Live nova: se o decodificador está num nível de recuo, reabre no pedido completo no próximo quadro-chave. Sem isto, quem só abriu sem tempo real
    /// enquanto transmitia continuava assim (com mais atraso) nas lives seguintes, mesmo depois de parar a transmissão. Chamar da thread que chama Entregar.
    /// </summary>
    public void RecomecarCompleto()
    {
        if (_nivel == 0 || _desistiu) return;
        Streemu.Util.Log.Info($"Decodificador: live nova, voltando do modo {Modo} para o pedido completo.");
        _nivel = 0; _rodadasFalhas = 0;
        _reabrir = true;
        _esperandoChave = true;
    }
    static string ModoDoNivel(int nivel) => nivel switch { 0 => "hardware", 1 => "hardware_sem_tempo_real", _ => "software" };

    public void Entregar(byte[] au, long pts90k)
    {
        if (_descartado) return;
        Interlocked.Increment(ref Entregues);
        if (_desistiu) return;
        Chegou?.Invoke(pts90k, Streemu.Net.Tunnel.NowUs);
        if (_esperandoChave)
        {
            var (sps, pps, idr) = Nals(au);
            if (!idr || sps == null || pps == null) { Interlocked.Increment(ref DescartadosAteChave); return; }
            if (_vida == null || _reabrir || (_sps != null && !sps.AsSpan().SequenceEqual(_sps)))
            {
                FalhaAbertura? desistiu = null;
                lock (_gate)
                {
                    if (_descartado) return;
                    // a tela do player já foi liberada (o player está fechando ou foi para o fundo): não é falha do decodificador, só não há onde mostrar
                    if (!_surface.IsValid) { Interlocked.Increment(ref DescartadosAteChave); return; }
                    bool abriu;
                    FalhaAbertura? falha;
                    try { abriu = Configurar(sps, pps, out falha); }
                    catch (Exception e)
                    {
                        // erro nosso no meio da escada (em 0.1.9 a formatação do código de erro lançava e a escada nunca passava do nível 0):
                        // conta como falha definitiva, para não tentar de novo a cada quadro-chave sem nunca avisar
                        Streemu.Util.Log.Info("ERRO: " + "abrir o decodificador: " + e);
                        abriu = false;
                        falha = new FalhaAbertura("inesperado", Nome, 0, 0, _nivel, ModoDoNivel(_nivel), e.GetType().Name, e.Message ?? "", null, "", false, false, ++_rodadasFalhas, "");
                    }
                    if (!abriu && !_surface.IsValid) { Interlocked.Increment(ref DescartadosAteChave); return; }   // a tela sumiu durante a tentativa
                    if (!abriu)
                    {
                        // falha transitória ganha mais uma escada no próximo quadro-chave; as outras (e a segunda rodada) encerram
                        if (falha!.Transitorio && _rodadasFalhas < MaxRodadas) { Interlocked.Increment(ref DescartadosAteChave); return; }
                        _desistiu = true;
                        desistiu = falha;
                    }
                }
                // fora do _gate: quem assina pode mexer na tela e na ligação
                if (desistiu != null)
                {
                    try { NaoAbriu?.Invoke(desistiu); } catch (Exception e) { Streemu.Util.Log.Info("ERRO: " + "aviso de decodificador que não abriu: " + e.Message); }
                    return;
                }
            }
            _esperandoChave = false;
        }
        if (_vida is not { } vida) return;
        if (!vida.Fila.TryAdd((au, pts90k)))
        {
            // decodificador atrasado: jogar fora até o próximo quadro-chave (um P sem o anterior só gera sujeira na tela)
            Interlocked.Increment(ref DescartadosFilaCheia);
            while (vida.Fila.TryTake(out _)) { }
            _esperandoChave = true;
        }
    }

    /// <summary>
    /// Sob _gate: troca o decodificador, descendo a escada de recuo a partir do último nível que funcionou. Nunca lança: devolve false com a última falha.
    /// Nível 0: o pedido completo (baixa latência, prioridade 0, chaves do fabricante). Nível 1: o mesmo decodificador de hardware sem o que pede "tempo
    /// real" (no Moto G42 com a própria transmissão ativa o driver Qualcomm recusa uma segunda sessão assim, ERROR_UNSUPPORTED). Nível 2: por software.
    /// </summary>
    bool Configurar(byte[] sps, byte[] pps, out FalhaAbertura? falha)
    {
        Parar();
        _sps = null;
        _reabrir = false;
        var (w, h) = TamanhoDoSps(sps);
        var tentativas = new List<string>();
        falha = null;
        for (int nivel = _nivel; nivel <= 2; nivel++)
        {
            // por software, acima de 1080p o celular abre mas não acompanha (vídeo quase parado): melhor a mensagem de que não dá conta
            if (nivel == 2 && (long)w * h > MaxPixelsSoftware) { tentativas.Add($"2:pulado ({w}x{h} acima de 1080p)"); continue; }
            foreach (var nome in nivel == 2 ? NomesSoftware : new string?[] { null })
            {
                var codec = TentarAbrir(nivel, nome, sps, pps, w, h, out falha, out string chaves);
                tentativas.Add($"{nivel}:{(falha == null ? "ok" : falha.Etapa + " " + (falha.CodigoHex ?? falha.Tipo))}");
                if (codec == null) continue;
                _sps = sps; _nivel = nivel; _rodadasFalhas = 0;
                (_largura, _altura) = (w, h);
                Modo = ModoDoNivel(nivel);
                ChavesDoFabricante = chaves;
                var vida = new Vida(codec);
                Streemu.Util.Log.Info($"Decodificador {Nome}: {w}x{h}, modo {Modo} (nível {nivel}), baixa latência do Android {(nivel > 0 ? "não pedida" : _baixaLatencia)}, " +
                                      $"do fabricante: {(chaves.Length > 0 ? chaves : "nenhuma")}.");
                Tamanho?.Invoke(w, h);
                vida.Entrada = new Thread(() => LacoEntrada(vida)) { IsBackground = true, Name = "dec-entrada" };
                vida.Saida = new Thread(() => LacoSaida(vida)) { IsBackground = true, Name = "dec-saida" };
                _vida = vida;
                vida.Entrada.Start();
                vida.Saida.Start();
                falha = null;
                return true;
            }
        }
        _rodadasFalhas++;
        // já estava em software e o SPS novo passou de 1080p: nenhuma tentativa rodou
        falha ??= new FalhaAbertura("pulado", "", w, h, 2, ModoDoNivel(2), "AcimaDoLimiteDoSoftware", $"{w}x{h} acima de 1080p por software", null, "", false, false, 0, "");
        falha = falha with { Rodadas = _rodadasFalhas, Tentativas = string.Join("; ", tentativas) };
        Streemu.Util.Log.Info($"ERRO: nenhum nível abriu o decodificador ({falha.Tentativas}); rodada {_rodadasFalhas} de {MaxRodadas}{(falha.Transitorio ? ", falha transitória" : "")}.");
        return false;
    }

    /// <summary>Uma tentativa (um nível, um decodificador). Em qualquer falha libera o codec, registra a etapa e devolve null.</summary>
    MediaCodec? TentarAbrir(int nivel, string? nomeFixo, byte[] sps, byte[] pps, int w, int h, out FalhaAbertura? falha, out string chaves)
    {
        string etapa = "create", nomeCodec = nomeFixo ?? "";
        MediaCodec? codec = null;
        chaves = "";
        falha = null;
        _baixaLatencia = "";
        try
        {
            var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, w, h);
            fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps));
            fmt.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps));
            if (nivel == 0) fmt.SetInteger(MediaFormat.KeyPriority, 0);   // tempo real
            fmt.SetInteger(MediaFormat.KeyMaxInputSize, 4 << 20);   // quadro-chave de 1440p/4K com bitrate alto passa do padrão de alguns fabricantes
            codec = nomeFixo != null ? MediaCodec.CreateByCodecName(nomeFixo) : MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc);
            nomeCodec = codec.Name;
            Nome = nomeCodec;
            if (nivel == 0)
            {
                // KeyLowLatency só vale para decodificador que declara FEATURE_LowLatency: no Samsung A11 (SDM450, Android 12,
                // OMX.qcom.video.decoder.avc, 2026-11-27) o configure recusava a chave com CodecException 0x80001001 em toda conexão
                // e só o nível 1 abria. O resto do pedido do nível 0 (prioridade 0, chaves da Qualcomm) continua, e a escada cai para o 1 se também falhar.
                if (!OperatingSystem.IsAndroidVersionAtLeast(30)) _baixaLatencia = "indisponível (antes do Android 11)";
                else if (!SuportaBaixaLatencia(codec))
                {
                    _baixaLatencia = "não suportada pelo decodificador";
                    Streemu.Util.Log.Info($"Decodificador {nomeCodec}: baixa latência do Android {_baixaLatencia}; a chave não é pedida.");
                }
                else { fmt.SetInteger(MediaFormat.KeyLowLatency, 1); _baixaLatencia = "pedida"; }
                chaves = ChavesDeBaixaLatencia(fmt, nomeCodec);
            }
            // o tamanho real (o do SPS, com recorte), e não o 16:9 que a qualidade da sala sugere: só informa, quem decide é o configure
            Streemu.Util.Log.Info($"Decodificador {nomeCodec}: {Aparelho.DescreverTamanho(codec.CodecInfo, w, h)}.");
            etapa = "configure";
            codec.Configure(fmt, _surface, null, MediaCodecConfigFlags.None);
            // aviso de "quadro foi para a tela" numa thread própria (precisa de Looper)
            if (_threadAvisos == null) { _threadAvisos = new Android.OS.HandlerThread("dec-avisos"); _threadAvisos.Start(); }
            codec.SetOnFrameRenderedListener(new AvisoDeTela(this), new Android.OS.Handler(_threadAvisos.Looper!));
            etapa = "start";
            codec.Start();
            return codec;
        }
        catch (Exception e)
        {
            // SPS absurdo ou decodificador recusou: sem isto, cada nova tentativa vazava um MediaCodec
            if (codec != null) { try { codec.Release(); } catch { } codec.Dispose(); }
            string? codigo = null; string diag = "", tipo = e.GetType().Name;
            bool transitorio = false, recuperavel = false;
            if (e is MediaCodec.CodecException ce)
            {
                codigo = $"0x{(int)ce.ErrorCode:x8}";   // ErrorCode é enum: "x8" direto nele lança FormatException
                diag = ce.DiagnosticInfo ?? ""; transitorio = ce.IsTransient; recuperavel = ce.IsRecoverable;
                Streemu.Util.Log.Info($"ERRO: decodificador {nomeCodec} {w}x{h} nível {nivel}, etapa {etapa}: CodecException ErrorCode {codigo}, DiagnosticInfo \"{diag}\", IsTransient {transitorio}, IsRecoverable {recuperavel}.");
            }
            else
            {
                // o Android costuma devolver o código só no texto ("... 0xfffffc0e"): guarda para a telemetria
                var m = System.Text.RegularExpressions.Regex.Match(e.Message ?? "", "0x[0-9a-fA-F]{8}");
                if (m.Success) codigo = m.Value.ToLowerInvariant();
                Streemu.Util.Log.Info($"ERRO: decodificador {nomeCodec} {w}x{h} nível {nivel}, etapa {etapa}: {tipo}: {e.Message}");
            }
            falha = new FalhaAbertura(etapa, nomeCodec, w, h, nivel, ModoDoNivel(nivel), tipo, e.Message ?? "", codigo, diag, transitorio, recuperavel, 0, "");
            return null;
        }
    }

    /// <summary>O decodificador declara FEATURE_LowLatency (Android 11+)? Se a pergunta falhar, vale o comportamento de antes: pede a chave.</summary>
    const string FeatureLowLatency = "low-latency";   // CodecCapabilities.FEATURE_LowLatency (API 30); o binding do .NET não traz a constante
    static bool SuportaBaixaLatencia(MediaCodec codec)
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return false;
            var cap = codec.CodecInfo?.GetCapabilitiesForType(MediaFormat.MimetypeVideoAvc);
            return cap == null || cap.IsFeatureSupported(FeatureLowLatency);
        }
        catch (Exception e)
        {
            Streemu.Util.Log.Info("Decodificador: não deu para perguntar se há baixa latência (" + e.Message + "); peço a chave como antes.");
            return true;
        }
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

    void LacoEntrada(Vida vida)
    {
        var codec = vida.Codec;
        try
        {
            while (!vida.Parar)
            {
                if (!vida.Fila.TryTake(out var q, 100)) continue;
                int i;
                while ((i = codec.DequeueInputBuffer(10_000)) < 0) if (vida.Parar) return;
                var buf = codec.GetInputBuffer(i)!;
                buf.Clear();
                if (q.Dados.Length > buf.Capacity())
                {
                    // quadro maior que o buffer do decodificador: o Put lançaria e esta thread morreria (vídeo parado de vez).
                    // Devolve o buffer vazio e espera o próximo quadro-chave.
                    codec.QueueInputBuffer(i, 0, 0, 0, MediaCodecBufferFlags.None);
                    _esperandoChave = true;
                    Streemu.Util.Log.Info($"Quadro de {q.Dados.Length} bytes maior que o buffer do decodificador ({buf.Capacity()}); esperando o próximo quadro-chave.");
                    continue;
                }
                buf.Put(q.Dados);
                long us = q.Pts >= 0 ? q.Pts * 100 / 9 : 0;
                if (Interlocked.Increment(ref _logEntrada) <= 3) Android.Util.Log.Info("Streemu", $"Entrada crua: pts90k {q.Pts}, enviado {us} µs");
                _chegada[us] = Stopwatch.GetTimestamp();
                codec.QueueInputBuffer(i, 0, q.Dados.Length, us, MediaCodecBufferFlags.None);
            }
        }
        catch (Exception e) when (vida.Parar) { _ = e; }
        catch (Exception e) when (InterrompidoPelaTela(vida)) { _ = e; }
        catch (Exception e) { Streemu.Util.Log.Info("ERRO: " + "entrada do decodificador: " + e); Cair(vida, "entrada", e); }
    }

    void LacoSaida(Vida vida)
    {
        var codec = vida.Codec;
        var info = new MediaCodec.BufferInfo();
        try
        {
            while (!vida.Parar)
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
        catch (Exception e) when (vida.Parar) { _ = e; }
        catch (Exception e) when (InterrompidoPelaTela(vida)) { _ = e; }
        catch (Exception e) { Streemu.Util.Log.Info("ERRO: " + "saída do decodificador: " + e); Cair(vida, "saida", e); }
    }

    /// <summary>
    /// Uma thread do decodificador lançou com a vida ainda em uso. Manda a outra thread parar (senão ela tropeça no mesmo codec e loga de novo) e avisa
    /// uma vez, só se a vida ainda é a atual e o decodificador não tinha desistido (aí o <see cref="NaoAbriu"/> já foi para a telemetria).
    /// </summary>
    void Cair(Vida vida, string etapa, Exception e)
    {
        vida.Parar = true;
        if (_descartado || _desistiu || _vida != vida || Interlocked.Exchange(ref _caiuAvisado, 1) != 0) return;
        string? codigo = null; string diag = ""; bool transitorio = false;
        if (e is MediaCodec.CodecException ce)
        {
            codigo = $"0x{(int)ce.ErrorCode:x8}";
            diag = ce.DiagnosticInfo ?? ""; transitorio = ce.IsTransient;
        }
        try { Caiu?.Invoke(new QuedaDoDecodificador(etapa, Nome, _largura, _altura, _nivel, Modo, e.GetType().Name, e.Message ?? "", codigo, diag, transitorio)); }
        catch (Exception ex) { Streemu.Util.Log.Info("ERRO: " + "aviso de queda do decodificador: " + ex.Message); }
    }

    const int EsperaThreadMs = 2000, EsperaExtraMs = 5000;

    /// <summary>
    /// Sob _gate: para as threads da vida atual (cada uma acorda em até ~100 ms) e só então para e libera o codec: Stop/Release com uma thread ainda
    /// dentro de DequeueInputBuffer/QueueInputBuffer era o que lançava IllegalStateException nas threads (Samsung A11, 2026-11-27). Se uma thread não sai
    /// em 2 s (driver preso), não trava quem chamou: o Stop/Release fica para uma tarefa de fundo, que espera mais um pouco e libera de qualquer jeito
    /// (sem isto o codec ficaria preso e a próxima abertura falharia). Os laços ignoram o que lançar depois de Parar.
    /// </summary>
    void Parar()
    {
        var v = _vida; _vida = null;
        if (v == null) return;
        long t0 = Stopwatch.GetTimestamp();
        v.Parar = true;
        var vivas = new List<Thread>();
        foreach (var t in new[] { v.Entrada, v.Saida })
        {
            if (t == null) continue;
            int restante = EsperaThreadMs - (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!t.Join(Math.Max(restante, 0))) vivas.Add(t);
        }
        if (vivas.Count == 0)
        {
            LiberarCodec(v);
            Streemu.Util.Log.Info($"Decodificador parado em {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms.");
            return;
        }
        foreach (var t in vivas) Streemu.Util.Log.Info($"ERRO: thread {t.Name} não saiu em {EsperaThreadMs}ms; o codec será liberado em segundo plano.");
        Streemu.Util.Log.Info($"Decodificador: Parar saiu após {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms, com {vivas.Count} thread(s) viva(s).");
        _ = Task.Run(() =>
        {
            foreach (var t in vivas)
                if (!t.Join(EsperaExtraMs)) Streemu.Util.Log.Info($"ERRO: thread {t.Name} ainda viva após {EsperaThreadMs + EsperaExtraMs}ms; libero o codec mesmo assim.");
            LiberarCodec(v);
            Streemu.Util.Log.Info($"Decodificador: codec liberado em segundo plano após {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms.");
        });
    }

    static void LiberarCodec(Vida v)
    {
        try { v.Codec.Stop(); } catch (Exception e) { Streemu.Util.Log.Info("Decodificador: Stop: " + e.Message); }
        try { v.Codec.Release(); } catch (Exception e) { Streemu.Util.Log.Info("Decodificador: Release: " + e.Message); }
    }

    /// <summary>Pode vir de qualquer thread, inclusive enquanto o SRT entrega um quadro: depois disto, Entregar não faz mais nada.</summary>
    public void Dispose()
    {
        _descartado = true;
        lock (_gate) Parar();
        _threadAvisos?.QuitSafely();
    }

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
