using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// O filme pela transmissão no celular anfitrião, em cópia direta (spec 028, 5.2): o que o <c>MainForm.FilmeStream.cs</c> do Windows faz com <c>filme_stream_*</c>. Quem assiste manda
/// <c>filme_stream_quero</c> (com a capacidade do aparelho) pela ligação do filme; se o arquivo toca nele, ganha uma <see cref="SaidaFilme"/> e o <c>filme_stream_info</c> com a época; o SRT dele
/// é aberto por <see cref="LigarStream"/> (<c>iniciarSaida</c>), que no app usa o libsrt e nos testes só esvazia a fila.
/// </summary>
public sealed partial class AnfitriaoCinema
{
    /// <summary>O que o espectador lê quando o arquivo não toca no aparelho dele (spec 028, F5).</summary>
    public const string AvisoNaoToca = "Esse arquivo não toca no seu aparelho pela transmissão; baixe o arquivo.";

    /// <summary>Abre o envio do SRT de um espectador (na thread dele); devolve o que o fecha. Quem chama é o <see cref="AnfitriaoCinema"/>, com a ligação do filme dele.</summary>
    public delegate IDisposable IniciarSaida(SaidaFilme saida, PeerLink link);

    EnvioFilmeCopia? _envio;
    string? _codecAudio;
    IniciarSaida? _iniciarSaida;
    Timer? _seguir;
    readonly Dictionary<string, IDisposable> _saidasSrt = new();
    readonly HashSet<string> _streamSids = new();
    readonly Dictionary<string, Capacidade?> _capacidades = new();   // por ligação; nulo = app antigo (toca tudo)

    /// <summary>Há transmissão do filme (o anúncio sai com <c>stream=true</c>)? Só para h264 e hevc, que o <c>PlayerStream</c> e o mpv decodificam do mpegts.</summary>
    public bool StreamLigado => _envio != null;

    /// <summary>Liga a transmissão do filme em cópia direta. Chamar antes de <see cref="Iniciar"/>. <paramref name="codecAudio"/>: o codec da faixa que o envio manda (nome do ffprobe), para a checagem de capacidade.</summary>
    public void LigarStream(EnvioFilmeCopia envio, string? codecAudio, IniciarSaida iniciarSaida)
    {
        if (!PodeTransmitir(envio.Info.Video)) throw new ArgumentException($"codec de vídeo \"{envio.Info.Video}\" não vai pela transmissão; esperado h264 ou hevc");
        _envio = envio; _codecAudio = codecAudio; _iniciarSaida = iniciarSaida;
        envio.Mudou += AoMudarEpoca;
        envio.Falhou += texto => AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = texto });
    }

    /// <summary>A cópia direta só serve para h264 e hevc (AV1 e o resto pedem recompressão, que o celular só faz na F5b).</summary>
    public static bool PodeTransmitir(string codecVideo) => codecVideo is "h264" or "hevc";

    void IniciarStream()
    {
        if (_envio == null) return;
        _sessao.Mudou += SeguirSessao;
        _seguir = new Timer(_ => _naThread(SeguirSessao), null, 400, 400);   // a Sessão não é thread-safe: lê na thread dela
    }

    /// <summary>A Sessão é de quem manda no tempo: o envio só a segue, a cada mudança e a cada 400 ms (como o Windows). Chamar na thread da Sessão.</summary>
    void SeguirSessao()
    {
        if (_fechado || _envio is not { } e) return;
        double pos = _sessao.Posicao; bool tocando = _sessao.Tocando;
        try { e.Seguir(pos, tocando); }
        catch (Exception ex) { Log.Info("[filme] o envio não seguiu a Sessão: " + ex.Message); }
    }

    void AoMudarEpoca(int epoca, double pos, bool tocando)
    {
        AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_epoca", ["epoca"] = epoca, ["pos"] = pos, ["tocando"] = tocando });
    }

    void AvisarEspectadores(JsonObject m)
    {
        string[] sids;
        lock (_g) sids = _streamSids.ToArray();
        foreach (var sid in sids)
        {
            PeerLink? l;
            lock (_g) _links.TryGetValue(sid, out l);
            if (l != null && m.DeepClone() is JsonObject c) l.Tunnel.SendControl(c);
        }
    }

    /// <summary>filme_stream_* que chegaram pela ligação de um espectador (o "id" já está carimbado).</summary>
    void StreamControle(PeerLink link, JsonObject m)
    {
        if (_envio is not { } envio) { RecusarStream(link, "O filme de " + _meuNome + " não está sendo transmitido; baixe o arquivo."); return; }
        switch (JsonSeguro.Texto(m["type"]))
        {
            case "filme_stream_quero": QueroStream(link, m, envio); break;
            case "filme_stream_recarregar":
                Log.Info($"[filme] {link.PeerNick} não tem o ponto do filme guardado: reabrindo o envio.");
                envio.Recarregar(link.PeerNick);
                break;
            case "filme_stream_carregado":   // quem assiste pela transmissão já está no ponto do pulo: vira o filme_carregado da Sessão, com o id da ligação
                ReceberNaSessao(new JsonObject { ["type"] = "filme_carregado", ["filme"] = m["filme"]?.DeepClone(), ["seq"] = m["seq"]?.DeepClone(), ["nome"] = link.PeerNick, ["id"] = link.PeerId });
                break;
            case "filme_stream_cmd": ComandoDeEspectador(link, m); break;
        }
    }

    static void RecusarStream(PeerLink link, string texto) =>
        link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = texto });

    void QueroStream(PeerLink link, JsonObject m, EnvioFilmeCopia envio)
    {
        var capacidade = Capacidade.De(m["capacidade"]);   // nulo = app antigo, que toca tudo como o Windows
        if (!Capacidade.CopiaServe(capacidade, envio.Info, _codecAudio))
        {
            Log.Info($"[filme] {link.PeerNick} pediu a transmissão, mas o aparelho ({capacidade?.Plataforma}) não toca {envio.Info.Resumo}; mandei baixar o arquivo.");
            RecusarStream(link, AvisoNaoToca);
            return;
        }
        var saida = envio.Adicionar(link.SessionId, link.PeerNick);
        if (saida == null) return;
        bool novo;
        lock (_g) { novo = _streamSids.Add(link.SessionId); _capacidades[link.SessionId] = capacidade; }
        if (novo) IniciarSrt(saida, link);
        _naThread(() =>
        {
            SeguirSessao();   // abre (ou reabre) a época já, para a resposta levar o número dela
            link.Tunnel.SendControl(InfoDaTransmissao(envio));
        });
        Log.Info($"[filme] {link.PeerNick} assiste pela transmissão (cópia direta, {envio.Info.Resumo}).");
    }

    void IniciarSrt(SaidaFilme saida, PeerLink link)
    {
        var fechavel = _iniciarSaida!(saida, link);
        lock (_g) _saidasSrt[link.SessionId] = fechavel;
    }

    /// <summary>A resposta ao <c>filme_stream_quero</c>: o título, a duração, a época e a posição, e o estado da Sessão para quem entra no meio do filme.</summary>
    JsonObject InfoDaTransmissao(EnvioFilmeCopia envio) => new()
    {
        ["type"] = "filme_stream_info", ["titulo"] = _sessao.Nome, ["duracao"] = envio.Info.DuracaoS,
        ["perfil"] = $"Copia · {envio.Info.Resumo}", ["epoca"] = envio.Epoca, ["pos"] = envio.Posicao, ["tocando"] = envio.Tocando,
        ["estado"] = _sessao.EstadoAtual(),
    };

    void ReceberNaSessao(JsonObject m) => _naThread(() =>
    {
        try { _sessao.Receber(m); }
        catch (Exception e) { Log.Info($"[filme] mensagem {JsonSeguro.Texto(m["type"])} com campo inválido: {e.Message}"); }
    });

    /// <summary>Quem assiste pediu tocar, pausar ou pular: vale se o anfitrião deixou todos controlarem; senão, o motivo volta.</summary>
    void ComandoDeEspectador(PeerLink link, JsonObject m)
    {
        double? pos = JsonSeguro.Numero(m["pos"]);
        string cmd = JsonSeguro.Texto(m["cmd"]) ?? "";
        if (!_sessao.TodosControlam)
        {
            Log.Info($"[filme] {link.PeerNick} pediu {cmd} pela transmissão: recusado (só o anfitrião controla).");
            RecusarStream(link, $"Só {_meuNome} controla o filme.");
            return;
        }
        Log.Info($"[filme] {link.PeerNick} pediu {cmd} pela transmissão: aceito.");
        _naThread(() =>
        {
            switch (cmd)
            {
                case "tocar": _sessao.Tocar(); break;
                case "pausar": _sessao.Pausar(); break;
                case "ir" when pos != null: _sessao.Ir(pos.Value); break;
            }
        });
    }

    /// <summary>
    /// O anfitrião trocou a faixa de áudio (spec 028, 5.2): quem não toca o codec da faixa nova recebe o aviso de baixar o arquivo e sai da transmissão; o envio abre uma época com a faixa
    /// nova para os outros, que são avisados. Chamar na thread da Sessão.
    /// </summary>
    public void TrocarAudio(FonteInfo info, Func<IFonteTs> criarFonte, string? codecAudio)
    {
        if (_envio is not { } envio) return;
        _codecAudio = codecAudio;
        string[] sids; lock (_g) sids = _streamSids.ToArray();
        foreach (var sid in sids)
        {
            Capacidade? cap; PeerLink? link;
            lock (_g) { _capacidades.TryGetValue(sid, out cap); _links.TryGetValue(sid, out link); }
            if (Capacidade.CopiaServe(cap, info, codecAudio)) continue;
            Log.Info($"[filme] a faixa nova ({codecAudio}) não toca no aparelho de {link?.PeerNick}; saiu da transmissão.");
            if (link != null) RecusarStream(link, AvisoNaoToca);
            SairDoStream(sid);
        }
        envio.TrocarFonte(info, criarFonte, "faixa de áudio");
        AvisarEspectadores(new JsonObject { ["type"] = "filme_stream_aviso", ["texto"] = $"{_meuNome} trocou a faixa de áudio." });
    }

    /// <summary>O espectador saiu (ou a ligação caiu): tira a saída dele do envio e fecha o SRT.</summary>
    void SairDoStream(string sid)
    {
        IDisposable? srt;
        lock (_g) { _streamSids.Remove(sid); _capacidades.Remove(sid); _saidasSrt.Remove(sid, out srt); }
        _envio?.Remover(sid);
        try { srt?.Dispose(); } catch (Exception e) { Log.Info("[filme] o SRT do espectador não fechou direito: " + e.Message); }
    }

    void FecharStream()
    {
        _seguir?.Dispose();
        _sessao.Mudou -= SeguirSessao;
        string[] todos;
        lock (_g) todos = _streamSids.ToArray();
        foreach (var sid in todos) SairDoStream(sid);
        _envio?.Dispose();
    }
}
