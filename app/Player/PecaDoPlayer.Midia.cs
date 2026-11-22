using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Proto.Transmissao;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Player;

// A mídia de uma peça: o SRT, o demux, o decodificador, o som e os números (a série assistir.csv).
sealed partial class PecaDoPlayer
{
    // ================================================================================================ SRT -> demux -> decodificador

    void LacoSrt()
    {
        var buf = new byte[1500];
        Tunnel? tunelDoDec = null;   // a ligação que o decodificador de agora já viu: uma diferente é live nova
        while (!_fechando)
        {
            int u = Srt.Erro;
            try
            {
                // espera a tela e uma ligação conectada (depois de trocar de rede, a nova)
                while (!_fechando && (_surface == null || _tunel is not { State: TunnelState.Connected } || _relay == null)) Thread.Sleep(50);
                if (_fechando) break;
                var (tunel, relay) = (_tunel!, _relay!);
                string? motivoLatencia = null;
                lock (_midia)
                {
                    if (_fechando) break;
                    // um decodificador que desistiu não volta a abrir: o "Tentar de novo" cria outro (a tela já foi reaproveitada pelo codec liberado)
                    if (_dec is { Desistiu: true } velho) { _dec = null; _ = Task.Run(() => { try { velho.Dispose(); } catch { } }); }
                    _dec ??= CriarDecodificador();
                    // live nova (não um SRT reaberto na mesma ligação): o modo reduzido da live anterior não vale para esta
                    if (tunel != tunelDoDec)
                    {
                        _dec.RecomecarCompleto(); tunelDoDec = tunel;
                        // e a latência do SRT também recomeça: calculada pela ida e volta desta ligação
                        _latenciaSrt = LatenciaForcadaMs > 0 ? LatenciaForcadaMs : LatenciaInicial(tunel);
                        (_escaladas, _amostrasRuins, _ultimaEscaladaMs, _latenciaPedida) = (0, 0, 0, 0);
                        motivoLatencia = "inicio";
                    }
                }
                // um pedido do Painel (descartes por atraso): reabre esta ligação com a latência maior; nas reconexões comuns vale a de antes
                if (Interlocked.Exchange(ref _latenciaPedida, 0) is > 0 and var pedida && motivoLatencia == null && pedida > _latenciaSrt)
                { _latenciaSrt = pedida; motivoLatencia = "escalada"; }
                if (motivoLatencia != null) RegistrarLatencia(tunel, relay, motivoLatencia);
                u = Srt.Conectar(relay.Port, tunel.Passphrase, _latenciaSrt);
                _srtSocket = u;
                _demux.Reiniciar();   // ligação nova: o primeiro pacote não pode virar buraco por causa do contador da anterior
                Streemu.Util.Log.Info($"[{Apelido}] SRT conectado (latência {_latenciaSrt} ms).");
                while (!_fechando && _tunel == tunel && _latenciaPedida == 0)
                {
                    int n = Srt.srt_recvmsg(u, buf, buf.Length);
                    if (n == Srt.Erro)
                    {
                        string e = Srt.UltimoErro;
                        // a libsrt escreve "transmission timed out" (não "timeout"): sem o "timed out", 3 s sem bytes derrubavam e reabriam o SRT
                        if (e.Contains("timeout", StringComparison.OrdinalIgnoreCase) || e.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("again", StringComparison.OrdinalIgnoreCase)) continue;
                        throw new InvalidOperationException("SRT recv: " + e);
                    }
                    try { _demux.Alimentar(buf.AsSpan(0, n)); }
                    catch (Exception e)
                    {
                        // erro do processamento de mídia (demux, decodificador, som): o SRT está bem, então não derruba nem reabre a ligação
                        Streemu.Util.Log.Info("ERRO: mídia: " + e);
                    }
                }
            }
            catch (Exception e) { Streemu.Util.Log.Info($"[{Apelido}] AVISO: " + e.Message + " (reabrindo em 1 s)"); Thread.Sleep(1000); }
            finally { _srtSocket = Srt.Erro; if (u != Srt.Erro) Srt.srt_close(u); }
        }
    }

    /// <summary>
    /// A latência do SRT ao abrir a ligação: 3 ida e volta mais 20 ms (uma retransmissão e folga), entre 80 e 600 ms; pelo relay, no mínimo 200 ms.
    /// Sem medida da ida e volta ainda, presume 120 ms.
    /// </summary>
    static int LatenciaInicial(Tunnel t)
    {
        double rtt = t.RttMs >= 0 ? t.RttMs : RttPresumidoMs;
        int ms = Math.Clamp((int)Math.Round(3 * rtt + 20), LatenciaMinimaMs, LatenciaMaximaInicialMs);
        return PeloRelay(t) ? Math.Max(ms, LatenciaMinimaRelayMs) : ms;
    }

    static bool PeloRelay(Tunnel t) => t.Target is { } c && c.Kind != CandKind.Direct;

    /// <summary>Uma linha no log e o evento srt_latencia (a escolha da latência, no início da live ou numa escalada), com o tamanho real dos buffers UDP no início.</summary>
    void RegistrarLatencia(Tunnel t, LocalRelay relay, string motivo)
    {
        bool semMedida = t.RttMs < 0;
        double rtt = semMedida ? RttPresumidoMs : t.RttMs;
        string caminho = PeloRelay(t) ? "relay" : "direto";
        string via = caminho == "relay" ? "pelo relay" : "direto";
        string forcada = LatenciaForcadaMs > 0 ? ", valor forçado no teste" : "";
        Streemu.Util.Log.Info(motivo == "inicio"
            ? $"[{Apelido}] Latência do SRT: {_latenciaSrt} ms (ida e volta {rtt:0} ms{(semMedida ? " presumida" : "")}, {via}{forcada})."
            : $"[{Apelido}] Latência do SRT subiu para {_latenciaSrt} ms (descartes por atraso; ida e volta {rtt:0} ms, {via}).");
        var o = new JsonObject
        {
            ["latencia_ms"] = _latenciaSrt, ["rtt_ms"] = Math.Round(rtt), ["rtt_presumido"] = semMedida, ["caminho"] = caminho, ["motivo"] = motivo,
            ["ligacao"] = _ligacaoTel, ["escaladas"] = _escaladas, ["forcada"] = LatenciaForcadaMs > 0,
        };
        if (motivo == "inicio")
        {
            // o que o kernel deu de fato depois de pedir 4 MB: se for pouco, o UDP descarta rajadas antes de o app ler
            int bt = t.ReceiveBufferBytes, br = relay.ReceiveBufferBytes;
            o["buffer_udp_tunel_bytes"] = bt; o["buffer_udp_relay_bytes"] = br;
            Streemu.Util.Log.Info($"[{Apelido}] Buffer de recepção UDP (pedido 4096 KB): túnel {bt / 1024} KB, relay local {br / 1024} KB.");
        }
        Telemetria.Evento("srt_latencia", o);
    }

    // ---- UDP do kernel (/proc/net/snmp, linha "Udp:"): contadores do aparelho todo, não só deste app ----
    static volatile bool _snmpIndisponivel;

    /// <summary>RcvbufErrors e InErrors acumulados, ou nulo se o Android não deixa ler (10+ costuma bloquear). Avisa uma vez no log e não tenta mais.</summary>
    static (long RcvbufErros, long InErros)? LerUdpSnmp()
    {
        if (_snmpIndisponivel) return null;
        try
        {
            string[]? nomes = null;
            using var r = File.OpenText("/proc/net/snmp");
            string? linha;
            while ((linha = r.ReadLine()) != null)
            {
                if (!linha.StartsWith("Udp:", StringComparison.Ordinal)) continue;
                var campos = linha.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nomes == null) { nomes = campos; continue; }   // a primeira linha "Udp:" tem os nomes, a segunda os valores
                int ib = Array.IndexOf(nomes, "RcvbufErrors"), ie = Array.IndexOf(nomes, "InErrors");
                if (ib < 0 || ie < 0 || campos.Length != nomes.Length) break;
                return (long.Parse(campos[ib]), long.Parse(campos[ie]));
            }
            throw new InvalidDataException("linha Udp: não encontrada");
        }
        catch (Exception e)
        {
            _snmpIndisponivel = true;
            Streemu.Util.Log.Info($"AVISO: não deu para ler /proc/net/snmp ({e.GetType().Name}); as colunas udp_rcvbuf_erros e udp_in_erros ficam vazias.");
            return null;
        }
    }

    Decodificador CriarDecodificador()
    {
        var d = new Decodificador(_surface!);
        d.Tamanho += (w, h) =>
        {
            Ui(() =>
            {
                if (_fechando) return;
                (VideoW, VideoH) = (w, h);
                Mudou?.Invoke();
            });
            _tentativaMidia++;
            Telemetria.Evento("midia_tentativa", new JsonObject
            {
                ["lado"] = "viewer", ["ligacao"] = _ligacaoTel, ["tentativa"] = $"{_ligacaoTel}-{_tentativaMidia}", ["numero"] = _tentativaMidia,
                // depois do primeiro: o codec mudou o formato de saída (resolução)
                ["gatilho"] = _tentativaMidia == 1 ? "inicio" : "formato_saida",
                ["decodificador"] = "mediacodec", ["codec"] = "h264", ["nome"] = d.Nome,
                ["modo"] = d.Nivel >= 1 ? d.Modo : null,
                ["chaves_do_fabricante"] = d.ChavesDoFabricante, ["renderizador"] = "surface", ["resolucao"] = $"{w}x{h}",
            });
        };
        d.Chegou += (pts, us) => { if (Atraso(pts, us) is double ms) _atrasoChegada.Add(ms); };
        d.NaTela += (pts, us) => { Tocando(); if (Atraso(pts, us) is double ms) _atrasoTela.Add(ms); };
        d.NaoAbriu += f => AoNaoAbrir(d, f);
        _demux.Quadro = d.Entregar;
        // o som abre no primeiro pacote de áudio, quando a PMT já disse quantos canais são
        _demux.Audio = (pes, pts) => ObterSom()?.Entregar(pes, pts);
        return d;
    }

    /// <summary>
    /// Nenhum decodificador abriu (vem da thread do SRT, depois de a escada de recuo acabar). Mostra o erro com "Tentar de novo" e encerra a ligação
    /// (EncerrarLigacao avisa quem transmite): o SRT em si não falhou, mas sem decodificador receber bytes só gasta rede e bateria. Ao zerar o túnel o
    /// laço do SRT sai do recebimento e espera a ligação seguinte, onde recria o decodificador (o que desistiu é descartado).
    /// </summary>
    void AoNaoAbrir(Decodificador d, Decodificador.FalhaAbertura f)
    {
        if (_fechando || _dec != d) return;
        bool transmitindo = ServicoTransmissao.AoVivo;
        string erro = f.CodigoHex ?? f.Tipo;
        Telemetria.Evento("midia_falha", new JsonObject
        {
            ["lado"] = "viewer", ["ligacao"] = _ligacaoTel, ["tentativa"] = $"{_ligacaoTel}-{_tentativaMidia + 1}", ["etapa"] = f.Etapa,
            ["codec"] = f.Codec, ["nome"] = f.Codec, ["nivel"] = f.Nivel, ["modo"] = f.Modo, ["resolucao"] = $"{f.Largura}x{f.Altura}", ["erro"] = erro,
            ["diagnostico"] = f.Diagnostico.Length > 0 ? f.Diagnostico : f.Mensagem, ["transitorio"] = f.Transitorio, ["niveis_tentados"] = f.Tentativas,
            ["transmitindo"] = transmitindo, ["capacidade"] = _capacidade, ["pecas"] = Quantas(),
        });
        string titulo, detalhe;
        // a qualidade acima do que o aparelho declara vem antes: parar a transmissão não resolveria (ex.: 1440p num decodificador que vai até 1920x1920)
        if (_aguenta == Aparelho.Aguenta.Nao)
        {
            titulo = $"Este celular não dá conta de {(_capacidade.Length > 0 ? _capacidade.Split(':')[0] : $"{f.Largura}x{f.Altura}")}.";
            detalhe = "Quem transmite pode baixar a qualidade no PC. (" + erro + ")";
        }
        else if (Quantas() > 1)
        {
            titulo = "Este celular não abriu mais um vídeo.";
            detalhe = "Feche uma das outras lives e tente de novo. (" + erro + ")";
        }
        else if (transmitindo)
        {
            titulo = "Este celular não consegue mostrar o vídeo enquanto transmite.";
            detalhe = "Pare a transmissão para assistir, ou baixe a qualidade dela. (" + erro + ")";
        }
        else
        {
            titulo = "Não deu para abrir o vídeo.";
            detalhe = "Código " + erro + ".";
        }
        Esperando(titulo, detalhe, erro: true);   // antes de EncerrarLigacao: o evento "falha" leva o id da ligação, que o live_fim apaga
        EncerrarLigacao(avisar: true, motivo: "decodificador");
    }

    Som? ObterSom()
    {
        if (_som is { } som) return som;
        lock (_midia) return _fechando ? null : _som ??= new Som(_demux.CanaisAudio) { Mudo = _mudo };
    }

    /// <summary>
    /// Atraso de um quadro, em ms (só no modo de teste, que recebe a hora do PC): a hora do PC agora menos a hora em que o PC gerou o quadro (o PTS).
    /// </summary>
    double? Atraso(long pts90k, long meuUs)
    {
        if (_tunel?.PeerClockOffsetUs is not long off || _utcMenosQpcPc is not long m || pts90k < 0) return null;
        long horaPcUs = meuUs + off + m;
        const long Volta = 1L << 33;
        long agora90k = horaPcUs / 100 * 9 % Volta;
        long d = (agora90k - pts90k) & (Volta - 1);
        if (d > Volta / 2) d -= Volta;
        return d / 90.0;
    }

    /// <summary>
    /// Escalada da latência do SRT. Em 3 amostras de 2 s seguidas com descarte por atraso (pacotes tarde &gt; 0,5% dos recebidos, ou 2 buracos de TS no
    /// intervalo), reabre a ligação com 1,5x a latência, até 1000 ms, no máximo uma vez a cada 20 s e 4 por live. Nunca desce na mesma live. Sem
    /// estatística do SRT (sem ligação aberta) a amostra não conta nem zera. Com latência forçada no teste não escala.
    /// </summary>
    void VerSeEscala(Tunnel t, Srt.Estatisticas? srt, long buracosIntervalo)
    {
        if (LatenciaForcadaMs > 0 || srt is not { } e || t != _tunel) return;
        bool ruim = (e.Recebidos > 0 && e.Descartados > e.Recebidos * 0.005) || buracosIntervalo >= 2;
        _amostrasRuins = ruim ? _amostrasRuins + 1 : 0;
        if (_amostrasRuins < AmostrasRuinsParaEscalar) return;
        long agora = Environment.TickCount64;
        if (_latenciaSrt <= 0 || _latenciaSrt >= LatenciaTetoMs || _escaladas >= MaxEscaladas || (_ultimaEscaladaMs != 0 && agora - _ultimaEscaladaMs < IntervaloEscaladaMs)) return;
        int nova = Math.Min(LatenciaTetoMs, (int)Math.Round(_latenciaSrt * 1.5));
        (_escaladas, _ultimaEscaladaMs, _amostrasRuins) = (_escaladas + 1, agora, 0);
        _latenciaPedida = nova;
    }

    // ================================================================================================ qualidade da live (aviso na tela)

    readonly AvaliadorDeQualidade _avaliador = new();
    long _qualidadeDesdeMs = Environment.TickCount64;
    ResultadoDaQualidade _qualidadeMostrada = new(NivelDaQualidade.Bom, CausaDaQualidade.Nenhuma, "Qualidade boa");   // o que o Painel já publicou (QualidadeAtual só muda na thread da tela)

    static string TextoDaQualidade(ResultadoDaQualidade r) => r.Nivel == NivelDaQualidade.Bom ? "boa" : $"{(r.Nivel == NivelDaQualidade.Ruim ? "ruim" : "alerta")} ({r.Causa.ToString().ToLowerInvariant()})";

    /// <summary>Avalia a amostra (o avaliador roda sempre, para a telemetria); só quando o estado mostrado muda acorda a tela e registra o evento qualidade_aviso.</summary>
    void AvaliarQualidade(AmostraDeQualidade amostra)
    {
        var r = _avaliador.Avaliar(amostra);
        var antes = _qualidadeMostrada;
        if (r == antes) return;
        long agora = Environment.TickCount64;
        double duracao = Math.Round((agora - _qualidadeDesdeMs) / 1000.0, 1);
        (_qualidadeDesdeMs, _qualidadeMostrada) = (agora, r);
        // nivel: bom, alerta, ruim, recolhido (o problema virou o normal e o aviso sumiu) ou normalizado (o verde). Nesses dois, duracao_s é a do episódio inteiro
        bool doEpisodio = r.Estado is EstadoDoAviso.Recolhido or EstadoDoAviso.Normalizado;
        Telemetria.Evento("qualidade_aviso", new JsonObject
        {
            ["nivel"] = r.Estado.ToString().ToLowerInvariant(), ["causa"] = r.Causa.ToString().ToLowerInvariant(), ["ligacao"] = _ligacaoTel,
            ["duracao_s"] = doEpisodio ? Math.Round(_avaliador.DuracaoDoEpisodioS, 1) : duracao, ["mostrado"] = AvisoDeQualidade.Ligado(_ctx), ["lado"] = "viewer",
        });
        Ui(() =>
        {
            if (_fechando) return;
            QualidadeAtual = r;
            QualidadeMudou?.Invoke();
        });
    }

    // ================================================================================================ números (botão de info, e o logcat a cada 2 s)

    async Task Painel()
    {
        long bytesAntes = 0, mostradosAntes = 0, buracosAntes = 0, descartadosAntes = 0, entreguesAntes = 0, filaCheiaAntes = 0;
        (long Rcvbuf, long InErros)? snmpAntes = null;
        Tunnel? tunelAntes = null;
        var relogio = Stopwatch.StartNew();
        double antes = 0;
        while (!_fechando)
        {
            await Task.Delay(2000);
            var t = _tunel; var d = _dec;
            if (t == null)
            {
                AvaliarQualidade(new AmostraDeQualidade(false, false, _tocando, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, _termico));
                continue;
            }
            if (t != tunelAntes) { bytesAntes = t.RxBytes; tunelAntes = t; _avaliador.Reiniciar(); }
            double agora = relogio.Elapsed.TotalSeconds, dt = agora - antes; antes = agora;
            long bytes = t.RxBytes, bytesIntervalo = bytes - bytesAntes; double mbps = bytesIntervalo * 8 / dt / 1e6; bytesAntes = bytes;
            long mostrados = d?.Mostrados ?? 0; double fps = (mostrados - mostradosAntes) / dt; mostradosAntes = mostrados;
            double perda = t.RxViewer.TakeWindowLossPct();
            // SRT (por intervalo, com clear), demux e UDP do kernel: o que diz onde os pacotes se perderam
            Srt.Estatisticas? srt = Srt.LerEstatisticas(_srtSocket, out var est) ? est : null;
            long buracos = _demux.Descontinuidades, descartados = _demux.QuadrosDescartados;
            long buracosIntervalo = buracos - buracosAntes, descartadosIntervalo = descartados - descartadosAntes;
            (buracosAntes, descartadosAntes) = (buracos, descartados);
            var snmp = LerUdpSnmp();
            string udpRcvbuf = "", udpIn = "";
            if (snmp is { } sn && snmpAntes is { } sa) { udpRcvbuf = Math.Max(0, sn.RcvbufErros - sa.Rcvbuf).ToString(); udpIn = Math.Max(0, sn.InErros - sa.InErros).ToString(); }
            snmpAntes = snmp is { } s2 ? (s2.RcvbufErros, s2.InErros) : null;
            VerSeEscala(t, srt, buracosIntervalo);
            long entregues = d?.Entregues ?? 0, filaCheia = d?.DescartadosFilaCheia ?? 0;
            double entreguesPorS = (entregues - entreguesAntes) / dt;
            bool filaCheiaAumentou = filaCheia > filaCheiaAntes;
            (entreguesAntes, filaCheiaAntes) = (entregues, filaCheia);
            int fpsDaLive = Aparelho.LerQualidade(Qualidade)?.Fps ?? 0;
            long.TryParse(udpRcvbuf, out long rcvbufIntervalo);
            AvaliarQualidade(new AmostraDeQualidade(t.State == TunnelState.Connected, srt != null, _tocando && d != null, bytesIntervalo,
                srt?.Recebidos ?? 0, srt?.Perdidos ?? 0, srt?.Descartados ?? 0, buracosIntervalo, t.RttMs, entreguesPorS, fps, fpsDaLive,
                rcvbufIntervalo, filaCheiaAumentou, _termico, dt));
            string s = $"{t.Target}  ida e volta {t.RttMs:0} ms  {mbps:0.0} Mbit/s  perda {perda:0.00}%" +
                       (d == null ? "" : $"\n{VideoW}x{VideoH} {fps:0} quadros/s  {d.Nome}  decodificar {d.MsNoDecodificador:0.0} ms") +
                       (_som is { } som ? $"\nsom: fila {som.NaFilaMs:0} ms (mín. {som.MinimoNaFilaMs:0}), {som.Faltas} faltas, maior intervalo {som.MaiorIntervaloMs:0} ms" : "");
            if (_capacidade.Length > 0) s += $"\ndecodificador para {_capacidade}";
            if (d is { Modo.Length: > 0 }) s += $"\nmodo do decodificador: {d.Modo}";
            if (d is { ChavesDoFabricante.Length: > 0 }) s += $"\nbaixa latência do fabricante: {d.ChavesDoFabricante}";
            s += "\nQualidade: " + TextoDaQualidade(_qualidadeMostrada);
            if (srt is { } e2) s += $"\nSRT: latência {e2.LatenciaMs} ms, ida e volta {e2.RttMs:0} ms, {e2.Perdidos} perdidos / {e2.Descartados} tarde (2 s)";
            var tela = _atrasoTela.Janela();
            if (tela != null) s += $"\natraso até a tela {tela}";
            Logcat.Info("Streemu", $"[{Apelido}] " + s.Replace('\n', ' ') + (d == null ? "" : $" | quadros {d.Entregues}/{d.Mostrados}, {d.DescartadosAteChave} antes da chave, {d.DescartadosFilaCheia} fila cheia; TS {_demux.Descontinuidades} buracos") +
                         (srt is { } e1 ? $" | SRT {e1.LatenciaMs} ms: {e1.Recebidos} recebidos, {e1.Perdidos} perdidos, {e1.Retransmitidos} retrans., {e1.Descartados} tarde, {e1.Belated} fora de hora" : ""));
            // térmico e bateria a cada 10 s (são chamadas ao sistema; a cada 2 s seria à toa)
            if (++_amostra % 5 == 1) { _termico = Aparelho.Termico(_ctx) ?? ""; _bateria = Aparelho.Bateria(_ctx); }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Telemetria.Serie("assistir.csv",
                "hora_utc,ligacao,caminho,mbit_s,perda_pct,ida_e_volta_ms,quadros_s,largura,altura,decodificar_ms,entregues,mostrados,descartados_chave,descartados_fila,ts_buracos,som_fila_ms,som_min_ms,som_faltas,termico,bateria_pct,peca,pecas,srt_latencia_ms,srt_rtt_ms,srt_recebidos,srt_perdidos,srt_retrans,srt_tarde,srt_belated,srt_buf_ms,ts_descartados,ts_buracos_intervalo,udp_rcvbuf_erros,udp_in_erros",
                string.Join(",", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), _ligacaoTel ?? "teste", (t.Target?.ToString() ?? "").Replace(',', ' '),
                    mbps.ToString("0.00", inv), perda.ToString("0.00", inv), t.RttMs.ToString("0", inv), fps.ToString("0.0", inv), VideoW, VideoH,
                    (d?.MsNoDecodificador ?? 0).ToString("0.0", inv), d?.Entregues ?? 0, d?.Mostrados ?? 0, d?.DescartadosAteChave ?? 0, d?.DescartadosFilaCheia ?? 0,
                    _demux.Descontinuidades, (_som?.NaFilaMs ?? 0).ToString("0", inv), (_som?.MinimoNaFilaMs ?? 0).ToString("0", inv), _som?.Faltas ?? 0, _termico, _bateria,
                    Indice, Quantas(),
                    // colunas do SRT vazias quando não há ligação SRT aberta neste instante; as do UDP, quando o Android não deixa ler
                    srt?.LatenciaMs.ToString() ?? "", srt?.RttMs.ToString("0.0", inv) ?? "", srt?.Recebidos.ToString() ?? "", srt?.Perdidos.ToString() ?? "",
                    srt?.Retransmitidos.ToString() ?? "", srt?.Descartados.ToString() ?? "", srt?.Belated.ToString() ?? "", srt?.BufferMs.ToString() ?? "",
                    descartadosIntervalo, buracosIntervalo, udpRcvbuf, udpIn));
            Ui(() => { Numeros = s; NumerosMudaram?.Invoke(); });
        }
    }

}
