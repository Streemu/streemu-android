using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Arquivos;

/// <summary>O fim de cada pedido (histórico, telemetria, ligação) e os prazos de 1 s. É a segunda metade do <c>MainForm.ArquivosLigacao.cs</c> e o <c>ArquivosTick</c> do app Windows.</summary>
public sealed partial class FluxoDeArquivos
{
    // ------------------------------------------------------------------ prazos

    /// <summary>Chamar a cada 1 s enquanto houver pedido (a tela liga um relógio no primeiro uso). Cuida do "visto", dos 10 min, da ligação caída e do progresso.</summary>
    public void Tick() => Rodar(TickNaFila);

    void TickNaFila()
    {
        var agora = _agora();
        foreach (var p in Pedidos)
        {
            if (p.Terminado) { if (agora - p.TerminouEm > GuardarTerminado) Esquecer(p.Pedido); continue; }
            if (p.SemVisto(agora)) Terminar(p, FaseArquivo.PrecisaAtualizar, MotivoSemVisto(p));
            else if (p.Venceu(agora)) Expirar(p);
            else if (LigacaoCaiu(p)) Terminar(p, p.Fase == FaseArquivo.Conectando ? FaseArquivo.Falhou : FaseArquivo.Parou, $"A ligação com {p.Apelido} caiu. O que já veio fica guardado.");
            else AtualizarProgressoDoEnvio(p);
        }
        if (Pedidos.Any(p => !p.Terminado)) AvisarMudanca();
    }

    /// <summary>
    /// "Precisa atualizar" só quando o amigo ainda está na sala; se saiu, o motivo certo é outro. Com o celular, soma-se a possibilidade de o app
    /// dele estar em segundo plano: a mensagem não promete a causa, só diz as duas coisas prováveis.
    /// </summary>
    string MotivoSemVisto(PedidoEmCurso p) => _sala.PresenteNaSala(p.AmigoId)
        ? $"{p.Apelido} precisa atualizar o Streemu para trocar arquivos, ou está com o app fechado."
        : $"{p.Apelido} saiu da sala.";

    /// <summary>
    /// D4: 10 min sem resposta. Pedido esperando aceite expira sozinho dos dois lados, com o mesmo prazo: avisar o outro faria
    /// ele terminar em "cancelado" se o aviso chegasse antes do prazo dele. Só quem recebe parado no aviso de cota avisa, porque do
    /// outro lado a ligação está aberta e sem prazo.
    /// </summary>
    void Expirar(PedidoEmCurso p)
    {
        if (p.Fase is FaseArquivo.ConfirmandoRelay or FaseArquivo.ConfirmandoDados)
            _ = _sala.EnviarAsync(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Cancelado, p.Pedido, "O pedido expirou."));
        Terminar(p, FaseArquivo.Expirou, "O pedido expirou: ninguém respondeu em 10 min.");
    }

    // ------------------------------------------------------------------ recuperação

    /// <summary>
    /// Chamar uma vez ao criar o fluxo, antes de qualquer pedido novo. Os pedidos que estavam andando quando o app foi morto viram "parou" no
    /// histórico (os arquivos que já tinham chegado ficam como estavam: conferidos). Devolve quantos pedidos foram recuperados, para o app avisar.
    /// Ex.: <c>int n = fluxo.RecuperarInterrompidos(); if (n &gt; 0) Avisar($"{n} envio(s) pararam quando o app fechou");</c>
    /// </summary>
    public int RecuperarInterrompidos()
    {
        if (_guarda == null) return 0;
        var perdidos = _guarda.Interrompidos;
        foreach (var p in perdidos)
            foreach (var a in p.Arquivos.Where(a => !Historico.Itens.Any(i => i.Pedido == p.Pedido && i.Identidade == a.Identidade && i.Resultado == Resultado.Conferido)))
                Historico.Registrar(new ItemHistorico(p.Sentido, p.Pedido, p.AmigoId, p.Apelido, a.Nome, a.Tamanho, a.Identidade, _agora(), Resultado.Parou, p.Referencias?.GetValueOrDefault(a.Identidade)));
        _guarda.Limpar();
        if (perdidos.Count > 0) _log($"[arquivo] {perdidos.Count} pedido(s) estavam andando quando o app foi fechado; marquei como \"parou\".");
        return perdidos.Count;
    }

    // ------------------------------------------------------------------ fim

    /// <summary>Encerra e avisa o outro lado pela sala (a ligação pode já ter caído). <paramref name="paraOAmigo"/>: sem ele, vai o mesmo motivo.</summary>
    void EncerrarEAvisar(PedidoEmCurso p, FaseArquivo fase, string motivo, string? paraOAmigo = null)
    {
        if (p.Terminado) return;
        _ = _sala.EnviarAsync(p.AmigoId, MensagemArquivo.Simples(MensagemArquivo.Cancelado, p.Pedido, paraOAmigo ?? motivo));
        Terminar(p, fase, motivo);
    }

    /// <summary>O fim de um pedido, qualquer que seja: para o que estiver rodando, fecha a ligação e registra no histórico e na telemetria.</summary>
    void Terminar(PedidoEmCurso p, FaseArquivo fase, string? motivo)
    {
        if (p.Terminado) return;
        string? caminho = p.Link?.Tunnel.Target is { } alvo ? (alvo.Kind == CandKind.Direct && !ForcarRelay ? "direto" : "relay") : null;
        p.Fase = fase; p.Motivo = motivo; p.TerminouEm = _agora();
        p.Parar.Cancel();
        p.RespostaRelay?.TrySetResult(false);
        p.RespostaDados?.TrySetResult(false);
        FecharLigacao(p);
        _guarda?.Remover(p.Pedido);
        RegistrarNoHistorico(p);
        EnviarTelemetria(p, caminho);
        _log($"[arquivo] pedido {p.Pedido} terminou: {fase}.");
        AvisarMudanca();
    }

    void FecharLigacao(PedidoEmCurso p)
    {
        var (envio, link) = (p.Envio, p.Link);
        p.Envio = null; p.Link = null;
        Task.Run(() => { envio?.Dispose(); link?.Dispose(); });
    }

    /// <summary>
    /// Uma linha por arquivo. Quem recebe sabe arquivo a arquivo (o que já chegou tem referência); quem manda só sabe o pedido inteiro.
    /// O "pedir de novo" que não chegou a ser oferecido não vira linha.
    /// </summary>
    void RegistrarNoHistorico(PedidoEmCurso p)
    {
        if (p.Fase == FaseArquivo.PrecisaAtualizar && p.Sentido == Sentido.Recebido) return;
        foreach (var a in p.Oferta.Arquivos)
        {
            bool chegou = p.Sentido == Sentido.Recebido && p.Caminhos.ContainsKey(a.Identidade);
            var resultado = chegou ? Resultado.Conferido : p.ParaHistorico();
            Historico.Registrar(new ItemHistorico(p.Sentido, p.Pedido, p.AmigoId, p.Apelido, a.Nome, a.Tamanho, a.Identidade,
                _agora(), resultado, p.Caminhos.GetValueOrDefault(a.Identidade)));
        }
    }

    /// <summary>Sem nome de arquivo, sem tamanho exato e sem o motivo (que traz apelidos): só a faixa, a fase e o caminho (critério 18 da spec 030).</summary>
    void EnviarTelemetria(PedidoEmCurso p, string? caminho) => _telemetria("arquivo_fim", new JsonObject
    {
        ["sentido"] = p.Sentido == Sentido.Enviado ? "enviado" : "recebido",
        ["arquivos"] = p.Oferta.Arquivos.Count,
        ["faixa"] = PedidoEmCurso.Faixa(p.Oferta.Total),
        ["segundos"] = Math.Round(p.Relogio.Elapsed.TotalSeconds, 1),
        ["caminho"] = caminho,
        ["resultado"] = p.Fase.ToString(),
        ["pedacos_refeitos"] = p.PedacosRefeitos,
    });
}
