using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Filme;

namespace Streemu.Arquivos;

/// <summary>
/// O envio de arquivo pela sala do grupo (spec 020 no PC, spec 030 no Android): oferecer, aceitar, recusar, cancelar, pedir de novo, expirar.
/// É o <c>MainForm.Arquivos.cs</c> do app Windows, sem nada de tela nem do Windows: o aparelho entra por <see cref="IPortaDaSala"/>, <see cref="IFabricaDeLigacao"/> e <see cref="IArquivosDoAparelho"/>.
/// A ligação e a transferência ficam em FluxoDeArquivos.Ligacao.cs; o fim, o histórico e os prazos em FluxoDeArquivos.Fim.cs.
/// <para>
/// <b>Uma fila só.</b> Tudo o que muda o estado roda numa fila exclusiva (<see cref="Rodar(Action)"/>), então o código não precisa de trava e os
/// <c>await</c> voltam para ela. Quem chama pode estar em qualquer thread; a tela só lê (<see cref="Pedidos"/>) e é avisada por <see cref="Mudou"/>.
/// </para>
/// Ex.: <c>var fluxo = new FluxoDeArquivos(sala, ligacoes, aparelho, historico); sala.Chegou += fluxo.AoReceber; await fluxo.OferecerAsync(amigoId, escolhidos);</c>
/// </summary>
public sealed partial class FluxoDeArquivos
{
    static readonly TimeSpan GuardarTerminado = TimeSpan.FromMinutes(2);

    readonly IPortaDaSala _sala;
    readonly IFabricaDeLigacao _ligacoes;
    readonly IArquivosDoAparelho _aparelho;
    readonly Func<DateTime> _agora;
    readonly Action<string> _log;
    readonly Action<string, JsonObject> _telemetria;
    readonly GuardaDeCurso? _guarda;
    readonly Dictionary<string, PedidoEmCurso> _pedidos = new();
    readonly object _trava = new();
    readonly TaskScheduler _fila = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

    public FluxoDeArquivos(IPortaDaSala sala, IFabricaDeLigacao ligacoes, IArquivosDoAparelho aparelho, Historico historico,
        Func<DateTime>? agora = null, Action<string>? log = null, Action<string, JsonObject>? telemetria = null, GuardaDeCurso? guarda = null)
    {
        _sala = sala; _ligacoes = ligacoes; _aparelho = aparelho; Historico = historico; _guarda = guarda;
        _agora = agora ?? (() => DateTime.Now);
        _log = log ?? (_ => { });
        _telemetria = telemetria ?? ((_, _) => { });
    }

    public Historico Historico { get; }

    /// <summary>Algum pedido mudou de fase ou de progresso. Vem da fila do fluxo: a tela deve passar para a thread dela e limitar o redesenho.</summary>
    public event Action? Mudou;

    /// <summary>Quem manda cede banda quando isto diz que há live ou jogo em andamento (no PC, o <c>_sender.Running</c>). Pelo relay, o Cinema segue a mesma regra.</summary>
    public Func<bool> LiveEmAndamento { get; set; } = () => false;

    /// <summary>
    /// O aparelho está numa rede de dados móveis (conexão medida)? Antes de BAIXAR mais de 100 MB nela, a pessoa confirma o gasto do plano
    /// (spec 030, D5). Em Wi-Fi não pergunta. O padrão (falso) é o do PC, que nunca pergunta.
    /// </summary>
    public Func<bool> RedeMedida { get; set; } = () => false;

    /// <summary>A pessoa marcou "não perguntar de novo" no aviso de dados móveis. Quem sabe é o aparelho (uma preferência).</summary>
    public Func<bool> NaoPerguntarDados { get; set; } = () => false;

    /// <summary>Chamado quando a pessoa responde ao aviso de dados móveis marcando "não perguntar de novo"; o aparelho guarda a preferência.</summary>
    public Action? AoEscolherNaoPerguntarDados { get; set; }

    /// <summary>Teste: trata a ligação como relay de reserva, para exercitar a D1 no loopback, que é sempre direto.</summary>
    public bool ForcarRelay { get; set; }

    /// <summary>Um retrato dos pedidos, do mais novo para o mais velho (a tela lê daqui).</summary>
    public IReadOnlyList<PedidoEmCurso> Pedidos { get { lock (_trava) return _pedidos.Values.OrderByDescending(p => p.Criado).ToList(); } }

    void AvisarMudanca() => Mudou?.Invoke();

    /// <summary>Espera tudo o que já está na fila terminar (os testes usam depois de <see cref="Tick"/> e de mensagens da sala, que rodam sem devolver tarefa).</summary>
    public Task AguardarFilaAsync() => Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _fila);

    // ------------------------------------------------------------------ a fila

    Task Rodar(Func<Task> f) => Task.Factory.StartNew(f, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _fila).Unwrap();

    Task<T> Rodar<T>(Func<Task<T>> f) => Task.Factory.StartNew(f, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _fila).Unwrap();

    void Rodar(Action a) => Task.Factory.StartNew(a, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _fila);

    PedidoEmCurso? Achar(string pedido) { lock (_trava) return _pedidos.GetValueOrDefault(pedido); }

    void Guardar(PedidoEmCurso p) { lock (_trava) _pedidos[p.Pedido] = p; }

    void Esquecer(string pedido) { lock (_trava) _pedidos.Remove(pedido); }

    static string NovoId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    static string Tipo(JsonObject m) => m["tipo"] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    // ------------------------------------------------------------------ sala

    /// <summary>Uma mensagem <c>arquivo_*</c> da sala. Pode ser chamada de qualquer thread.</summary>
    public void AoReceber(string de, string apelido, JsonObject msg) => Rodar(() => Tratar(de, apelido, msg));

    void Tratar(string de, string apelido, JsonObject msg)
    {
        string tipo = Tipo(msg);
        if (tipo == MensagemArquivo.Oferta) { OfertaChegou(de, apelido, msg); return; }
        if (tipo == MensagemArquivo.DeNovo) { DeNovoChegou(de, apelido, msg); return; }
        if (MensagemArquivo.PedidoDe(msg) is not string id || Achar(id) is not { } p || p.AmigoId != de) return;
        RespostaDoPedido(p, tipo, msg);
        AvisarMudanca();
    }

    void RespostaDoPedido(PedidoEmCurso p, string tipo, JsonObject msg)
    {
        switch (tipo)
        {
            case MensagemArquivo.Visto: p.Visto = true; break;
            case MensagemArquivo.Aceito when p.Sentido == Sentido.Enviado: _ = Rodar(() => AceitoPeloAmigoAsync(p, msg)); break;
            case MensagemArquivo.Ligar when p.Sentido == Sentido.Recebido: LigarAoRemetente(p, msg); break;
            case MensagemArquivo.Recusado: Terminar(p, FaseArquivo.Recusado, MensagemArquivo.MotivoDe(msg) ?? $"{p.Apelido} recusou."); break;
            case MensagemArquivo.Cancelado: Terminar(p, FaseArquivo.Cancelado, MensagemArquivo.MotivoDe(msg) ?? $"{p.Apelido} cancelou."); break;
            case MensagemArquivo.Concluido when p.Sentido == Sentido.Enviado: Terminar(p, FaseArquivo.Conferido, null); break;
        }
    }

    /// <summary>Quem recebe: responde "visto" na hora (é o que diz a quem manda que esta versão entende o pedido) e guarda a oferta.</summary>
    void OfertaChegou(string de, string apelido, JsonObject msg)
    {
        if (MensagemArquivo.LerOferta(msg) is not OfertaDeArquivos oferta) { _log($"[arquivo] {apelido} mandou uma oferta fora do formato; ignorei."); return; }
        _ = _sala.EnviarAsync(de, MensagemArquivo.Simples(MensagemArquivo.Visto, oferta.Pedido));
        if (Achar(oferta.Pedido) is { } meu) { OfertaDoMeuDeNovo(meu, de, oferta); return; }
        Guardar(new PedidoEmCurso(Sentido.Recebido, de, apelido, oferta, _agora()));
        _log($"[arquivo] {apelido} quer mandar {oferta.Arquivos.Count} arquivo(s), {oferta.Total / 1e6:N1} MB.");
        AvisarMudanca();
    }

    /// <summary>A oferta é a resposta a um "pedir de novo" meu: a pessoa já disse que quer, então aceita sem perguntar.</summary>
    void OfertaDoMeuDeNovo(PedidoEmCurso meu, string de, OfertaDeArquivos oferta)
    {
        if (meu.Sentido != Sentido.Recebido || meu.AmigoId != de || meu.Fase != FaseArquivo.AguardandoDeNovo) return;
        meu.Oferta = oferta;
        meu.Fase = FaseArquivo.Esperando;
        _ = Rodar(() => AceitarNaFilaAsync(meu.Pedido));
    }

    /// <summary>Quem mandou: o amigo pede de novo pelo histórico. Só vale para um arquivo que EU mandei para ELE (D6).</summary>
    void DeNovoChegou(string de, string apelido, JsonObject msg)
    {
        if (MensagemArquivo.LerDeNovo(msg) is not { } deNovo || Achar(deNovo.Pedido) != null) return;
        var (pedido, arquivo) = deNovo;
        _ = _sala.EnviarAsync(de, MensagemArquivo.Simples(MensagemArquivo.Visto, pedido));
        var p = new PedidoEmCurso(Sentido.Enviado, de, apelido, new OfertaDeArquivos(pedido, [arquivo]), _agora()) { Fase = FaseArquivo.PedidoDeNovo, Visto = true };
        if (ReferenciaEnviada(de, arquivo.Identidade) is string referencia) p.Caminhos[arquivo.Identidade] = referencia;
        Guardar(p);
        _log($"[arquivo] {apelido} pediu de novo um arquivo de {arquivo.Tamanho / 1e6:N1} MB.");
        AvisarMudanca();
    }

    string? ReferenciaEnviada(string amigoId, string identidade) =>
        Historico.Itens.FirstOrDefault(i => i.Sentido == Sentido.Enviado && i.AmigoId == amigoId && i.Identidade == identidade)?.Caminho;

    // ------------------------------------------------------------------ ações (a tela chama)

    /// <summary>
    /// Quem manda: oferece os arquivos escolhidos a um amigo da sala. Devolve o motivo, já para a tela, se não deu; nulo se a oferta saiu.
    /// Ex.: <c>string? erro = await fluxo.OferecerAsync(amigo.Id, escolhidos);</c>
    /// </summary>
    public Task<string?> OferecerAsync(string amigoId, IReadOnlyList<ArquivoParaEnviar> arquivos, string? pedido = null) =>
        Rodar(() => OferecerNaFilaAsync(amigoId, arquivos, pedido));

    async Task<string?> OferecerNaFilaAsync(string amigoId, IReadOnlyList<ArquivoParaEnviar> arquivos, string? pedido)
    {
        if (ErroDeOferta(amigoId, arquivos) is string erro) return erro;
        var oferta = new OfertaDeArquivos(pedido ?? NovoId(), arquivos.Select(a => new ArquivoOferecido(a.Nome, a.Tamanho, a.Identidade)).ToList());
        var p = new PedidoEmCurso(Sentido.Enviado, amigoId, NomeDoAmigo(amigoId), oferta, _agora());
        foreach (var a in arquivos) p.Caminhos[a.Identidade] = a.Referencia;
        Guardar(p);
        await _sala.EnviarAsync(amigoId, MensagemArquivo.CriarOferta(oferta));
        _log($"[arquivo] ofereci {oferta.Arquivos.Count} arquivo(s), {oferta.Total / 1e6:N1} MB, a {p.Apelido}.");
        AvisarMudanca();
        return null;
    }

    string? ErroDeOferta(string amigoId, IReadOnlyList<ArquivoParaEnviar> arquivos)
    {
        if (!_sala.AmigoNaSala(amigoId)) return "Esse amigo não está na sala agora.";
        if (arquivos.Count == 0 || arquivos.Count > MensagemArquivo.MaxArquivos) return $"Escolha de 1 a {MensagemArquivo.MaxArquivos} arquivos.";
        if (arquivos.FirstOrDefault(a => a.Tamanho <= 0) is { } vazio) return $"\"{vazio.Nome}\" está vazio.";
        if (arquivos.Any(a => a.Nome.Length > MensagemArquivo.MaxNome)) return "Um dos nomes de arquivo é longo demais. Encurte e tente de novo.";
        return null;
    }

    string NomeDoAmigo(string amigoId) => _nomes.GetValueOrDefault(amigoId) ?? "amigo";

    readonly Dictionary<string, string> _nomes = new();

    /// <summary>A tela diz o nome do amigo escolhido (a sala dele já é conhecida pela tela); sem isso a linha do pedido mostraria "amigo".</summary>
    public void LembrarNome(string amigoId, string nome) { lock (_trava) _nomes[amigoId] = nome; }

    /// <summary>Quem recebe: aceita. Confere o espaço antes de qualquer coisa (critério 8) e abre a ligação; o resto vem com o "ligar".</summary>
    public Task<string?> AceitarAsync(string pedido) => Rodar(() => AceitarNaFilaAsync(pedido));

    async Task<string?> AceitarNaFilaAsync(string pedido)
    {
        if (Achar(pedido) is not { Sentido: Sentido.Recebido, Fase: FaseArquivo.Esperando } p) return "Esse pedido não está mais esperando.";
        if (await Task.Run(() => FaltaEspacoPara(p)) is string semEspaco) return semEspaco;
        p.Fase = FaseArquivo.Conectando;
        _guarda?.Gravar(p);
        AvisarMudanca();
        if (await AbrirLigacaoAsync(p, souEmissor: false) is string erro) { EncerrarEAvisar(p, FaseArquivo.Falhou, erro); return erro; }
        if (p.Terminado) { FecharLigacao(p); return null; }
        await _sala.EnviarAsync(p.AmigoId, MensagemArquivo.ComCodigo(MensagemArquivo.Aceito, p.Pedido, p.Link!.MyCode));
        return null;
    }

    string? FaltaEspacoPara(PedidoEmCurso p)
    {
        long jaBaixado = p.Oferta.Arquivos.Sum(a => Download.JaBaixado(_aparelho.PastaDeTrabalho, a.Identidade) ?? 0);
        return _aparelho.FaltaEspaco(p.Oferta.Total, jaBaixado);
    }

    public void Recusar(string pedido) => Rodar(() => EncerrarPorMim(pedido, MensagemArquivo.Recusado, FaseArquivo.Recusado, $"{_sala.MeuNome} recusou.", "Você recusou."));

    public void Cancelar(string pedido) => Rodar(() => EncerrarPorMim(pedido, MensagemArquivo.Cancelado, FaseArquivo.Cancelado, $"{_sala.MeuNome} cancelou.", "Você cancelou."));

    void EncerrarPorMim(string pedido, string tipo, FaseArquivo fase, string paraOAmigo, string aqui)
    {
        if (Achar(pedido) is not { } p || p.Terminado) return;
        _ = _sala.EnviarAsync(p.AmigoId, MensagemArquivo.Simples(tipo, pedido, paraOAmigo));
        Terminar(p, fase, aqui);
    }

    /// <summary>Quem recebe, pelo histórico: pede o mesmo arquivo de novo (D6). A oferta que voltar é aceita sozinha.</summary>
    public Task<string?> PedirDeNovoAsync(ItemHistorico item) => Rodar(async () =>
    {
        if (!_sala.AmigoNaSala(item.AmigoId)) return $"{item.Apelido} não está na sala agora.";
        var arquivo = new ArquivoOferecido(item.Nome, item.Tamanho, item.Identidade);
        var p = new PedidoEmCurso(Sentido.Recebido, item.AmigoId, item.Apelido, new OfertaDeArquivos(NovoId(), [arquivo]), _agora()) { Fase = FaseArquivo.AguardandoDeNovo };
        Guardar(p);
        await _sala.EnviarAsync(item.AmigoId, MensagemArquivo.CriarDeNovo(p.Pedido, arquivo));
        AvisarMudanca();
        return (string?)null;
    });

    /// <summary>Quem mandou: atende o "pedir de novo", se o arquivo ainda está no mesmo lugar e igual; senão, recusa com o motivo.</summary>
    public Task<string?> MandarDeNovoAsync(string pedido) => Rodar(async () =>
    {
        if (Achar(pedido) is not { Fase: FaseArquivo.PedidoDeNovo } p) return "Esse pedido não está mais esperando.";
        var arquivo = p.Oferta.Arquivos[0];
        string? referencia = p.Caminhos.GetValueOrDefault(arquivo.Identidade);
        if (referencia == null || await Task.Run(() => _aparelho.IdentidadeAtual(referencia)) != arquivo.Identidade)
        {
            EncerrarPorMim(pedido, MensagemArquivo.Recusado, FaseArquivo.Recusado, $"O arquivo mudou ou não está mais no aparelho de {_sala.MeuNome}.", "O arquivo mudou ou saiu do lugar.");
            return "O arquivo mudou ou saiu do lugar desde que você mandou.";
        }
        Esquecer(pedido);
        var de = new ArquivoParaEnviar(referencia, arquivo.Nome, arquivo.Tamanho, arquivo.Identidade);
        string? erro = await OferecerNaFilaAsync(p.AmigoId, [de], pedido);
        // sem isso, quem pediu ficaria 10 min esperando uma oferta que não vem
        if (erro != null) _ = _sala.EnviarAsync(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Recusado, pedido, $"{_sala.MeuNome} não conseguiu mandar de novo agora."));
        return erro;
    });
}
