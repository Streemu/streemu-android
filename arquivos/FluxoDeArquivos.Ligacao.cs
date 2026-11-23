using System.Text.Json.Nodes;
using Streemu.Filme;
using Streemu.Net;

namespace Streemu.Arquivos;

/// <summary>
/// A ligação de cada pedido e a transferência, com o transporte do Cinema (<c>Envio</c> e <c>Download</c>, as mesmas mensagens
/// filme_baixar/filme_arquivo, mas numa ligação só do pedido). Uma ligação por pedido, arquivos um depois do outro: o túnel tem um
/// tratador só para os pacotes de arquivo. É o <c>MainForm.ArquivosLigacao.cs</c> do app Windows. A sala e as ações: FluxoDeArquivos.cs.
/// </summary>
public sealed partial class FluxoDeArquivos
{
    static readonly TimeSpan EsperaConectar = TimeSpan.FromSeconds(25), DesistirDeConectar = TimeSpan.FromSeconds(40);
    const int SegundosParaDarComoCaida = 35;

    /// <summary>
    /// Cria a ligação do pedido. Quem manda passa a atender os filme_baixar dela, só com os arquivos que ofereceu.
    /// Devolve o motivo, se não deu.
    /// </summary>
    async Task<string?> AbrirLigacaoAsync(PedidoEmCurso p, bool souEmissor)
    {
        try { p.Link = await _ligacoes.CriarAsync("arq" + p.Pedido, p.AmigoId, p.Apelido, souEmissor); }
        catch (Exception e) { return $"Não deu para preparar a conexão com {p.Apelido}: {e.Message}"; }
        if (souEmissor) p.Link.Control += m => { if (m["type"]?.GetValue<string>() == "filme_baixar") Rodar(() => ServirArquivo(p, m)); };
        p.Relogio.Start();
        return null;
    }

    /// <summary>Quem manda: o amigo aceitou com o código dele. Liga e responde com o meu ("ligar").</summary>
    async Task AceitoPeloAmigoAsync(PedidoEmCurso p, JsonObject msg)
    {
        if (p.Fase != FaseArquivo.Esperando || MensagemArquivo.CodigoDe(msg) is not string codigo) return;
        p.Fase = FaseArquivo.Conectando;
        _guarda?.Gravar(p);
        if (await AbrirLigacaoAsync(p, souEmissor: true) is string erro) { EncerrarEAvisar(p, FaseArquivo.Falhou, erro); return; }
        if (p.Terminado) { FecharLigacao(p); return; }
        try { p.Link!.Connect(codigo); }
        catch (FormatException e) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"{p.Apelido} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"); return; }
        await _sala.EnviarAsync(p.AmigoId, MensagemArquivo.ComCodigo(MensagemArquivo.Ligar, p.Pedido, p.Link.MyCode));
        _log($"[arquivo] {p.Apelido} aceitou; conectando.");
    }

    /// <summary>Quem recebe: chegou o código de quem manda. Liga e começa a receber.</summary>
    void LigarAoRemetente(PedidoEmCurso p, JsonObject msg)
    {
        if (p.Fase != FaseArquivo.Conectando || p.Link == null || MensagemArquivo.CodigoDe(msg) is not string codigo) return;
        try { p.Link.Connect(codigo); }
        catch (FormatException e) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"{p.Apelido} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"); return; }
        _ = Rodar(() => ReceberArquivosAsync(p));
    }

    // ------------------------------------------------------------------ quem manda

    /// <summary>Um filme_baixar do amigo: serve o arquivo pedido, se ele está nesta oferta (nunca outro arquivo do aparelho).</summary>
    void ServirArquivo(PedidoEmCurso p, JsonObject m)
    {
        string id = m["filme"]?.GetValue<string>() ?? "";
        if (p.Terminado || p.Link == null || !p.Caminhos.TryGetValue(id, out var referencia)) return;
        if (p.EnvioDe != id) TrocarEnvio(p, id, referencia);
        p.Fase = FaseArquivo.Transferindo;
        p.Envio!.Responder();
    }

    /// <summary>Próximo arquivo: o envio anterior sai antes (o túnel só tem um tratador de pacotes de arquivo).</summary>
    void TrocarEnvio(PedidoEmCurso p, string id, string referencia)
    {
        p.Envio?.Dispose();
        var arquivo = p.Oferta.Arquivos.First(a => a.Identidade == id);
        string nome = Path.GetFileNameWithoutExtension(arquivo.Nome), ext = Path.GetExtension(arquivo.Nome);
        p.Envio = new Envio(p.Link!.Tunnel, () => _aparelho.AbrirParaEnvio(referencia), ext, id, nome,
            () => LiveEmAndamento(), priorizarLigacaoDireta: !ForcarRelay);
        p.EnvioDe = id;
        p.BytesAntes = 0;
        p.ArquivoAtual = Math.Max(0, p.Oferta.Arquivos.ToList().FindIndex(a => a.Identidade == id));
    }

    /// <summary>A cada 1 s: o que já saiu do arquivo atual e a velocidade (a tela de quem manda não vê o progresso do outro lado).</summary>
    static void AtualizarProgressoDoEnvio(PedidoEmCurso p)
    {
        if (p.Fase != FaseArquivo.Transferindo || p.Envio?.Emissor is not { } e) return;
        long bytes = e.BytesEnviados, tamanho = p.Oferta.Arquivos[p.ArquivoAtual].Tamanho;
        p.Progresso = new Progresso("enviando", Math.Min(bytes, tamanho), tamanho, Math.Max(0, bytes - p.BytesAntes));
        p.BytesAntes = bytes;
    }

    /// <summary>
    /// Quem manda não fica sabendo se o app do outro fechou: o Envio só para de mandar. Conectando: desiste depois de 40 s sem
    /// ligação. Transferindo: dá como caída depois de 35 s seguidos sem ligação (quem recebe desiste aos 30 s, Download.cs).
    /// </summary>
    static bool LigacaoCaiu(PedidoEmCurso p)
    {
        if (p.Fase == FaseArquivo.Conectando) return p.Relogio.Elapsed > DesistirDeConectar && p.Link is not { Connected: true };
        if (p.Sentido != Sentido.Enviado || p.Fase != FaseArquivo.Transferindo) return false;
        p.SegundosSemLigacao = p.Link is { Connected: true } ? 0 : p.SegundosSemLigacao + 1;
        return p.SegundosSemLigacao >= SegundosParaDarComoCaida;
    }

    // ------------------------------------------------------------------ quem recebe

    async Task ReceberArquivosAsync(PedidoEmCurso p)
    {
        if (!await EsperarLigacaoAsync(p)) { EncerrarEAvisar(p, FaseArquivo.Falhou, $"Não consegui conectar com {p.Apelido} em 25 s. Tente de novo."); return; }
        if (!await LiberadoPeloRelayAsync(p)) return;
        if (!await LiberadoPeloPlanoDeDadosAsync(p)) return;
        p.Fase = FaseArquivo.Transferindo;
        AvisarMudanca();
        try
        {
            for (int i = 0; i < p.Oferta.Arquivos.Count; i++) await ReceberUmAsync(p, i);
        }
        catch (OperationCanceledException) { return; } // quem cancelou já encerrou o pedido
        catch (Exception e) { FalhaAoReceber(p, e); return; }
        _ = _sala.EnviarAsync(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Concluido, p.Pedido));
        Terminar(p, FaseArquivo.Conferido, p.Motivo);
    }

    /// <summary>O motivo daqui pode ter a pasta deste aparelho (sem espaço, nome recusado): ao amigo vai só o aviso.</summary>
    void FalhaAoReceber(PedidoEmCurso p, Exception e)
    {
        var fase = p.Link is { Connected: true } ? FaseArquivo.Falhou : FaseArquivo.Parou;
        EncerrarEAvisar(p, fase, e.Message, $"{_sala.MeuNome} não conseguiu receber. O que já veio fica guardado com {_sala.MeuNome}.");
    }

    async Task<bool> EsperarLigacaoAsync(PedidoEmCurso p)
    {
        var ate = _agora() + EsperaConectar;
        while (_agora() < ate && !p.Terminado)
        {
            if (p.Link is { Connected: true }) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>D1: pelo relay, até 100 MB segue; até 2 GB, a pessoa confirma o gasto da cota (<see cref="ResponderRelay"/>); acima, bloqueia.</summary>
    async Task<bool> LiberadoPeloRelayAsync(PedidoEmCurso p)
    {
        p.PeloRelay = ForcarRelay || p.Link?.Tunnel.Target is { Kind: not CandKind.Direct };
        var decisao = RegraRelay.Decidir(p.PeloRelay, p.Oferta.Total);
        if (decisao == DecisaoRelay.Livre) return true;
        if (decisao == DecisaoRelay.Bloquear)
        {
            EncerrarEAvisar(p, FaseArquivo.Bloqueado, $"A conexão com {p.Apelido} só funciona pelo relay de reserva, e lá o limite é {RegraRelay.Teto / 1e9:N0} GB por envio ({p.Oferta.Total / 1e9:N1} GB aqui). Tente de outra rede ou divida os arquivos.");
            return false;
        }
        p.Fase = FaseArquivo.ConfirmandoRelay;
        p.PerguntouEm = _agora();
        p.RespostaRelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _log($"[arquivo] a ligação com {p.Apelido} passa pelo relay: esperando confirmar {p.Oferta.Total / 1e6:N0} MB da cota.");
        AvisarMudanca();
        bool seguir = await p.RespostaRelay.Task;
        if (!seguir) EncerrarEAvisar(p, FaseArquivo.Cancelado, $"{_sala.MeuNome} preferiu não usar a cota do relay de reserva.");
        return seguir;
    }

    /// <summary>A resposta da pessoa ao aviso de cota do relay (true = receber mesmo assim).</summary>
    public void ResponderRelay(string pedido, bool seguir) =>
        Rodar(() => { if (Achar(pedido) is { Fase: FaseArquivo.ConfirmandoRelay } p) p.RespostaRelay?.TrySetResult(seguir); });

    /// <summary>
    /// D5: em rede de dados móveis, mais de 100 MB pergunta antes (<see cref="ResponderDados"/>). Não bloqueia: a pessoa decide, e pode marcar
    /// "não perguntar de novo". É o limite do relay sem aviso, o mesmo número nas duas regras.
    /// </summary>
    async Task<bool> LiberadoPeloPlanoDeDadosAsync(PedidoEmCurso p)
    {
        if (p.Oferta.Total <= RegraRelay.AteSemAviso || !RedeMedida() || NaoPerguntarDados()) return true;
        p.Fase = FaseArquivo.ConfirmandoDados;
        p.PerguntouEm = _agora();
        p.RespostaDados = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _log($"[arquivo] rede de dados móveis: esperando confirmar {p.Oferta.Total / 1e6:N0} MB do plano.");
        AvisarMudanca();
        bool seguir = await p.RespostaDados.Task;
        if (!seguir) EncerrarEAvisar(p, FaseArquivo.Cancelado, $"{_sala.MeuNome} preferiu não gastar o plano de dados agora.", $"{_sala.MeuNome} não vai baixar agora.");
        else p.Fase = FaseArquivo.Conectando;
        return seguir;
    }

    /// <summary>A resposta da pessoa ao aviso de dados móveis (true = baixar mesmo assim). <paramref name="naoPerguntarDeNovo"/> guarda a escolha no aparelho.</summary>
    public void ResponderDados(string pedido, bool seguir, bool naoPerguntarDeNovo = false) => Rodar(() =>
    {
        if (Achar(pedido) is not { Fase: FaseArquivo.ConfirmandoDados } p) return;
        if (naoPerguntarDeNovo) AoEscolherNaoPerguntarDados?.Invoke();
        p.RespostaDados?.TrySetResult(seguir);
    });

    /// <summary>Um arquivo: baixa e confere (<c>Download</c>), entrega onde a pessoa o acha e guarda a referência.</summary>
    async Task ReceberUmAsync(PedidoEmCurso p, int i)
    {
        var arquivo = p.Oferta.Arquivos[i];
        p.ArquivoAtual = i;
        var textos = new TextosDownload(p.Apelido, "o arquivo", "arquivo", "[arquivo]");
        var download = new Download(p.Link!.Tunnel, arquivo.Identidade, _aparelho.PastaDeTrabalho, textos);
        string conferido = await Task.Run(() => download.RodarAsync(pr => Rodar(() => { p.Progresso = pr; AvisarMudanca(); }), p.Parar.Token));
        var entregue = await _aparelho.EntregarAsync(conferido, p.Parar.Token);
        p.Caminhos[arquivo.Identidade] = entregue.Referencia;
        p.Motivo ??= entregue.Aviso;
        // já no histórico: se o app for morto antes do último arquivo, este não pode virar "parou" na recuperação
        Historico.Registrar(new ItemHistorico(Sentido.Recebido, p.Pedido, p.AmigoId, p.Apelido, arquivo.Nome, arquivo.Tamanho, arquivo.Identidade, _agora(), Resultado.Conferido, entregue.Referencia));
        p.PedacosRefeitos += download.Relatorio()["pedacos_refeitos"]?.GetValue<int>() ?? 0;
    }
}
