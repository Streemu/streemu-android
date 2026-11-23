using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Arquivos;

/// <summary>A sala do grupo, vista pelo envio de arquivo: só o que o fluxo precisa dela (no app, o <c>GroupRoom</c>; nos testes, uma sala de mentira).</summary>
public interface IPortaDaSala
{
    /// <summary>O nome que o amigo vê.</summary>
    string MeuNome { get; }

    Task EnviarAsync(string paraId, JsonObject mensagem);

    /// <summary>O amigo está na sala agora? Não conta eu mesmo (o <c>Find</c> da sala acha também a mim).</summary>
    bool AmigoNaSala(string id);

    /// <summary>Alguém com esse id está na sala (amigo ou eu)? Serve para dizer "saiu da sala" em vez de "precisa atualizar".</summary>
    bool PresenteNaSala(string id);
}

/// <summary>Cria a ligação direta de um pedido (o <c>PeerLink</c>), com o relay, o IP da operadora e o modo local que o aparelho usa.</summary>
public interface IFabricaDeLigacao
{
    Task<PeerLink> CriarAsync(string sessao, string amigoId, string amigoNome, bool souEmissor);
}

/// <summary>Um arquivo que a pessoa escolheu para mandar. <see cref="Referencia"/> é como o aparelho o reabre depois (no Android, o endereço do seletor).</summary>
public sealed record ArquivoParaEnviar(string Referencia, string Nome, long Tamanho, string Identidade);

/// <summary>Onde um arquivo recebido ficou depois de conferido. <see cref="Onde"/> é o texto que a pessoa lê; <see cref="Referencia"/> reabre o arquivo (Abrir).</summary>
public sealed record ArquivoEntregue(string Nome, string Onde, string Referencia, string? Aviso);

/// <summary>Tudo o que o envio de arquivo pede ao aparelho: ler o que vai, guardar o que vem, saber se cabe. No app, em cima do <c>Escolha</c> e do <c>Recebidos</c>.</summary>
public interface IArquivosDoAparelho
{
    /// <summary>Abre o arquivo para ler, com posição (o envio lê por pedaço, de qualquer ponto). Quem chama fecha.</summary>
    Stream AbrirParaEnvio(string referencia);

    /// <summary>A identidade que o arquivo tem agora, ou nulo se sumiu ou o acesso se perdeu. Serve ao "pedir de novo" (D6).</summary>
    string? IdentidadeAtual(string referencia);

    /// <summary>A pasta onde o download grava e confere o arquivo (a pasta do app, no Android).</summary>
    string PastaDeTrabalho { get; }

    /// <summary>Por que não cabe (nulo = cabe), já em português, antes de aceitar.</summary>
    string? FaltaEspaco(long totalBytes, long jaBaixadoBytes);

    /// <summary>Leva o arquivo conferido para onde a pessoa o acha e apaga a cópia de trabalho.</summary>
    Task<ArquivoEntregue> EntregarAsync(string caminhoConferido, CancellationToken ct);
}
