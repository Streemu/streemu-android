using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// O lado ANFITRIÃO do Cinema, sem nada do Android (portável, testável em net10.0): o que o MainForm.Filme.cs do Windows faz quando a pessoa abre um filme.
/// <list type="bullet">
/// <item>Anuncia <c>filme_aberto</c> pela sala para cada amigo (campos filme, nome, anfitriao, stream, estado, pos, pessoas, quem, quem_ids; "capa" e "fila" ficam de fora no celular),
///   na hora, a cada 5 s e quando o filme começa ou pausa; <c>filme_fechado</c> ao fechar. Quem não vê o anúncio por 20 s o tira do Cinema (CinemaSala).</item>
/// <item><c>filme_quero {sessao, codigo, filme}</c> → uma PeerLink de envio por pedido (tetos: 4 por pessoa, 24 no total; o pedido da própria conta é ignorado) →
///   <c>filme_aceito {sessao, codigo}</c> ou <c>filme_recusado {sessao, motivo}</c>.</item>
/// <item>Mensagens <c>filme_*</c> do túnel vão para a <see cref="Sessao"/> (filme_pronto, filme_pedido, filme_carregado...), com o "id" carimbado pela ligação;
///   <c>filme_baixar</c> vira um <see cref="Envio"/> lendo do Stream que <c>abrir</c> devolve.</item>
/// <item><c>filme_parar {sessao}</c> encerra a ligação e tira a pessoa da lista (Sessao.AmigoSaiu).</item>
/// </list>
/// O que a Sessao manda (estado, permissões) sai copiado por todas as ligações (<see cref="EnviarATodos"/>). As mensagens do túnel e da sala podem chegar em qualquer thread: o parâmetro
/// <c>naThread</c> as leva para a thread da tela (a Sessao não é thread-safe); nos testes é direto.
/// </summary>
public sealed partial class AnfitriaoCinema : IDisposable
{
    public const int MaxPorPessoa = 4, MaxTotal = 24;
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(5);

    /// <summary>Cria a PeerLink de envio (sessão, conta, apelido). No app: <c>PeerLink.CreateAsync(..., iAmSender: true, ..., "filme")</c>.</summary>
    public delegate Task<PeerLink> CriarLink(string sid, string de, string apelido);

    readonly Sessao _sessao;
    readonly string _minhaConta, _meuNome;
    readonly Func<Stream> _abrir;
    readonly string _ext;
    readonly CriarLink _criar;
    readonly Func<string, JsonObject, Task> _enviarSala;
    readonly Func<IEnumerable<string>> _amigos;
    readonly Func<bool> _recuar;
    readonly Action<Action> _naThread;
    readonly Dictionary<string, PeerLink> _links = new();
    readonly Dictionary<Tunnel, Envio> _envios = new();
    readonly Dictionary<string, int> _aceitando = new();
    readonly object _g = new();
    Timer? _tique;
    volatile bool _fechado;
    bool _tocava;
    int _avisouArquivo;

    /// <summary>Alguém pediu o arquivo: o tamanho em bytes, uma vez só (a tela avisa se a rede tem franquia).</summary>
    public event Action<long>? ServindoArquivo;
    /// <summary>A lista de ligações mudou (entrou ou saiu alguém).</summary>
    public event Action? Mudou;

    /// <param name="abrir">Abre o arquivo do filme com seek (um Stream novo a cada chamada; quem recebe o fecha).</param>
    /// <param name="ext">Extensão com ponto, para o amigo salvar com ela (".mkv").</param>
    /// <param name="amigos">Ids das pessoas na sala agora (para quem vai o anúncio).</param>
    public AnfitriaoCinema(Sessao sessao, string minhaConta, string meuNome, Func<Stream> abrir, string ext, CriarLink criar,
        Func<string, JsonObject, Task> enviarSala, Func<IEnumerable<string>> amigos, Func<bool>? recuar = null, Action<Action>? naThread = null)
    {
        if (!sessao.Anfitriao) throw new ArgumentException("a Sessao precisa ser de anfitrião");
        _sessao = sessao; _minhaConta = minhaConta; _meuNome = meuNome; _abrir = abrir; _ext = ext; _criar = criar;
        _enviarSala = enviarSala; _amigos = amigos; _recuar = recuar ?? (() => false); _naThread = naThread ?? (a => a());
    }

    /// <summary>O que a Sessao manda vai para todas as ligações (cópia por ligação: o SendControl carimba um id). Ligue com o parâmetro "enviar" da Sessao.</summary>
    public void EnviarATodos(JsonObject m)
    {
        PeerLink[] ls;
        lock (_g) ls = _links.Values.ToArray();
        foreach (var l in ls) if (m.DeepClone() is JsonObject c) l.Tunnel.SendControl(c);
    }

    /// <summary>Quantas ligações de envio há agora.</summary>
    public int QuantasLigacoes { get { lock (_g) return _links.Count; } }

    public void Iniciar()
    {
        _sessao.Mudou += AoMudar;
        IniciarStream();
        Anunciar();
        _tique = new Timer(_ => { if (!_fechado) Anunciar(); }, null, Intervalo, Intervalo);
    }

    void AoMudar()
    {
        if (_sessao.Tocando != _tocava) { _tocava = _sessao.Tocando; Anunciar(); }
    }

    // ------------------------------------------------------------------ anúncio na sala

    /// <summary>O "filme_aberto" de agora, como o MainForm.Filme.cs (AnuncioSala + ComEstado): o estado do card de quem ainda não entrou.</summary>
    public JsonObject Anuncio()
    {
        var s = _sessao;
        var m = new JsonObject
        {
            ["tipo"] = "filme_aberto", ["filme"] = s.Filme, ["nome"] = s.Nome, ["anfitriao"] = _meuNome, ["stream"] = StreamLigado,
            ["estado"] = s.Tocando ? "rolando" : s.Comecou ? "pausado" : "aguardo",
            ["pos"] = Math.Round(s.Posicao, 1),
        };
        var quem = s.Amigos.Keys.ToList();
        m["pessoas"] = quem.Count;
        m["quem"] = new JsonArray(quem.Select(k => (JsonNode?)s.NomeDe(k)).ToArray());
        m["quem_ids"] = new JsonArray(quem.Select(k => (JsonNode?)k).ToArray());
        return m;
    }

    public void Anunciar()
    {
        if (_fechado) return;
        foreach (var id in _amigos().ToList()) _ = Enviar(id, Anuncio());
    }

    async Task Enviar(string para, JsonObject m)
    {
        try { await _enviarSala(para, m); }
        catch (Exception e) { Log.Info("[filme] a sala não levou \"" + JsonSeguro.Texto(m["tipo"]) + "\": " + e.Message); }
    }

    // ------------------------------------------------------------------ mensagens da sala

    /// <summary>Mensagem filme_* de um amigo pela sala (<paramref name="de"/> já carimbado). Devolve true se tratou.</summary>
    public bool AoReceberSala(string de, string apelido, JsonObject msg)
    {
        string tipo = JsonSeguro.Texto(msg["tipo"]) ?? "";
        string sid = JsonSeguro.Texto(msg["sessao"]) ?? "";
        switch (tipo)
        {
            case "filme_quero":
                _ = AceitarAsync(de, apelido, sid, JsonSeguro.Texto(msg["codigo"]) ?? "", JsonSeguro.Texto(msg["filme"]) ?? "");
                return true;
            case "filme_parar":
                PeerLink? l;
                lock (_g) _links.TryGetValue(sid, out l);
                if (l == null || l.PeerId != de) return true;   // o "sessao" vem do app do amigo: só o outro lado da ligação mexe nela
                Fim(sid);
                _naThread(() => _sessao.AmigoSaiu(de));
                return true;
        }
        return false;
    }

    /// <summary>Um amigo quer entrar (MainForm.Filme.cs, AceitarFilmeAsync): ligação nova só para o filme.</summary>
    async Task AceitarAsync(string de, string apelido, string sid, string codigo, string filme)
    {
        if (_fechado || sid == "" || (de == _minhaConta && _minhaConta != "")) return;   // o pedido da minha própria conta vem de outro aparelho meu: não dá para responder
        lock (_g) if (_links.ContainsKey(sid)) return;
        if (filme != _sessao.Filme) { await Enviar(de, Recusado(sid, $"O filme de {_meuNome} já fechou.")); return; }
        bool cheio;
        lock (_g)
        {
            int dele = _links.Values.Count(l => l.PeerId == de) + _aceitando.GetValueOrDefault(de);
            cheio = dele >= MaxPorPessoa || _links.Count + _aceitando.Values.Sum() >= MaxTotal;
            if (!cheio) _aceitando[de] = _aceitando.GetValueOrDefault(de) + 1;   // o CreateAsync leva segundos: o pedido em andamento já conta
        }
        if (cheio)
        {
            Log.Info($"[filme] {apelido} pediu para entrar, mas já há ligações demais; recusei.");
            await Enviar(de, Recusado(sid, $"{_meuNome} já tem gente demais ligada neste filme. Feche o filme aberto aí e tente de novo."));
            return;
        }
        try
        {
            PeerLink? criado = null;
            try
            {
                criado = await _criar(sid, de, apelido);
                criado.Connect(codigo);
            }
            catch (Exception e)
            {
                criado?.Dispose();
                Log.Info($"[filme] {apelido} quis entrar, mas não deu para preparar a conexão: {e.Message}");
                await Enviar(de, Recusado(sid, $"{_meuNome} não conseguiu preparar a conexão ({e.Message})."));
                return;
            }
            bool entrou;
            lock (_g) { entrou = !_fechado; if (entrou) _links[sid] = criado; }
            if (!entrou) { criado.Dispose(); await Enviar(de, Recusado(sid, $"O filme de {_meuNome} já fechou.")); return; }
            var link = criado;
            link.Control += m => AoReceberTunel(link, m);
            Log.Info($"[filme] {apelido} está entrando no filme.");
            await Enviar(de, new JsonObject { ["tipo"] = "filme_aceito", ["sessao"] = sid, ["codigo"] = link.MyCode });
            Mudou?.Invoke();
        }
        finally
        {
            lock (_g) { if (_aceitando.GetValueOrDefault(de) <= 1) _aceitando.Remove(de); else _aceitando[de]--; }
        }
    }

    static JsonObject Recusado(string sid, string motivo) => new() { ["tipo"] = "filme_recusado", ["sessao"] = sid, ["motivo"] = motivo };

    // ------------------------------------------------------------------ mensagens do túnel

    void AoReceberTunel(PeerLink link, JsonObject m)
    {
        string tipo = JsonSeguro.Texto(m["type"]) ?? "";
        if (!tipo.StartsWith("filme_") || _fechado) return;
        m["id"] = link.PeerId;   // quem fala é quem está na ligação: o "id" que o outro app mandou não vale
        if (tipo == "filme_baixar") { ServirArquivo(link.Tunnel, m); return; }
        if (tipo.StartsWith("filme_stream")) { StreamControle(link, m); return; }   // sem LigarStream o anúncio sai com stream=false e quem pede recebe o aviso de baixar
        _naThread(() =>
        {
            try { _sessao.Receber(m); }
            catch (Exception e) { Log.Info($"[filme] mensagem {tipo} com campo inválido: {e.Message}"); }   // o JSON vem do PC de outra pessoa
        });
    }

    /// <summary>filme_baixar (repetido até chegar a resposta com o manifesto): um Envio por túnel, lendo do Stream.</summary>
    void ServirArquivo(Tunnel t, JsonObject m)
    {
        if (JsonSeguro.Texto(m["filme"]) != _sessao.Filme) return;
        Envio? e;
        long tamanho = 0;
        lock (_g)
        {
            if (!_envios.TryGetValue(t, out e))
            {
                _envios[t] = e = new Envio(t, _abrir, _ext, _sessao.Filme, _sessao.Nome, _recuar);
                Log.Info("[filme] um amigo pediu o arquivo; preparando para mandar.");
                tamanho = Identidade.Tamanho(_sessao.Filme);
            }
        }
        if (tamanho > 0 && Interlocked.Exchange(ref _avisouArquivo, 1) == 0) ServindoArquivo?.Invoke(tamanho);
        e.Responder();
    }

    /// <summary>Quantos envios de arquivo estão ativos e quantos bytes já saíram.</summary>
    public (int Envios, long Bytes) Enviando()
    {
        lock (_g) return (_envios.Count, _envios.Values.Sum(x => x.Emissor?.BytesEnviados ?? 0));
    }

    void Fim(string sid)
    {
        PeerLink? l;
        lock (_g)
        {
            if (!_links.Remove(sid, out l)) return;
            if (_envios.Remove(l.Tunnel, out var e)) Task.Run(e.Dispose);
        }
        Task.Run(() => SairDoStream(sid));
        Log.Info($"[filme] ligação com {l.PeerNick} encerrada.");
        Task.Run(l.Dispose);
        Mudou?.Invoke();
    }

    // ------------------------------------------------------------------ fechar

    /// <summary>
    /// Fecha: filme_fim pelas ligações (3 vezes em 300 ms, o UDP pode perder), filme_fechado a todos da sala e, 1 s depois, as ligações. A Sessao fica com quem chamou.
    /// </summary>
    public async Task FecharAsync()
    {
        if (_fechado) return;
        _fechado = true;
        _tique?.Dispose();
        _sessao.Mudou -= AoMudar;
        var fim = new JsonObject { ["type"] = "filme_fim", ["filme"] = _sessao.Filme };
        for (int i = 0; i < 3; i++) { EnviarATodos(fim); if (i < 2) await Task.Delay(150); }
        foreach (var id in _amigos().ToList()) _ = Enviar(id, new JsonObject { ["tipo"] = "filme_fechado", ["filme"] = _sessao.Filme });
        await Task.Delay(1000);
        Dispose();
    }

    public void Dispose()
    {
        _fechado = true;
        _tique?.Dispose();
        FecharStream();
        List<PeerLink> links; List<Envio> envios;
        lock (_g) { links = _links.Values.ToList(); envios = _envios.Values.ToList(); _links.Clear(); _envios.Clear(); }
        foreach (var e in envios) try { e.Dispose(); } catch { }
        foreach (var l in links) try { l.Dispose(); } catch { }
    }
}
