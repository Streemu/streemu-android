using System.Globalization;
using System.Net;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Proto.Filme;

/// <summary>
/// O SRT de UM espectador do filme pela transmissão (spec 028, 5.2): o que o <c>LacoSaida</c> do <c>EnvioStream</c> do Windows faz com um ffmpeg, aqui com o libsrt (<see cref="Srt"/>), como o
/// <c>LacoDeEnvio</c> da live. Um listener numa porta livre (o relay da ligação dele aponta para ela) que dura a ligação inteira: o player dele abre o SRT ao receber o
/// <c>filme_stream_epoca</c> (e de novo se reabrir), cada conexão nova troca a antiga e liga a <see cref="SaidaFilme"/> ao <see cref="EnvioFilmeCopia"/> (que começa pelo último GOP guardado).
/// </summary>
sealed class SaidaSrtFilme : IDisposable
{
    readonly EnvioFilmeCopia _envio;
    readonly SaidaFilme _saida;
    readonly PeerLink _link;
    volatile bool _fim, _srtPronto;
    readonly Thread _thread;
    readonly Timer _numeros;
    long _falhasDeEnvio, _bytesAntes;
    readonly System.Diagnostics.Stopwatch _desde = System.Diagnostics.Stopwatch.StartNew();
    double _ultimoInstanteS;

    public SaidaSrtFilme(EnvioFilmeCopia envio, SaidaFilme saida, PeerLink link)
    {
        _envio = envio; _saida = saida; _link = link;
        _thread = new Thread(Laco) { IsBackground = true, Name = "filme-srt-" + saida.Apelido };
        _thread.Start();
        _numeros = new Timer(_ => Numeros(), null, 2000, 2000);
    }

    /// <summary>
    /// A linha ENVIO-FILME de cada espectador, a cada 2 s, para o log e para a telemetria (como o <c>Numeros</c> da live): Mbit/s, blocos descartados e falhas de envio (se crescem, quem assiste vê pulos),
    /// fila, quanto o envio já passou da posição e o caminho da ligação (direto ou relay).
    /// </summary>
    void Numeros()
    {
        if (_fim) return;
        try
        {
            double agora = _desde.Elapsed.TotalSeconds, dt = Math.Max(0.001, agora - _ultimoInstanteS); _ultimoInstanteS = agora;
            long bytes = Interlocked.Read(ref _saida.BytesEnviados);
            double mbps = (bytes - _bytesAntes) * 8 / dt / 1e6; _bytesAntes = bytes;
            var t = _link.Tunnel;
            string caminho = t.Target is { } c ? (c.Kind == CandKind.Direct ? "direto" : "relay") : "?";
            string srt = _srtPronto ? "conectado" : "esperando";
            long descartados = Interlocked.Read(ref _saida.BlocosDescartados), falhas = Interlocked.Read(ref _falhasDeEnvio);
            int filaKb = _saida.BytesNaFila / 1024;
            double adiantado = _envio.Adiantado;
            var inv = CultureInfo.InvariantCulture;
            Log.Info($"ENVIO-FILME {_saida.Apelido} época {_envio.Epoca} {mbps:0.0} Mbit/s, blocos descartados {descartados}, falhas de envio {falhas}, fila {filaKb} KB, adiantado {adiantado:0.#} s, leitura {_envio.VelocidadeDeLeitura:0.00}x, túnel {t.State}, caminho {caminho}, srt {srt}, ida e volta {t.RttMs:0} ms");
            Telemetria.Serie("filme-envio.csv", "hora_utc,ligacao,epoca,caminho,tunel,srt,mbit_s,descartados,falhas,fila_kb,adiantado_s,leitura_x,ida_e_volta_ms",
                string.Join(",", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), _link.SessionId, _envio.Epoca, caminho, t.State, srt, mbps.ToString("0.00", inv), descartados, falhas, filaKb,
                    double.IsNaN(adiantado) ? "" : adiantado.ToString("0.0", inv), double.IsNaN(_envio.VelocidadeDeLeitura) ? "" : _envio.VelocidadeDeLeitura.ToString("0.00", inv), t.RttMs.ToString("0", inv)));
        }
        catch (Exception e) { Log.Info("[filme] estatística do envio: " + e.Message); }   // medir não pode derrubar o envio
    }

    int _escuta = Srt.Erro, _u = Srt.Erro;   // o ouvinte (fixo enquanto a ligação dura) e a conexão do player em uso
    long _ultimaSonda;
    bool _precisaConectar;                    // há conexão nova que ainda não foi ligada à fila da época

    void Laco()
    {
        int rapidas = 0;
        while (!_fim)
        {
            string senha = _link.Tunnel.Passphrase;
            if (!_link.Connected || senha == "") { Thread.Sleep(150); continue; }
            var inicio = System.Diagnostics.Stopwatch.StartNew();
            try { Servir(senha); }
            // texto neutro: o player do outro lado fechar (troca de época, saída) lança aqui, e isso não é falha
            catch (Exception e) { Log.Info($"[filme] envio para {_saida.Apelido} terminou: {e.Message}"); }
            rapidas = inicio.Elapsed.TotalSeconds < 3 ? rapidas + 1 : 0;
            Thread.Sleep(rapidas >= 5 ? 5000 : 100);   // quem falha logo ao abrir não pode girar em laço apertado
        }
    }

    /// <summary>
    /// Um ouvinte SRT por espectador, na MESMA porta enquanto a ligação dura. Campo de 22/11: a cada época o ouvinte era fechado e reaberto noutra porta, e o PC (que começa a conectar assim que
    /// recebe o <c>filme_stream_epoca</c>) às vezes conectava no intervalo: sem imagem por ~4 s, até o player dele tentar de novo. Com a porta fixa a conexão do PC é aceita quando chegar,
    /// antes ou depois de a época abrir, e troca a antiga.
    /// </summary>
    void Servir(string senha)
    {
        int porta = LocalRelay.FreeUdpPort();
        _link.Relay.FixedTarget = new IPEndPoint(IPAddress.Loopback, porta);
        _escuta = Srt.Escutar(porta, senha, EnvioFilmeCopia.LatenciaMs);
        try { while (!_fim && _link.Connected) Volta(); }
        finally { Fechar(); }
    }

    /// <summary>Uma volta: vê se o player caiu ou reconectou (a cada 200 ms), liga a conexão nova à época em curso e manda o próximo bloco.</summary>
    void Volta()
    {
        Sondar();
        if (_u == Srt.Erro) return;   // sem conexão o Aceitar já esperou 250 ms
        if (_precisaConectar && _envio.Epoca > 0) { _envio.Conectar(_saida); _precisaConectar = false; }
        if (_precisaConectar || _saida.EpocaConectada != _envio.Epoca) { Thread.Sleep(20); return; }   // época velha: espera a conexão do player para a nova
        if (_saida.Proximo(250) is { } bloco && !Despachar(_u, bloco)) _saida.PerderAteChave();
    }

    /// <summary>A cada 200 ms (ou a cada volta sem conexão): fecha a conexão que caiu e aceita uma nova, que entra no lugar da velha e recomeça no último GOP da época.</summary>
    void Sondar()
    {
        long agora = Environment.TickCount64;
        if (_u != Srt.Erro && agora - _ultimaSonda < 200) return;
        _ultimaSonda = agora;
        if (_u != Srt.Erro && Srt.srt_getsockstate(_u) != Srt.SrtsConnected) { Log.Info($"[filme] {_saida.Apelido} saiu do SRT."); Largar(); }
        int novo = Srt.Aceitar(_escuta, _u == Srt.Erro ? 250 : 0);
        if (novo == Srt.Erro) return;
        if (_u != Srt.Erro) Srt.srt_close(_u);
        _u = novo; _precisaConectar = true; _srtPronto = true;
        Log.Info($"[filme] {_saida.Apelido} conectou no SRT (época {_envio.Epoca}).");
    }

    void Largar()
    {
        _srtPronto = false; _precisaConectar = false;
        _envio.Desconectar(_saida);
        if (_u != Srt.Erro) Srt.srt_close(_u);
        _u = Srt.Erro;
    }

    void Fechar()
    {
        Largar();
        if (_escuta != Srt.Erro) Srt.srt_close(_escuta);
        _escuta = Srt.Erro;
    }

    /// <summary>Um bloco em pedaços de 1316 bytes (7 pacotes TS, o pkt_size do SRT). Falso se o SRT não aceitou (fila cheia há mais de 150 ms): o resto do bloco se perde.</summary>
    bool Despachar(int u, byte[] bloco)
    {
        for (int off = 0; off < bloco.Length; off += 1316)
        {
            int n = Math.Min(1316, bloco.Length - off);
            if (!Srt.Enviar(u, bloco, off, n)) { Interlocked.Increment(ref _falhasDeEnvio); return false; }
            Interlocked.Add(ref _saida.BytesEnviados, n);
            _envio.ContarBytes(n);
        }
        return true;
    }

    /// <summary>Para o laço (acorda em até 250 ms). A fila é fechada por quem tira a saída do envio (<see cref="EnvioFilmeCopia.Remover"/>).</summary>
    public void Dispose() { _fim = true; _numeros.Dispose(); }
}
