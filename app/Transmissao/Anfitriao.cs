using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Proto.Transmissao;

/// <summary>
/// A transmissão do celular, do lado da sala: o espelho do que o app Windows faz (MainForm.Grupo.cs: AcceptViewerAsync, EndLink, UpdateLive;
/// Media/StreamSender.cs: AddOutput/OutputLoop). Liga a <see cref="Captura"/> ao <see cref="TsMux"/> e entrega os blocos TS a quem pediu para assistir.
///
/// O protocolo é o do app Windows, para o celular que assiste e o PC funcionarem sem mudança: <c>quero_assistir {sessao, sala, codigo}</c> → uma
/// <see cref="PeerLink"/> de envio por pessoa e por transmissão → <c>aceito {sessao, codigo}</c> (ou <c>recusado {sessao, motivo}</c>) → quando o túnel conecta,
/// um SRT listener em 127.0.0.1:porta livre (o LocalRelay do espectador aponta para ele) → os blocos do mux, a partir de um quadro-chave.
/// <c>parar</c> encerra a ligação; <c>reabrir_player</c> refaz o envio.
///
/// Threads: a sala chama <see cref="AoReceber"/> de fundo; a captura e o mux chamam <see cref="AoBloco"/> nas threads deles (só enfileira, nunca espera);
/// cada espectador tem a sua thread de envio. Quem atrasa perde (a fila dele é limitada), e os outros não sentem.
/// </summary>
sealed class Anfitriao : IDisposable
{
    /// <summary>
    /// Teto de pessoas assistindo. Cada uma é uma cópia do vídeo saindo pelo upload do celular (~8 Mbit/s a 1080p30): no 4G, 4 já é muito.
    /// O app Windows usa 16, mas lá o upload costuma ser de fibra.
    /// </summary>
    public const int MaxEspectadores = 4;
    const int LatenciaMs = 80;   // a mesma do app no PC e do player
    const string Jogo = "Tela do celular";

    /// <summary>A transmissão no ar (a Sessao encaminha para ela o que vem da sala). Só uma por vez.</summary>
    public static Anfitriao? Ativo { get; private set; }

    readonly Captura _captura;
    readonly TsMux _mux;
    readonly int _kbps;
    readonly object _gate = new();
    readonly Dictionary<string, Espectador> _espectadores = new();   // sessão -> espectador
    readonly HashSet<string> _aceitando = new();                     // quem está com um pedido sendo preparado (o CreateAsync leva segundos)
    volatile Espectador[] _lista = Array.Empty<Espectador>();        // cópia para os blocos andarem sem lock
    GroupRoom? _sala;
    string _streamId = "", _qualidade = "";
    long _desdeMs;
    volatile bool _rodando;
    LiveInfo? _ultimaLive;
    Thread? _vigia;
    int _jaParou;

    /// <summary>Mudou o número de espectadores (para a tela). Vem de qualquer thread.</summary>
    public event Action? Mudou;
    /// <summary>A transmissão acabou sozinha (trocou de grupo, saiu da conta, a sala fechou). Texto para a tela. Vem de qualquer thread.</summary>
    public event Action<string>? Encerrou;

    public string StreamId => _streamId;
    public int Assistindo { get { lock (_gate) return _espectadores.Count; } }

    /// <param name="kbps">A taxa pedida ao encoder: dimensiona a fila de cada espectador.</param>
    public Anfitriao(Captura captura, TsMux mux, int kbps)
    {
        _captura = captura; _mux = mux; _kbps = kbps;
    }

    /// <summary>Anuncia a live na sala e começa a atender pedidos. Chamar depois do <see cref="Captura.Iniciar"/> (a qualidade vem do tamanho que a captura aceitou).</summary>
    public void Iniciar()
    {
        if (_rodando) return;
        _sala = Sessao.Sala;
        if (_sala is not { Connected: true }) throw new InvalidOperationException("Sem conexão com o grupo.");
        Srt.srt_startup();
        _streamId = RandomId(10);
        _desdeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // o lado menor, como "1080p" no PC: com o celular em pé a altura é o lado maior (o cartão mostrava "1904p")
        _qualidade = $"{Math.Min(_captura.Largura, _captura.Altura)}p {_captura.Fps} FPS";
        _rodando = true;
        Ativo = this;
        Telemetria.Ativo = true;
        _captura.Video += AoVideo;
        _captura.Audio += AoAudio;
        _mux.Saida += AoBloco;
        Sessao.NaoEntregue += AoNaoEntregar;
        Log.Info($"Ao vivo: {Jogo}, {_qualidade}, {_kbps} kbps, {_captura.Largura}x{_captura.Altura}, encoder {_captura.Encoder}, áudio {(_captura.CanaisAudio > 0 ? _captura.CanaisAudio + " canal(is)" : "não")}. Transmissão {_streamId}.");
        Telemetria.Evento("live", new JsonObject
        {
            ["papel"] = "transmite", ["transmissao"] = _streamId, ["jogo"] = Jogo, ["qualidade"] = _qualidade, ["kbps"] = _kbps,
            ["resolucao"] = $"{_captura.Largura}x{_captura.Altura}", ["encoder"] = _captura.Encoder, ["audio"] = _captura.CanaisAudio,
        });
        AtualizarLive(forcar: true);
        _vigia = new Thread(Vigiar) { IsBackground = true, Name = "anfitriao-vigia" };
        _vigia.Start();
    }

    // ------------------------------------------------------------------ captura -> mux -> espectadores

    void AoVideo(byte[] dados, long ptsUs, bool chave)
    {
        try { _mux.Video(dados, ptsUs, chave); }
        catch (Exception e) { AvisarUmaVez("mux (vídeo): " + e.Message); }
    }

    void AoAudio(byte[] dados, long ptsUs)
    {
        try { _mux.Audio(dados, ptsUs); }
        catch (Exception e) { AvisarUmaVez("mux (áudio): " + e.Message); }
    }

    int _avisouErroDoMux;
    void AvisarUmaVez(string texto) { if (Interlocked.Exchange(ref _avisouErroDoMux, 1) == 0) Log.Info("Erro no " + texto); }

    /// <summary>Um bloco do mux (uma unidade de acesso). Só enfileira: nunca espera, a captura não pode travar por causa de um espectador lento.</summary>
    void AoBloco(byte[] bytes, bool inicioDeChave)
    {
        foreach (var e in _lista) e.Enfileirar(bytes, inicioDeChave);
    }

    // ------------------------------------------------------------------ a sala

    /// <summary>Mensagem da sala (de fundo). Só os tipos do envio; o resto a Sessao trata.</summary>
    public void AoReceber(string de, string apelido, JsonObject msg)
    {
        string tipo = JsonSeguro.Texto(msg["tipo"]) ?? "", sid = JsonSeguro.Texto(msg["sessao"]) ?? "";
        switch (tipo)
        {
            case "quero_assistir":
                _ = Task.Run(() => AceitarAsync(de, apelido, sid, JsonSeguro.Texto(msg["sala"]) ?? "", JsonSeguro.Texto(msg["codigo"]) ?? ""));
                break;
            case "reabrir_player":
                Espectador? e;
                lock (_gate) _espectadores.TryGetValue(sid, out e);
                // a sessão é escrita pelo app do amigo: só quem é o outro lado da ligação mexe nela
                if (e == null || e.Link.PeerId != de) return;
                Log.Info($"{apelido} vai reabrir o player; refazendo o envio para ele.");
                e.Reabrir = true;
                break;
            case "parar":
                lock (_gate) _espectadores.TryGetValue(sid, out e);
                if (e == null || e.Link.PeerId != de) return;
                Log.Info($"{apelido} fechou o player.");
                Encerrar(sid, avisar: false);
                break;
        }
    }

    /// <summary>A sessão é de um espectador desta transmissão (a Sessao usa para não confundir com o player do próprio celular).</summary>
    public bool TemSessao(string sid) { lock (_gate) return _espectadores.ContainsKey(sid); }

    /// <summary>Alguém pediu para assistir (MainForm.Grupo.cs, AcceptViewerAsync): ligação nova só para ele e um envio SRT só para ele.</summary>
    async Task AceitarAsync(string de, string apelido, string sid, string streamId, string codigo)
    {
        var sala = _sala;
        if (sala == null || sid == "") return;
        // a sala entrega por conta (README, "A sala com a mesma conta em dois dispositivos"): um pedido da minha própria conta vem de outro
        // dispositivo meu e não dá para responder a ele sem o aceito voltar para cá. Ignora, como se não tivesse chegado.
        if (de == Sessao.Conta.ContaId) { Log.Info("Pedido para assistir da minha própria conta; ignorei (a sala entrega por conta)."); return; }
        lock (_gate) if (_espectadores.ContainsKey(sid)) return;
        if (!_rodando || streamId != _streamId)
        {
            await Recusar(sala, de, sid, _rodando ? "Essa transmissão do celular acabou, mas tem outra no ar: clique em Assistir de novo." : "O celular já parou de transmitir.");
            return;
        }
        // pedido em andamento da mesma pessoa: ignora (um app adulterado abriria centenas de ligações com uma rajada)
        lock (_gate)
        {
            if (!_aceitando.Add(de)) { Log.Info($"{apelido} pediu para assistir de novo antes de eu terminar o pedido anterior; ignorei este."); return; }
        }
        PeerLink? link = null;
        try
        {
            bool cheio;
            lock (_gate) cheio = _espectadores.Count + _aceitando.Count - 1 >= MaxEspectadores && !_espectadores.Values.Any(x => x.Link.PeerId == de);
            if (cheio)
            {
                Log.Info($"{apelido} pediu para assistir, mas já são {MaxEspectadores} espectadores (o máximo); recusei.");
                await Recusar(sala, de, sid, $"O celular já tem o máximo de {MaxEspectadores} pessoas assistindo. Tente quando alguém sair.");
                return;
            }
            // quem reabre o player ou reconecta manda um pedido novo, e o novo vale
            string[] velhas;
            lock (_gate) velhas = _espectadores.Where(x => x.Value.Link.PeerId == de).Select(x => x.Key).ToArray();
            foreach (var velha in velhas) { Log.Info($"{apelido} pediu para assistir de novo; troco a ligação antiga pela nova."); Encerrar(velha, avisar: false); }
            try
            {
                var lan = IpLocal();
                link = await PeerLink.CreateAsync(sid, de, apelido, iAmSender: true, localMode: false, Sessao.Apelido,
                    lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), streamId);
                link.Connect(codigo);
            }
            catch (Exception e)
            {
                link?.Dispose();   // senão o socket, as threads e a reserva no relay ficariam abertos
                Log.Info($"{apelido} pediu para assistir, mas não deu para preparar a conexão: {e.Message}");
                await Recusar(sala, de, sid, $"O celular não conseguiu preparar a conexão ({e.Message}).");
                return;
            }
            var esp = new Espectador(link, apelido, FilaMaxima(_kbps));
            bool entrou;
            lock (_gate)
            {
                entrou = _rodando;   // pararam a live durante o await
                if (entrou) { _espectadores[sid] = esp; _lista = _espectadores.Values.ToArray(); }
            }
            if (!entrou) { link.Dispose(); await Recusar(sala, de, sid, "O celular já parou de transmitir."); return; }
            link.Tunnel.StateChanged += (s, msg) => Log.Info($"ligação com {apelido}: {s} {msg}");
            esp.Thread = new Thread(() => LacoDeEnvio(esp)) { IsBackground = true, Name = "envio-" + apelido };
            esp.Thread.Start();
            link = null;   // daqui em diante é do espectador (Encerrar solta)
            Log.Info($"{apelido} pediu para assistir; conectando (agora são {Assistindo} espectador(es)).");
            await sala.SendTo(de, new JsonObject { ["tipo"] = "aceito", ["sessao"] = sid, ["codigo"] = esp.Link.MyCode });
            AtualizarLive();
        }
        catch (Exception e) { Log.Info($"Pedido de {apelido}: {e.Message}"); link?.Dispose(); }
        finally { lock (_gate) _aceitando.Remove(de); }
    }

    static async Task Recusar(GroupRoom sala, string para, string sid, string motivo)
    {
        try { await sala.SendTo(para, new JsonObject { ["tipo"] = "recusado", ["sessao"] = sid, ["motivo"] = motivo }); }
        catch (Exception e) { Log.Info("Não deu para mandar o recusado: " + e.Message); }
    }

    /// <summary>
    /// Mesma regra do app Windows: um amigo fora da sala por um instante, com o túnel de pé, não fechou o app. "reabrir_player" não vem para cá (nós é que o recebemos);
    /// o que mandamos (aceito, recusado, parar) não entregue só encerra se o túnel não conectou.
    /// </summary>
    void AoNaoEntregar(string para, JsonObject? msg)
    {
        string sid = JsonSeguro.Texto(msg?["sessao"]) ?? "", tipo = JsonSeguro.Texto(msg?["tipo"]) ?? "";
        Espectador? e;
        lock (_gate) _espectadores.TryGetValue(sid, out e);
        if (e == null || e.Link.PeerId != para) return;
        if (e.Link.Connected) { Log.Info($"A sala não entregou \"{tipo}\" para {e.Apelido} (fora da sala agora), mas a ligação direta continua."); return; }
        Encerrar(sid, avisar: false);
    }

    /// <summary>Encerra a ligação com um espectador (MainForm.Grupo.cs, EndLink). <paramref name="avisar"/> manda "parar" para ele.</summary>
    void Encerrar(string sid, bool avisar, bool esperar = false)
    {
        Espectador? e;
        lock (_gate)
        {
            if (!_espectadores.Remove(sid, out e)) return;
            _lista = _espectadores.Values.ToArray();
        }
        e.Fim = true;
        e.Acordar();
        Task? aviso = null;
        if (avisar && _sala is { } sala) aviso = sala.SendTo(e.Link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = sid });
        Log.Info($"Ligação com {e.Apelido} encerrada.");
        Telemetria.Evento("live_fim", new JsonObject { ["papel"] = "transmite", ["transmissao"] = _streamId, ["ligacao"] = sid, ["outro_apelido"] = e.Apelido,
            ["minutos"] = Math.Round((Environment.TickCount64 - e.CriadoEm) / 60000.0, 1), ["motivo"] = avisar ? "parou_a_live" : "espectador_saiu" });
        // liberar o relay espera a resposta da Cloudflare: fora da thread de quem chamou
        var t = Task.Run(() => { try { aviso?.Wait(1500); } catch { } e.Link.Dispose(); });
        if (esperar) { try { t.Wait(2500); } catch { } }
        AtualizarLive();
    }

    /// <summary>Para a transmissão: avisa cada espectador, fecha as ligações e tira a live da sala. Pode ser chamado mais de uma vez. Não mexe na captura (é do serviço).</summary>
    public void Parar()
    {
        if (Interlocked.Exchange(ref _jaParou, 1) != 0) return;
        bool teveLive = _rodando;   // Iniciar chegou a anunciar (e a emitir o "live"); sem isto não há o que fechar
        _rodando = false;
        if (Ativo == this) Ativo = null;
        _captura.Video -= AoVideo; _captura.Audio -= AoAudio; _mux.Saida -= AoBloco;
        Sessao.NaoEntregue -= AoNaoEntregar;
        string[] todos;
        lock (_gate) todos = _espectadores.Keys.ToArray();
        foreach (var sid in todos) Encerrar(sid, avisar: true, esperar: true);
        _sala?.SetLive(null);
        _ultimaLive = null;
        // o fim da live em si, como o app Windows manda (MainForm.Telemetria.cs, TelemetriaLive): sem ele a "live" propria nunca fechava no servidor
        // (só as ligações com cada espectador, que têm o id da sessão); visto no bug hunt de 2026-11-27 com o Samsung A11
        if (teveLive) Telemetria.Evento("live_fim", new JsonObject { ["transmissao"] = _streamId, ["ligacao"] = "propria" });
        Telemetria.Ativo = false;
        Log.Info("Transmissão parada.");
        Mudou?.Invoke();
    }

    public void Dispose() => Parar();

    /// <summary>O LiveInfo que vai para a sala: só manda de novo quando algo muda (a contagem de quem assiste).</summary>
    void AtualizarLive(bool forcar = false)
    {
        lock (_gate)
        {
            var info = _rodando ? new LiveInfo(_streamId, Jogo, _qualidade, _desdeMs, _espectadores.Count) : null;
            if (!forcar && Equals(info, _ultimaLive)) return;
            _ultimaLive = info;
            try { _sala?.SetLive(info); } catch (Exception e) { Log.Info("SetLive: " + e.Message); }
        }
        Mudou?.Invoke();
    }

    // ------------------------------------------------------------------ vigia (1 s) e números (2 s)

    void Vigiar()
    {
        var relogio = Stopwatch.StartNew();
        double ultimo = 0;
        int volta = 0;
        while (_rodando)
        {
            Thread.Sleep(1000);
            if (!_rodando) break;
            // a sala em que anunciei sumiu (trocou de grupo, saiu da conta, atualização obrigatória): a live deixa de existir para o grupo
            if (Sessao.Sala != _sala || Atualizacao.BloqueiaSala)
            {
                Log.Info("A sala do grupo mudou; encerrando a transmissão.");
                _ = Task.Run(() => { Parar(); Encerrou?.Invoke("A transmissão parou porque você saiu do grupo em que estava ao vivo."); });
                return;
            }
            try { LimparSumidos(); } catch (Exception e) { Log.Info("vigia: " + e.Message); }
            if (++volta % 2 != 0) continue;
            double agora = relogio.Elapsed.TotalSeconds, dt = agora - ultimo; ultimo = agora;
            foreach (var e in _lista) Numeros(e, dt);
        }
    }

    /// <summary>
    /// MainForm.Grupo.cs, EndLinksOfPeersGoneFromRoom: amigo fora da sala E túnel parado = fechou o app. Sumir da sala sozinho não conta (a conexão com a sala cai por ~2 s de vez em quando).
    /// Além disso, quem não conectou em 60 s libera a vaga (o túnel já desistiu).
    /// </summary>
    void LimparSumidos()
    {
        var sala = _sala;
        if (sala is not { Connected: true }) return;
        foreach (var e in _lista)
        {
            if (e.Link.Connected) { e.UltimoBom = Environment.TickCount64; continue; }
            long parado = Environment.TickCount64 - Math.Max(e.UltimoBom, e.CriadoEm);
            if (parado <= 5000) continue;
            if (!sala.IsPresent(e.Link.PeerId)) { Encerrar(e.Link.SessionId, avisar: false); continue; }
            if (parado > 60_000) { Log.Info($"{e.Apelido} não conectou em 60 s; liberando a vaga."); Encerrar(e.Link.SessionId, avisar: true); }
        }
    }

    /// <summary>A linha ENVIO de cada espectador, a cada 2 s, para o logcat e para a telemetria.</summary>
    void Numeros(Espectador e, double dt)
    {
        long bytes = Interlocked.Read(ref e.BytesEnviados);
        double mbps = (bytes - e.BytesAntes) * 8 / Math.Max(dt, 0.001) / 1e6;
        e.BytesAntes = bytes;
        var t = e.Link.Tunnel;
        string caminho = t.Target is { } c ? (c.Kind == CandKind.Direct ? "direto" : "relay") : "?";
        string srt = e.SrtPronto ? "conectado" : "esperando";
        long descartados = Interlocked.Read(ref e.BlocosDescartados), falhas = Interlocked.Read(ref e.FalhasDeEnvio);
        int filaKb = e.BytesNaFila / 1024;
        Log.Info($"ENVIO {e.Apelido} {mbps:0.0} Mbit/s, blocos descartados {descartados}, falhas de envio {falhas}, fila {filaKb} KB, túnel {t.State}, caminho {caminho}, srt {srt}, ida e volta {t.RttMs:0} ms");
        var inv = CultureInfo.InvariantCulture;
        Telemetria.Serie("transmitir.csv", "hora_utc,ligacao,caminho,tunel,srt,mbit_s,descartados,falhas,fila_kb,ida_e_volta_ms",
            string.Join(",", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), e.Link.SessionId, caminho, t.State, srt, mbps.ToString("0.00", inv), descartados, falhas, filaKb, t.RttMs.ToString("0", inv)));
    }

    // ------------------------------------------------------------------ o envio de cada espectador (StreamSender.OutputLoop)

    /// <summary>
    /// Espera o túnel dele conectar, abre o SRT listener numa porta livre (o relay dele passa a apontar para ela), espera ele conectar no SRT e manda os blocos.
    /// Quando ele fecha ou reconecta o SRT, recomeça. Um espectador com problema não derruba os outros: espera mais entre as tentativas dele.
    /// </summary>
    void LacoDeEnvio(Espectador e)
    {
        int falhasRapidas = 0;
        while (_rodando && !e.Fim)
        {
            // sem senha = o túnel dele ainda não conectou
            string senha = e.Link.Tunnel.Passphrase;
            if (!e.Link.Connected || senha == "") { Thread.Sleep(200); continue; }
            int porta = LocalRelay.FreeUdpPort();
            e.Link.Relay.FixedTarget = new IPEndPoint(IPAddress.Loopback, porta);
            int escuta = Srt.Erro, u = Srt.Erro;
            var inicio = Stopwatch.StartNew();
            try
            {
                escuta = Srt.Escutar(porta, senha, LatenciaMs);
                while (_rodando && !e.Fim && !e.Reabrir && e.Link.Connected && u == Srt.Erro) u = Srt.Aceitar(escuta, 250);
                if (u != Srt.Erro)
                {
                    Log.Info($"{e.Apelido} conectou no SRT ({inicio.ElapsedMilliseconds} ms depois do túnel).");
                    u = EnviarBlocos(e, escuta, u);
                }
            }
            catch (Exception ex) { Log.Info($"Envio para {e.Apelido}: {ex.Message}"); }
            finally
            {
                e.SrtPronto = false;
                e.LimparFila();
                if (u != Srt.Erro) Srt.srt_close(u);
                if (escuta != Srt.Erro) Srt.srt_close(escuta);
                e.Reabrir = false;
            }
            falhasRapidas = inicio.Elapsed.TotalSeconds < 3 ? falhasRapidas + 1 : 0;
            if (falhasRapidas == 5) Log.Info($"O envio para {e.Apelido} falhou 5 vezes seguidas logo ao abrir; tentando a cada 5 s (os outros espectadores seguem normais).");
            for (int i = 0; i < (falhasRapidas >= 5 ? 50 : 3) && _rodando && !e.Fim; i++) Thread.Sleep(100);
        }
    }

    /// <summary>Manda a fila para o SRT até ele cair. Devolve o socket em uso (pode ter trocado se o espectador reconectou), para o chamador fechar.</summary>
    int EnviarBlocos(Espectador e, int escuta, int u)
    {
        e.Comecar();
        try { _captura.PedirChave(); } catch (Exception ex) { Log.Info("PedirChave: " + ex.Message); }
        long ultimaSonda = Environment.TickCount64;
        while (_rodando && !e.Fim && !e.Reabrir && e.Link.Connected)
        {
            var bloco = e.Proximo(250);
            long agora = Environment.TickCount64;
            if (agora - ultimaSonda >= 200 || bloco == null)
            {
                ultimaSonda = agora;
                if (Srt.srt_getsockstate(u) != Srt.SrtsConnected) { Log.Info($"{e.Apelido} saiu do SRT."); return u; }
                // ele reconectou o SRT (o player dele abriu de novo sem pedir pela sala): a ligação nova entra no lugar da velha, e começa num quadro-chave
                int novo = Srt.Aceitar(escuta, 0);
                if (novo != Srt.Erro)
                {
                    Log.Info($"{e.Apelido} reconectou no SRT; troquei a ligação.");
                    Srt.srt_close(u);
                    u = novo;
                    e.Comecar();
                    try { _captura.PedirChave(); } catch { }
                    continue;   // o bloco que estava na mão era para a velha
                }
            }
            if (bloco == null) continue;
            if (!EnviarBloco(e, u, bloco)) e.PerderAteChave();
        }
        return u;
    }

    /// <summary>Um bloco em pedaços de 1316 bytes (7 pacotes TS, o pkt_size do ffmpeg). Falso se o SRT não aceitou (fila cheia por mais de 150 ms): o resto do bloco se perde.</summary>
    static bool EnviarBloco(Espectador e, int u, byte[] bloco)
    {
        for (int off = 0; off < bloco.Length; off += 1316)
        {
            int n = Math.Min(1316, bloco.Length - off);
            if (!Srt.Enviar(u, bloco, off, n)) { Interlocked.Increment(ref e.FalhasDeEnvio); return false; }
            Interlocked.Add(ref e.BytesEnviados, n);
        }
        return true;
    }

    /// <summary>
    /// Quanto cabe na fila de cada espectador: ~0,6 s da taxa (mínimo 400 KB, para caber um quadro-chave grande). Passou disso, ele está atrasado:
    /// joga tudo fora e recomeça no próximo quadro-chave (menos 1 s parado para ele do que acumular atraso para sempre).
    /// </summary>
    static int FilaMaxima(int kbps) => Math.Max(400_000, kbps * 75);

    // ------------------------------------------------------------------ um espectador

    sealed class Espectador(PeerLink link, string apelido, int limiteFila)
    {
        public PeerLink Link { get; } = link;
        public string Apelido { get; } = apelido;
        public Thread? Thread;
        public volatile bool Fim, Reabrir;
        public volatile bool SrtPronto;
        public long CriadoEm = Environment.TickCount64, UltimoBom = Environment.TickCount64;
        public long BytesEnviados, BytesAntes, BlocosDescartados, FalhasDeEnvio;

        readonly object _fila = new();
        readonly Queue<byte[]> _blocos = new();
        int _bytes;
        bool _esperaChave = true;
        public int BytesNaFila => Volatile.Read(ref _bytes);

        /// <summary>O SRT dele conectou: começa do zero, no próximo quadro-chave.</summary>
        public void Comecar()
        {
            lock (_fila) { _blocos.Clear(); _bytes = 0; _esperaChave = true; }
            SrtPronto = true;
        }

        public void LimparFila() { lock (_fila) { _blocos.Clear(); _bytes = 0; _esperaChave = true; } }

        /// <summary>O SRT não deu conta de um bloco: o que vem a seguir não decodifica sem um quadro-chave novo.</summary>
        public void PerderAteChave()
        {
            lock (_fila)
            {
                Interlocked.Add(ref BlocosDescartados, _blocos.Count + 1);
                _blocos.Clear(); _bytes = 0; _esperaChave = true;
            }
        }

        public void Acordar() { lock (_fila) Monitor.PulseAll(_fila); }

        /// <summary>Chamado pela thread da captura/mux: nunca espera.</summary>
        public void Enfileirar(byte[] bloco, bool inicioDeChave)
        {
            if (!SrtPronto) return;
            lock (_fila)
            {
                if (_esperaChave)
                {
                    if (!inicioDeChave) return;   // quem entra com a captura rodando começa no próximo quadro-chave
                    _esperaChave = false;
                }
                if (_bytes + bloco.Length > limiteFila)
                {
                    Interlocked.Add(ref BlocosDescartados, _blocos.Count + 1);
                    _blocos.Clear(); _bytes = 0;
                    if (!inicioDeChave) { _esperaChave = true; return; }
                }
                _blocos.Enqueue(bloco);
                _bytes += bloco.Length;
                Monitor.Pulse(_fila);
            }
        }

        public byte[]? Proximo(int esperaMs)
        {
            lock (_fila)
            {
                if (_blocos.Count == 0 && !Fim) Monitor.Wait(_fila, esperaMs);
                if (_blocos.Count == 0) return null;
                var b = _blocos.Dequeue();
                _bytes -= b.Length;
                return b;
            }
        }
    }

    // ------------------------------------------------------------------ pequenas

    static string RandomId(int n)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, n));
    }

    /// <summary>O IP da rede local do celular (para o endereço "dentro da operadora" do código): o mesmo do player, num lugar só (a trava do espelho só libera o IP de lá).</summary>
    static IPAddress? IpLocal() => Streemu.Proto.Player.PecaDoPlayer.IpLocal();
}
