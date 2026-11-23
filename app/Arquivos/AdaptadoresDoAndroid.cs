using System.Text.Json.Nodes;
using Android.Content;
using Streemu.Arquivos;
using Streemu.Filme;
using Streemu.Net;
using Streemu.Proto.Player;
using AndroidUri = Android.Net.Uri;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>A sala do grupo do app (<see cref="Sessao"/>) vista pelo <see cref="FluxoDeArquivos"/>.</summary>
sealed class SalaAndroid : IPortaDaSala
{
    public string MeuNome => Sessao.Apelido;

    public Task EnviarAsync(string paraId, JsonObject mensagem) => Sessao.Sala is { } sala ? sala.SendTo(paraId, mensagem) : Task.CompletedTask;

    // Friends, não Find: o Find acha também a mim mesmo
    public bool AmigoNaSala(string id) => Sessao.Sala is { Connected: true } sala && sala.Friends.Any(f => f.Id == id);

    public bool PresenteNaSala(string id) => Sessao.Sala?.IsPresent(id) == true;
}

/// <summary>A ligação direta de cada pedido, montada como a do Cinema (<c>BaixarFilme</c>): IP da operadora quando serve e o relay de reserva do grupo.</summary>
sealed class FabricaAndroid : IFabricaDeLigacao
{
    public Task<PeerLink> CriarAsync(string sessao, string amigoId, string amigoNome, bool souEmissor)
    {
        var lan = PecaDoPlayer.IpLocal();
        return PeerLink.CreateAsync(sessao, amigoId, amigoNome, souEmissor, localMode: false, Sessao.Apelido,
            lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), "arquivo");
    }
}

/// <summary>
/// O celular como "aparelho" do envio de arquivo: lê o que vai pelo descritor do seletor (<see cref="Escolha"/>) e guarda o que vem na pasta
/// de recebidos (<see cref="Recebidos"/>). <paramref name="pastaEscolhida"/> devolve o endereço da pasta que a pessoa escolheu, ou nulo/o
/// texto padrão quando ela não escolheu nenhuma.
/// </summary>
sealed class AparelhoAndroid : IArquivosDoAparelho
{
    readonly Context _c;
    readonly Func<string?> _pastaEscolhida;

    public AparelhoAndroid(Context c, Func<string?> pastaEscolhida) { _c = c; _pastaEscolhida = pastaEscolhida; }

    public Stream AbrirParaEnvio(string referencia) => Escolha.AbrirParaLer(_c, AndroidUri.Parse(referencia)!);

    public string? IdentidadeAtual(string referencia)
    {
        try { using var fs = Escolha.AbrirParaLer(_c, AndroidUri.Parse(referencia)!); return Identidade.De(fs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Java.IO.IOException or Java.Lang.SecurityException)
        {
            LogApp.Info($"[arquivo] o arquivo do pedir de novo não abriu mais ({e.GetType().Name})");
            return null;
        }
    }

    public string PastaDeTrabalho => Recebidos.PastaDoApp(_c);

    public string? FaltaEspaco(long totalBytes, long jaBaixadoBytes) => Recebidos.FaltaEspaco(_c, totalBytes, jaBaixadoBytes, _pastaEscolhida());

    public async Task<ArquivoEntregue> EntregarAsync(string caminhoConferido, CancellationToken ct)
    {
        var p = await Recebidos.PublicarAsync(_c, caminhoConferido, _pastaEscolhida(), ct);
        return new ArquivoEntregue(p.Nome, p.Onde, p.UriConteudo ?? p.Caminho ?? "", p.Aviso);
    }
}
