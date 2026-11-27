using Stopwatch = System.Diagnostics.Stopwatch;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Android.Content;
using Android.Net;
using Android.Views;
using Streemu.Net;
using Streemu.Proto.Transmissao;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Player;

enum FaseDaPeca { Esperando, Tocando, Erro }

/// <summary>
/// Uma live inteira, do pedido à imagem (spec 003): o "quero_assistir" pela sala, o túnel, o relay local, o SRT, o demux, o decodificador, o som, os
/// números e os eventos de telemetria daquela live. O player pode ter até quatro peças; cada uma tem a própria ligação (sessão), então o "parar" de
/// uma não derruba as outras. A tela só mostra o estado (<see cref="Fase"/>, <see cref="Titulo"/>) e dá a Surface; nada aqui mexe em view.
/// Uso: <c>var p = new PecaDoPlayer(this, RunOnUiThread, conta, apelido, sala, jogo, qualidade, desde, null); p.Iniciar();</c>
/// </summary>
sealed partial class PecaDoPlayer
{
    // Latência do SRT (do lado de quem assiste; o SRT usa o MAIOR valor entre os dois lados e só negocia no handshake)
    const int LatenciaMinimaMs = 80;        // a do app no PC; rede local e 4G direto (ida e volta de 10 a 35 ms) ficam aqui
    const int LatenciaMinimaRelayMs = 200;  // pelo relay (TURN) não vale menos que isto
    const int LatenciaMaximaInicialMs = 600, LatenciaTetoMs = 1000;
    const double RttPresumidoMs = 120;      // enquanto o túnel não mediu a ida e volta
    const int MaxEscaladas = 4;             // por live
    const long IntervaloEscaladaMs = 20_000;
    const int AmostrasRuinsParaEscalar = 3; // 3 amostras de 2 s seguidas
    /// <summary>Teste (só no Debug, <c>--ei latencia_srt N</c>): força esta latência do SRT, sem cálculo e sem escalada. 0 = automática.</summary>
    public static int LatenciaForcadaMs { get; set; }

    readonly Context _ctx;
    readonly Action<Action> _ui;
    public string Conta { get; }
    public string Apelido { get; }
    public string Jogo { get; }
    public string Qualidade { get; }
    public long Desde { get; }
    public string? CodigoTeste { get; }
    /// <summary>Posição desta peça no player e quantas peças há, só para a telemetria (a série assistir.csv separa as peças).</summary>
    public int Indice { get; set; }
    public Func<int> Quantas { get; set; } = () => 1;

    // o que a tela mostra (só muda na thread da tela)
    public FaseDaPeca Fase { get; private set; } = FaseDaPeca.Esperando;
    public string Titulo { get; private set; } = "Preparando a conexão...";
    public string Detalhe { get; private set; } = "";
    /// <summary>O aviso de capacidade do aparelho (na espera) e o mesmo texto curto por cima do vídeo.</summary>
    public string Aviso { get; private set; } = "";
    public string AvisoVideo { get; private set; } = "";
    public string Numeros { get; private set; } = "";
    public int VideoW { get; private set; }
    public int VideoH { get; private set; }
    public string NomeDoDecodificador => _dec?.Nome ?? "";
    public bool Mudo { get => _mudo; set { _mudo = value; var s = _som; if (s != null) s.Mudo = value; } }
    bool _mudo;
    /// <summary>A estimativa de Mbit/s desta live, pela qualidade que a sala anunciou (a base do aviso de dados móveis).</summary>
    public double MbitEstimado => Aparelho.LerQualidade(Qualidade) is { } q ? 8.0 * q.W * q.H * q.Fps / (1920.0 * 1080 * 60) : 8;
    public event Action? Mudou;
    public event Action? NumerosMudaram;
    /// <summary>O estado de qualidade a mostrar mudou (vem na thread da tela); <see cref="QualidadeAtual"/> tem o novo.</summary>
    public event Action? QualidadeMudou;
    public ResultadoDaQualidade QualidadeAtual { get; private set; } = new(NivelDaQualidade.Bom, CausaDaQualidade.Nenhuma, "Qualidade boa");

    // a ligação
    PeerLink? _link;
    bool _pedindo;
    int _tentativa;   // cada pedido novo invalida as esperas do anterior
    Tunnel? _tunel;
    LocalRelay? _relay;
    Decodificador? _dec;
    Som? _som;
    /// <summary>Cria e solta o decodificador e o som: sem isto, a thread do SRT podia criar um depois do fechar, e ele tocava para sempre.</summary>
    readonly object _midia = new();
    readonly TsDemux _demux = new();
    volatile Surface? _surface;
    volatile bool _fechando;
    bool _tocando, _avisoVideoPendente;
    int _srtLigado, _fechada;
    long? _utcMenosQpcPc;
    readonly Amostras _atrasoChegada = new(), _atrasoTela = new();
    Aparelho.Aguenta _aguenta = Aparelho.Aguenta.NaoSei;   // o que o decodificador promete para a qualidade desta live
    string _capacidade = "";
    // a ligação de agora para a telemetria: live (ao pedir), midia_tentativa (o decodificador abriu), midia_video_ok (primeiro quadro), live_fim
    string? _ligacaoTel, _transmissaoTel;
    long _ligacaoDesde, _aceitoEm;
    bool _videoOkTel;
    int _tentativaMidia, _amostra, _bateria = -1;
    // latência do SRT desta live: escolhida ao abrir a ligação, lembrada nas reconexões e só sobe (nunca desce na mesma live)
    int _latenciaSrt, _escaladas, _amostrasRuins;
    long _ultimaEscaladaMs;
    volatile int _latenciaPedida;   // o Painel pede e o laço do SRT reabre a ligação com ela
    volatile int _srtSocket = Srt.Erro;
    string _termico = "";

    public PecaDoPlayer(Context ctx, Action<Action> ui, string conta, string apelido, string jogo, string qualidade, long desde, string? codigoTeste)
    {
        (_ctx, _ui, Conta, Apelido, Jogo, Qualidade, Desde, CodigoTeste) = (ctx, ui, conta, apelido, jogo, qualidade, desde, codigoTeste);
    }

    public bool EstaTocando => _tocando;
    public bool Fechando => _fechando;
    public bool TemLigacao => _link != null;
    public string? IdDaLigacao => _ligacaoTel;

    /// <summary>Começa a assistir. Com <see cref="CodigoTeste"/> (build Debug) conecta ao código do proto-envio, sem sala.</summary>
    public void Iniciar()
    {
        ConferirCapacidade();
        _ = Task.Run(Painel);
        if (CodigoTeste != null) _ = Task.Run(ModoTeste);
        else _ = AssistirAsync();
    }

    /// <summary>A tela da peça existe (ou foi trocada, quando a peça passa de principal para miniatura): o decodificador passa a mostrar nela.</summary>
    public void DefinirSurface(Surface surface)
    {
        _surface = surface;
        _dec?.TrocarSurface(surface);
    }

    /// <summary>A tela da peça está sendo destruída (o player está saindo): o que o decodificador lançar daqui em diante não é falha dele.</summary>
    public void PerderSurface() => _dec?.Saindo();

    public void TentarDeNovo() { if (CodigoTeste == null) _ = AssistirAsync(); }

    // ================================================================================================ estado

    void Ui(Action a) => _ui(a);

    /// <summary>O passo atual (ou o erro, que a tela mostra com "Tentar de novo").</summary>
    void Esperando(string estado, string detalhe = "", bool erro = false)
    {
        Streemu.Util.Log.Info($"[{Apelido}] " + estado + (detalhe.Length > 0 ? " " + detalhe : ""));
        if (erro) Telemetria.Evento("falha", new JsonObject { ["linha"] = estado + (detalhe.Length > 0 ? " " + detalhe : ""), ["ligacao"] = _ligacaoTel, ["lado"] = "viewer" });
        Ui(() =>
        {
            if (_fechando) return;
            _tocando = false;
            (Fase, Titulo, Detalhe) = (erro ? FaseDaPeca.Erro : FaseDaPeca.Esperando, estado, detalhe);
            Mudou?.Invoke();
        });
    }

    /// <summary>O primeiro quadro chegou à tela (vem da thread do decodificador).</summary>
    void Tocando()
    {
        Ui(() =>
        {
            if (_tocando || _fechando) return;
            _tocando = true;
            Fase = FaseDaPeca.Tocando;
            if (!_videoOkTel && _ligacaoTel != null)
            {
                _videoOkTel = true;
                Telemetria.Evento("midia_video_ok", new JsonObject
                {
                    ["lado"] = "viewer", ["ligacao"] = _ligacaoTel, ["tentativa"] = $"{_ligacaoTel}-{_tentativaMidia}", ["numero"] = _tentativaMidia,
                    ["ms"] = _aceitoEm > 0 ? Environment.TickCount64 - _aceitoEm : null, ["decodificador"] = _dec?.Nome, ["resolucao"] = $"{VideoW}x{VideoH}",
                });
            }
            _avisoVideoPendente = _aguenta == Aparelho.Aguenta.Nao && AvisoVideo.Length > 0;
            Mudou?.Invoke();
        });
    }

    /// <summary>Verdadeiro uma vez, na primeira imagem de uma live que o celular talvez não acompanhe: a tela mostra o aviso por alguns segundos.</summary>
    public bool TomarAvisoDeVideo() { bool v = _avisoVideoPendente; _avisoVideoPendente = false; return v; }

    /// <summary>
    /// Pergunta ao decodificador se ele aguenta a qualidade da live. Se o fabricante diz que não, avisa (a live abre assim mesmo: o decodificador
    /// descarta o que não dá tempo de mostrar, então a imagem trava mas o atraso não cresce). Vai para o logcat como "CAPACIDADE".
    /// </summary>
    void ConferirCapacidade()
    {
        if (Aparelho.LerQualidade(Qualidade) is not { } q) return;
        _ = Task.Run(() =>
        {
            _aguenta = Aparelho.AguentaH264(q.W, q.H, q.Fps, out var detalhe);
            _capacidade = $"{q.H}p{q.Fps}: {(_aguenta switch { Aparelho.Aguenta.Sim => "aguenta", Aparelho.Aguenta.Nao => "NÃO aguenta", _ => "não sei" })} ({detalhe})";
            Streemu.Util.Log.Info("CAPACIDADE " + _capacidade);
            Telemetria.Evento("capacidade", new JsonObject
            {
                ["qualidade"] = Qualidade, ["resultado"] = _aguenta switch { Aparelho.Aguenta.Sim => "aguenta", Aparelho.Aguenta.Nao => "nao_aguenta", _ => "nao_sei" }, ["detalhe"] = detalhe,
            });
            // transmitir do celular ocupa o decodificador/encoder de hardware: avisa antes, mas não bloqueia (em muitos aparelhos funciona)
            bool transmitindo = ServicoTransmissao.AoVivo;
            bool naoAguenta = _aguenta == Aparelho.Aguenta.Nao;
            if (!naoAguenta && !transmitindo) return;
            string texto = naoAguenta ? $"Este celular não promete {q.H}p a {q.Fps} quadros. A imagem pode travar; quem transmite pode baixar a qualidade no PC." : "";
            if (transmitindo) texto += (texto.Length > 0 ? " " : "") + "Transmitir e assistir ao mesmo tempo pode não funcionar neste celular.";
            Ui(() =>
            {
                (Aviso, AvisoVideo) = (texto, naoAguenta ? $"Este celular pode não dar conta de {q.H}p{q.Fps}" : "");
                Mudou?.Invoke();
            });
        });
    }

    // ================================================================================================ pedir para assistir

    /// <summary>O mesmo pedido do Windows (MainForm.Grupo.cs, WatchAsync): ligação nova e "quero_assistir" com o meu código; conecta no "aceito".</summary>
    async Task AssistirAsync()
    {
        if (_pedindo || _fechando) return;
        _pedindo = true;   // um toque a mais durante o await abriria uma segunda ligação e o "aceito" da primeira seria descartado
        int tentativa = ++_tentativa;
        EncerrarLigacao(avisar: true);
        // a sala pode estar voltando (depois de trocar de rede ela cai e reconecta em ~3 s): espera até 15 s antes de desistir
        var sala = Sessao.Sala;
        if (sala is not { Connected: true })
        {
            Esperando("Reconectando ao grupo...");
            for (int i = 0; i < 150 && !_fechando && (sala = Sessao.Sala) is not { Connected: true }; i++) await Task.Delay(100);
        }
        if (sala is not { Connected: true }) { _pedindo = false; Esperando("Sem conexão com o grupo.", sala?.Status ?? "", erro: true); return; }
        if (sala.Friends.FirstOrDefault(f => f.Id == Conta)?.Live is not { } live) { _pedindo = false; Esperando($"{Apelido} não está mais ao vivo.", "", erro: true); return; }
        Esperando("Preparando a conexão...");
        string sid = IdAleatorio(12);
        PeerLink link;
        try
        {
            var lan = IpLocal();
            link = await PeerLink.CreateAsync(sid, Conta, Apelido, iAmSender: false, localMode: false, Sessao.Apelido,
                lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), live.StreamId);
        }
        catch (Exception e) { Esperando("Não deu para preparar a conexão.", e.Message, erro: true); return; }
        finally { _pedindo = false; }
        if (_fechando || tentativa != _tentativa) { link.Dispose(); return; }
        _link = link;
        link.Tunnel.StateChanged += (s, msg) => Streemu.Util.Log.Info($"[{Apelido}] ligação {s}: {msg}");
        _ligacaoTel = sid; _transmissaoTel = live.StreamId; _ligacaoDesde = Environment.TickCount64; _aceitoEm = 0; _videoOkTel = false; _tentativaMidia = 0;
        Telemetria.Evento("live", new JsonObject
        {
            ["papel"] = "assiste", ["transmissao"] = live.StreamId, ["ligacao"] = sid, ["outro_apelido"] = Apelido, ["jogo"] = live.Game, ["qualidade"] = live.Quality,
            ["gatilho"] = tentativa == 1 ? "inicio" : "de_novo", ["peca"] = Indice, ["pecas"] = Quantas(),
        });
        try { await sala.SendTo(Conta, new JsonObject { ["tipo"] = "quero_assistir", ["sessao"] = sid, ["sala"] = live.StreamId, ["codigo"] = link.MyCode }); }
        catch (Exception e)
        {
            // sem isto a exceção sumia no Task e a tela ficava em "Preparando a conexão..." para sempre
            EncerrarLigacao(avisar: false);
            Esperando("Não deu para pedir a live.", e.Message, erro: true);
            return;
        }
        Esperando($"Esperando {Apelido} aceitar...");
        _ = Task.Delay(20_000).ContinueWith(_ => Ui(() =>
        {
            if (tentativa == _tentativa && _link == link && link.Tunnel.State is TunnelState.Ready or TunnelState.Discovering && !_tocando)
                Esperando($"{Apelido} não respondeu.", "O app no PC pode estar ocupado ou desatualizado.", erro: true);
        }));
    }

    /// <summary>Mensagem da sala (na thread da tela). Só vale a desta peça: da conta dela e da sessão da ligação de agora (um "parar" de outra live não passa).</summary>
    public void AoReceber(string de, string apelido, JsonObject msg)
    {
        var link = _link;
        // a mensagem vem do PC de outra pessoa: campo com tipo errado vira nulo (GetValue lançaria aqui, na thread da tela)
        if (link == null || de != Conta || Streemu.Util.JsonSeguro.Texto(msg["sessao"]) != link.SessionId) return;
        switch (Streemu.Util.JsonSeguro.Texto(msg["tipo"]))
        {
            case "aceito":
                try { link.Connect(Streemu.Util.JsonSeguro.Texto(msg["codigo"]) ?? ""); }
                catch (Exception e) { Esperando("Não deu para conectar.", $"{apelido} mandou um código inválido ({e.Message}).", erro: true); return; }
                Esperando("Conectando...");
                _aceitoEm = Environment.TickCount64;
                _tunel = link.Tunnel; _relay = link.Relay;
                EsperarConexao(link.Tunnel, _tentativa);
                break;
            case "recusado":
                EncerrarLigacao(avisar: false);
                Esperando($"{apelido} não aceitou.", Streemu.Util.JsonSeguro.Texto(msg["motivo"]) ?? "", erro: true);
                break;
            case "parar":
                EncerrarLigacao(avisar: false);
                Esperando($"{apelido} parou de transmitir.", "", erro: true);
                break;
        }
    }

    public void AoNaoEntregar(string para, JsonObject? msg)
    {
        if (_link != null && para == Conta && Streemu.Util.JsonSeguro.Texto(msg?["sessao"]) == _link.SessionId)
        { EncerrarLigacao(avisar: false); Esperando($"{Apelido} saiu do grupo.", "", erro: true); }
    }

    /// <summary>A live acabou sem "parar" (o PC caiu, por exemplo): a sala avisa que a pessoa não está mais ao vivo nesta sala.</summary>
    public void AoMudarSala()
    {
        if (CodigoTeste != null || _link == null || Sessao.Sala is not { Connected: true } sala) return;
        if (sala.Friends.FirstOrDefault(f => f.Id == Conta)?.Live == null)
        { EncerrarLigacao(avisar: false); Esperando($"A live de {Apelido} terminou.", "", erro: true); }
    }

    /// <summary>Wi-Fi para 4G (ou o contrário) muda o endereço: o túnel antigo morre. Pede de novo com uma ligação nova pela rede nova.</summary>
    public void AoMudarRede()
    {
        if (_fechando || CodigoTeste != null || (_link == null && !_tocando)) return;
        Streemu.Util.Log.Info($"[{Apelido}] A rede mudou: pedindo de novo pela rede nova.");
        Telemetria.Evento("rede_mudou", new JsonObject { ["ligacao"] = _ligacaoTel });
        EncerrarLigacao(avisar: true);
        // a sala também reconecta pela rede nova; espera ela voltar antes de pedir
        _ = Task.Run(async () =>
        {
            Esperando("A rede mudou. Reconectando...");
            // a sala só nota a queda um instante depois (03:18:55.53 a rede mudou, 55.60 a sala caiu): sem esta folga, o pedido saía com a sala
            // ainda "conectada" pela rede velha e falhava. O AssistirAsync espera a sala voltar.
            await Task.Delay(1500);
            Ui(() => _ = AssistirAsync());
        });
    }

    void EsperarConexao(Tunnel t, int tentativa)
    {
        _ = Task.Run(async () =>
        {
            var fim = DateTime.UtcNow.AddSeconds(30);
            while (t.State != TunnelState.Connected)
            {
                if (_fechando || tentativa != _tentativa) return;
                if (DateTime.UtcNow > fim) { Esperando("Não conectou.", "Nenhum caminho de rede funcionou em 30 s. Tente de novo, ou troque de rede.", erro: true); return; }
                await Task.Delay(50);
            }
            Streemu.Util.Log.Info($"[{Apelido}] Conectado por {t.Target} em {t.ConnectMs} ms.");
            Ui(() => { if (!_tocando && Fase == FaseDaPeca.Esperando) { Titulo = "Abrindo o vídeo..."; Mudou?.Invoke(); } });
            if (Interlocked.Exchange(ref _srtLigado, 1) == 0) new Thread(LacoSrt) { IsBackground = true, Name = "srt" }.Start();
        });
    }

    /// <summary>Fecha a ligação atual; com <paramref name="avisar"/>, manda "parar" para quem transmite (como o Windows faz quando o player fecha).</summary>
    void EncerrarLigacao(bool avisar, string? motivo = null)
    {
        var link = Interlocked.Exchange(ref _link, null);
        if (link == null) return;
        FimDaLive(motivo ?? (avisar ? "pedido_de_novo" : "fim"));
        if (avisar && Sessao.Sala is { } sala) _ = sala.SendTo(link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = link.SessionId });
        if (_tunel == link.Tunnel) { _tunel = null; _relay = null; }
        // depois que o SRT largar o relay: fechar agora faria o laço tentar o relay morto, que é o que ele já faz ao perder a conexão
        _ = Task.Delay(300).ContinueWith(_ => link.Dispose());
    }

    /// <summary>O live_fim da ligação de agora (uma vez por ligação).</summary>
    void FimDaLive(string motivo)
    {
        if (Interlocked.Exchange(ref _ligacaoTel, null) is not { } lig) return;
        Telemetria.Evento("live_fim", new JsonObject
        {
            ["papel"] = "assiste", ["transmissao"] = _transmissaoTel, ["ligacao"] = lig, ["outro_apelido"] = Apelido,
            ["minutos"] = Math.Round((Environment.TickCount64 - _ligacaoDesde) / 60000.0, 1), ["teve_video"] = _videoOkTel, ["motivo"] = motivo, ["peca"] = Indice,
        });
    }

    /// <summary>
    /// Fecha esta peça: manda "parar" para quem transmite e solta decodificador, som e ligação. Tudo o que espera (o "parar" pela sala, o fim do TURN,
    /// as threads do som e do decodificador) sai da thread da tela: somado, passava de 2 s com rede ruim, e o Android acusava "o app não responde".
    /// </summary>
    public void Fechar()
    {
        if (Interlocked.Exchange(ref _fechada, 1) == 1) return;
        _fechando = true;
        FimDaLive("fechou o player");
        var link = Interlocked.Exchange(ref _link, null);
        var sala = Sessao.Sala;
        Decodificador? dec; Som? som;
        lock (_midia) { dec = _dec; _dec = null; som = _som; _som = null; }
        var (tunel, relay) = CodigoTeste != null ? (_tunel, _relay) : (null, null);
        _ = Task.Run(async () =>
        {
            if (link != null && sala != null)
                try { await sala.SendTo(link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = link.SessionId }).WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            try { dec?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar o decodificador: " + e.Message); }
            try { som?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar o som: " + e.Message); }
            try { link?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar a ligação: " + e.Message); }
            try { tunel?.Dispose(); relay?.Dispose(); } catch { }
        });
    }

    // ================================================================================================ modo de teste (código colado, sem sala)

    async Task ModoTeste()
    {
        try
        {
            var t = new Tunnel(localMode: false) { Nick = "celular" };
            t.StateChanged += (s, msg) => Streemu.Util.Log.Info($"{s}: {msg}");
            var pub = await t.DiscoverAsync();
            var lan = IpLocal();
            string codigo = new PeerCode(pub.Address, pub.Port, t.Secret, lan != null ? new IPEndPoint(lan, t.LocalPort) : null).Encode();
            // o proto-envio manda, a cada segundo, a hora do PC junto com o relógio do túnel dele (ver Atraso)
            t.Control += m =>
            {
                if (m["tipo"]?.GetValue<string>() == "relogio-envio" && m["utc_us"] is { } utc && m["qpc_us"] is { } qpc)
                    _utcMenosQpcPc = utc.GetValue<long>() - qpc.GetValue<long>();
            };
            var relay = new LocalRelay("player");
            t.ToViewer = d => relay.Deliver(d);
            relay.Outgoing = d => t.SendMedia(Tunnel.TToSender, d);
            _tunel = t; _relay = relay;
            Streemu.Util.Log.Info($"MEU CÓDIGO: {codigo} ({Apelido})");
            Esperando("Conectando ao código...", "Meu código está no logcat (MEU CÓDIGO).");
            t.ConnectTo(PeerCode.Parse(CodigoTeste!.Trim().ToUpperInvariant()));
            EsperarConexao(t, _tentativa);
        }
        catch (Exception e) { Esperando("Falhou ao preparar.", e.Message, erro: true); }
    }

    // ================================================================================================ utilidades

    internal static IPAddress? IpLocal()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 53);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch { return null; }
    }

    static string IdAleatorio(int n)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, n));
    }
}
