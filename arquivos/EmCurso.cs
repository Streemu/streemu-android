using System.Diagnostics;
using Streemu.Filme;
using Streemu.Net;

namespace Streemu.Arquivos;

/// <summary>Em que ponto está um pedido (spec 020). As fases de fim viram uma linha do histórico (<see cref="PedidoEmCurso.ParaHistorico"/>).</summary>
public enum FaseArquivo
{
    /// <summary>Oferta saiu (ou chegou) e espera o aceite.</summary>
    Esperando,
    /// <summary>Quem manda: o amigo pediu de novo pelo histórico; falta dizer Mandar ou Recusar.</summary>
    PedidoDeNovo,
    /// <summary>Quem recebe: pediu de novo e espera a oferta, que aceita sozinho.</summary>
    AguardandoDeNovo,
    Conectando,
    /// <summary>Quem recebe: a ligação caiu no relay; falta confirmar o gasto da cota (D1).</summary>
    ConfirmandoRelay,
    /// <summary>Quem recebe, em rede de dados móveis: falta confirmar o gasto do plano antes do primeiro byte (spec 030, D5; só no celular).</summary>
    ConfirmandoDados,
    Transferindo,
    Conferido, Recusado, Expirou, Cancelado, Falhou, Parou, Bloqueado, PrecisaAtualizar,
}

/// <summary>
/// Um pedido em andamento, dos dois lados: a oferta, a fase e o que a ligação precisa. Vive só na memória; ao terminar vira uma
/// linha do <see cref="Historico"/>. Mexido só na thread da interface (o fluxo passa tudo por ela).
/// </summary>
public sealed class PedidoEmCurso
{
    /// <summary>D4: 10 min. STREEMU_TEST_ARQUIVO_VALIDADE_S encurta, só para o teste da expiração não esperar 10 min.</summary>
    public static readonly TimeSpan Validade = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_ARQUIVO_VALIDADE_S"), out var s) && s > 0
        ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EsperaDoVisto = TimeSpan.FromSeconds(10);

    public PedidoEmCurso(Sentido sentido, string amigoId, string apelido, OfertaDeArquivos oferta, DateTime criado)
    {
        Sentido = sentido; AmigoId = amigoId; Apelido = apelido; Oferta = oferta; Criado = criado;
    }

    public Sentido Sentido { get; }
    public string AmigoId { get; }
    public string Apelido { get; }
    public OfertaDeArquivos Oferta { get; set; }
    public string Pedido => Oferta.Pedido;
    public DateTime Criado { get; }
    public FaseArquivo Fase { get; set; } = FaseArquivo.Esperando;
    /// <summary>O amigo respondeu "visto": tem a versão com o envio de arquivo.</summary>
    public bool Visto { get; set; }
    /// <summary>Por que terminou (recusa, erro), já em português para a tela.</summary>
    public string? Motivo { get; set; }
    public int ArquivoAtual { get; set; }
    public Progresso? Progresso { get; set; }
    /// <summary>Identidade → caminho: de onde sai (quem manda) ou onde ficou (quem recebe).</summary>
    public Dictionary<string, string> Caminhos { get; } = new();

    internal PeerLink? Link { get; set; }
    internal Envio? Envio { get; set; }
    internal string? EnvioDe { get; set; }
    internal CancellationTokenSource Parar { get; } = new();
    /// <summary>Quem recebe, no relay: a resposta da pessoa ao aviso de cota (true = seguir).</summary>
    internal TaskCompletionSource<bool>? RespostaRelay { get; set; }
    /// <summary>Quem recebe, em dados móveis: a resposta da pessoa ao aviso de gasto do plano (true = baixar).</summary>
    internal TaskCompletionSource<bool>? RespostaDados { get; set; }
    internal Stopwatch Relogio { get; } = new();
    internal bool PeloRelay { get; set; }
    internal int SegundosSemLigacao { get; set; }
    /// <summary>Quem manda: bytes do envio no segundo anterior, para a velocidade na tela.</summary>
    internal long BytesAntes { get; set; }
    public DateTime? TerminouEm { get; set; }
    /// <summary>Quem recebe: pedaços que chegaram diferentes do manifesto e foram pedidos de novo (todos os arquivos do pedido).</summary>
    public int PedacosRefeitos { get; set; }

    public bool Terminado => Fase >= FaseArquivo.Conferido;

    /// <summary>Quem recebe: quando apareceu o aviso de cota do relay.</summary>
    internal DateTime? PerguntouEm { get; set; }

    /// <summary>
    /// Esperando uma resposta além do prazo (D4): o aceite, ou a confirmação da cota do relay. Sem prazo na cota, quem recebe que
    /// saísse de perto deixaria os dois lados com a ligação aberta para sempre.
    /// </summary>
    public bool Venceu(DateTime agora)
    {
        if (Fase is FaseArquivo.ConfirmandoRelay or FaseArquivo.ConfirmandoDados) return agora - (PerguntouEm ?? agora) > Validade;
        return (Fase is FaseArquivo.Esperando or FaseArquivo.PedidoDeNovo or FaseArquivo.AguardandoDeNovo) && agora - Criado > Validade;
    }

    /// <summary>
    /// O amigo não disse "visto" a tempo: versão sem o envio de arquivo, ou saiu da sala. Vale para a oferta de quem manda e para o
    /// "pedir de novo" de quem recebe; a oferta que chega a quem recebe não espera nada.
    /// </summary>
    public bool SemVisto(DateTime agora) =>
        !Visto && agora - Criado > EsperaDoVisto
        && (Sentido == Sentido.Enviado && Fase == FaseArquivo.Esperando || Fase == FaseArquivo.AguardandoDeNovo);

    public Resultado ParaHistorico() => Fase switch
    {
        FaseArquivo.Conferido => Resultado.Conferido,
        FaseArquivo.Recusado or FaseArquivo.Bloqueado => Resultado.Recusado,
        FaseArquivo.Expirou or FaseArquivo.PrecisaAtualizar => Resultado.Expirou,
        FaseArquivo.Cancelado => Resultado.Cancelado,
        FaseArquivo.Parou => Resultado.Parou,
        _ => Resultado.Falhou,
    };

    /// <summary>Faixa de tamanho para a telemetria: o tamanho exato ajudaria a reconhecer o arquivo (spec 018).</summary>
    public static string Faixa(long bytes) => bytes switch
    {
        < 10_000_000 => "<10MB", < 100_000_000 => "<100MB", < 1_000_000_000 => "<1GB", < 10_000_000_000 => "<10GB", _ => ">=10GB",
    };
}
