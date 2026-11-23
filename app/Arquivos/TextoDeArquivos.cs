using Android.Content;
using Android.Graphics;
using Streemu.Arquivos;
using Streemu.Filme;
using Streemu.Proto.Ui;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Os textos e as cores da tela de arquivos: o que cada fase de um pedido diz e como cada resultado aparece no histórico. É o <c>TextoDoPedido</c> do PC
/// (só os textos, sem nada de tela), em português do Brasil, frases curtas que dizem o que está acontecendo e, quando falha, o que fazer.
/// Ex.: <c>TextoDeArquivos.TituloDoPedido(pedido)</c> → "Bia quer te mandar um arquivo".
/// </summary>
static class TextoDeArquivos
{
    public static string OndeFicam(Context c, Streemu.Arquivos.FluxoDeArquivos fluxo) => Recebidos.TextoDoDestino(c, fluxo.Historico.PastaRecebidos);

    public static string TituloDoPedido(PedidoEmCurso p) => (p.Sentido, p.Fase) switch
    {
        (Sentido.Recebido, FaseArquivo.Esperando) => $"{p.Apelido} quer te mandar {(p.Oferta.Arquivos.Count == 1 ? "um arquivo" : $"{p.Oferta.Arquivos.Count} arquivos")}",
        (Sentido.Enviado, FaseArquivo.Esperando) => $"Esperando {p.Apelido} aceitar…",
        (_, FaseArquivo.Conectando) => $"Conectando com {p.Apelido}…",
        (_, FaseArquivo.ConfirmandoRelay) => "Vai usar a cota do relay de reserva",
        (_, FaseArquivo.ConfirmandoDados) => "Baixar usando dados móveis?",
        (Sentido.Enviado, _) => $"Enviando para {p.Apelido}",
        _ => $"Recebendo de {p.Apelido}",
    };

    public static string LinhaDoPedido(PedidoEmCurso p) => p.Fase switch
    {
        FaseArquivo.ConfirmandoRelay => $"{NotificacaoArquivos.Tamanho(p.Oferta.Total)} vão pela rede de reserva do grupo, que tem cota limitada. Receber mesmo assim?",
        FaseArquivo.ConfirmandoDados => $"{NotificacaoArquivos.TextoDaOferta(p.Oferta)} vai gastar {NotificacaoArquivos.Tamanho(p.Oferta.Total)} do seu plano de dados. Em Wi-Fi não gasta.",
        FaseArquivo.Transferindo when p.Progresso is { } pr => LinhaDeProgresso(pr),
        _ => NotificacaoArquivos.TextoDaOferta(p.Oferta),
    };

    static string LinhaDeProgresso(Progresso pr)
    {
        string vel = pr.BytesPorSegundo > 0 ? $" · {pr.BytesPorSegundo / 1e6:N1} MB/s" : "";
        string falta = pr.EtaS >= 0 ? " · " + Progresso.Faltam(pr.EtaS) : "";
        return $"{pr.Pct}%{vel}{falta}";
    }

    public static string Resultado(Resultado r) => r switch
    {
        Streemu.Arquivos.Resultado.Conferido => "Conferido ✓",
        Streemu.Arquivos.Resultado.Recusado => "Recusado",
        Streemu.Arquivos.Resultado.Expirou => "Expirou",
        Streemu.Arquivos.Resultado.Cancelado => "Cancelado",
        Streemu.Arquivos.Resultado.Parou => "Parou",
        _ => "Falhou",
    };

    /// <summary>A cor do texto do selo de resultado (o fundo é a mesma cor, bem transparente): verde conferido, âmbar falhou ou parou, cinza o resto.</summary>
    public static Color CorDoTexto(Resultado r) => r switch
    {
        Streemu.Arquivos.Resultado.Conferido => Tema.Ok,
        Streemu.Arquivos.Resultado.Falhou or Streemu.Arquivos.Resultado.Parou => Tema.Aviso,
        _ => Tema.TextoApagado,
    };

    /// <summary>Foto ou imagem pelo nome (a extensão decide o ícone do quadradinho do histórico).</summary>
    public static bool EhImagem(string nome) =>
        System.IO.Path.GetExtension(nome).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".heic" or ".bmp";

    /// <summary>"De Bia · 1,6 MB · 23/11 17:40" (ou "Para"), a linha miúda de cada item do histórico.</summary>
    public static string LinhaDoItem(ItemHistorico i) =>
        $"{(i.Sentido == Sentido.Recebido ? "De" : "Para")} {i.Apelido} · {NotificacaoArquivos.Tamanho(i.Tamanho)} · {i.Quando:dd/MM HH:mm}";
}
