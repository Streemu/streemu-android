using Android.Content;
using Streemu.Arquivos;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Liga os fluxos de envio de arquivo ao serviço e às notificações (spec 030, T6): quando um pedido muda, decide se o serviço em primeiro
/// plano precisa ficar de pé, atualiza o texto do andamento, avisa de um pedido novo (com Aceitar e Recusar) e do resultado quando acaba.
/// Vale para mais de um fluxo (o app tem um; a atividade de teste tem dois, um de cada lado).
/// </summary>
static class PresencaDeArquivos
{
    static readonly List<FluxoDeArquivos> Fluxos = new();
    static readonly Dictionary<string, bool> PedidoNaTela = new();   // pedido -> a notificação do pedido ainda está na tela
    static readonly HashSet<string> FimAvisado = new();
    static readonly object Trava = new();
    static Context? _app;

    /// <summary>O Android 15+ deu o tempo do serviço por encerrado e ele parou; só religa quando a pessoa voltar ao app (ver <see cref="AoVoltarParaFrente"/>).</summary>
    static bool _pausadoPeloSistema;

    public static void Ligar(Context c, FluxoDeArquivos fluxo)
    {
        _app = c.ApplicationContext;
        NotificacaoArquivos.Preparar(c);
        lock (Trava) Fluxos.Add(fluxo);
        fluxo.Mudou += Reagir;
    }

    /// <summary>O fluxo que tem o pedido que CHEGOU a este aparelho (os botões Aceitar e Recusar da notificação são só de pedido recebido).</summary>
    internal static FluxoDeArquivos? FluxoDoPedidoRecebido(string pedido)
    {
        lock (Trava) return Fluxos.FirstOrDefault(f => f.Pedidos.Any(p => p.Pedido == pedido && p.Sentido == Sentido.Recebido));
    }

    internal static List<FluxoDeArquivos> TodosOsFluxos() { lock (Trava) return Fluxos.ToList(); }

    /// <summary>Um retrato de todos os pedidos de todos os fluxos.</summary>
    static List<PedidoEmCurso> Todos() { lock (Trava) return Fluxos.SelectMany(f => f.Pedidos).ToList(); }

    /// <summary>
    /// Pedido que precisa do app vivo. Quem manda conta desde que oferece (espera o amigo aceitar por até 10 min com o serviço de pé);
    /// quem recebe, desde que aceita (um pedido ainda esperando aceite não precisa de serviço: só chega se o app está aberto).
    /// </summary>
    internal static bool Ativo(PedidoEmCurso p) => !p.Terminado && (p.Sentido == Sentido.Enviado
        ? p.Fase is FaseArquivo.Esperando or FaseArquivo.Conectando or FaseArquivo.Transferindo or FaseArquivo.ConfirmandoRelay
        : p.Fase is FaseArquivo.Conectando or FaseArquivo.Transferindo or FaseArquivo.ConfirmandoRelay or FaseArquivo.ConfirmandoDados);

    internal static void Reagir()
    {
        var c = _app;
        if (c == null) return;
        var todos = Todos();
        foreach (var p in todos) AvisarDoPedido(c, p);
        var ativos = todos.Where(Ativo).ToList();
        if (ativos.Count == 0) { if (ServicoArquivos.Rodando) ServicoArquivos.Parar(c); return; }
        if (ServicoArquivos.Rodando) NotificacaoArquivos.AtualizarAndamento(c, TextoDeAndamento(ativos), PorcentoDe(ativos));
    }

    static void AvisarDoPedido(Context c, PedidoEmCurso p)
    {
        bool esperandoMeuAceite = p.Sentido == Sentido.Recebido && p.Fase == FaseArquivo.Esperando;
        lock (Trava)
        {
            if (esperandoMeuAceite && !PedidoNaTela.ContainsKey(p.Pedido)) { PedidoNaTela[p.Pedido] = true; NotificacaoArquivos.NotificarPedido(c, p); }
            else if (!esperandoMeuAceite && PedidoNaTela.TryGetValue(p.Pedido, out var naTela) && naTela) { PedidoNaTela[p.Pedido] = false; NotificacaoArquivos.Remover(c, p.Pedido); }
            AvisarDoGastoDeDados(c, p);
            if (p.Terminado && FimAvisado.Add(p.Pedido)) AvisarOFim(c, p);
        }
    }

    static readonly Dictionary<string, bool> GastoNaTela = new();   // pedido -> a notificação de gasto de dados ainda está na tela

    /// <summary>Chamado com a trava: mostra "Baixar usando dados móveis?" quando o pedido pede e a tira quando a pessoa respondeu.</summary>
    static void AvisarDoGastoDeDados(Context c, PedidoEmCurso p)
    {
        bool perguntando = p.Fase == FaseArquivo.ConfirmandoDados;
        if (perguntando && !GastoNaTela.ContainsKey(p.Pedido)) { GastoNaTela[p.Pedido] = true; NotificacaoArquivos.NotificarGastoDeDados(c, p); }
        else if (!perguntando && GastoNaTela.TryGetValue(p.Pedido, out var naTela) && naTela) { GastoNaTela[p.Pedido] = false; NotificacaoArquivos.RemoverGastoDeDados(c, p.Pedido); }
    }

    /// <summary>O que a pessoa não pediu nem fez ela mesma: terminou, ou deu errado. Recusar e cancelar por conta própria não precisam de aviso.</summary>
    static void AvisarOFim(Context c, PedidoEmCurso p)
    {
        string quem = p.Apelido, o = NotificacaoArquivos.TextoDaOferta(p.Oferta);
        switch (p.Fase)
        {
            case FaseArquivo.Conferido: NotificacaoArquivos.NotificarFim(c, p, p.Sentido == Sentido.Recebido ? "Arquivo recebido ✓" : $"{quem} recebeu e conferiu", p.Sentido == Sentido.Recebido ? $"{o}, de {quem}" : o); break;
            case FaseArquivo.Recusado or FaseArquivo.Cancelado when p.Sentido == Sentido.Recebido: break;
            default: NotificacaoArquivos.NotificarFim(c, p, TituloDoFim(p.Fase), p.Motivo ?? o); break;
        }
    }

    static string TituloDoFim(FaseArquivo fase) => fase switch
    {
        FaseArquivo.Recusado => "Pedido recusado",
        FaseArquivo.Cancelado => "Pedido cancelado",
        FaseArquivo.Expirou => "O pedido expirou",
        FaseArquivo.Parou => "A transferência parou",
        FaseArquivo.Bloqueado => "Transferência bloqueada",
        FaseArquivo.PrecisaAtualizar => "O amigo não respondeu",
        _ => "Não deu para trocar o arquivo",
    };

    /// <summary>A linha da notificação do serviço: um pedido, em detalhe; vários, só a contagem.</summary>
    internal static string TextoDeAndamento() => TextoDeAndamento(Todos().Where(Ativo).ToList());

    static string TextoDeAndamento(List<PedidoEmCurso> ativos)
    {
        if (ativos.Count == 0) return "Preparando a transferência";
        if (ativos.Count > 1) return $"{ativos.Count} transferências em andamento";
        var p = ativos[0];
        string vel = p.Progresso is { BytesPorSegundo: > 0 } pr ? $" · {pr.BytesPorSegundo / 1e6:N1} MB/s" : "";
        return p.Fase switch
        {
            FaseArquivo.Esperando => $"Esperando {p.Apelido} aceitar",
            FaseArquivo.Conectando => $"Conectando com {p.Apelido}…",
            FaseArquivo.ConfirmandoRelay => $"Confirme o uso do relay de reserva ({p.Apelido})",
            FaseArquivo.ConfirmandoDados => $"Confirme o uso de dados móveis ({p.Apelido})",
            _ => $"{(p.Sentido == Sentido.Enviado ? "Enviando para" : "Recebendo de")} {p.Apelido}: {p.Progresso?.Pct ?? 0}%{vel}",
        };
    }

    static int PorcentoDe(List<PedidoEmCurso> ativos) =>
        ativos.Count == 1 && ativos[0].Fase == FaseArquivo.Transferindo && ativos[0].Progresso is { } pr ? pr.Pct : -1;

    internal static void AoTimeoutDoSistema(Context c)
    {
        _pausadoPeloSistema = true;
        NotificacaoArquivos.Avisar(c, "O Android pausou as transferências", "O sistema limita o tempo em segundo plano. Toque para abrir o Streemu e continuar; o que já veio fica guardado.");
    }

    /// <summary>A pessoa voltou ao app: o limite do Android 15 recomeça, então religa o serviço se ainda há pedido andando.</summary>
    internal static void AoVoltarParaFrente(Context c)
    {
        if (!_pausadoPeloSistema && ServicoArquivos.Rodando) return;
        _pausadoPeloSistema = false;
        if (Todos().Any(Ativo)) ServicoArquivos.Iniciar(c);
    }
}
