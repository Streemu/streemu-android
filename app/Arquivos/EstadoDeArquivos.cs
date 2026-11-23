using Streemu.Arquivos;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// O que a tela "Arquivos" lembra entre um redesenho e outro (a tela é refeita a cada mudança da sala e de um pedido): a aba aberta, o amigo e os
/// arquivos escolhidos para mandar, e o filtro do histórico. Vive na <c>MainActivity</c>; some quando o app fecha (uma escolha não enviada não vale a pena guardar).
/// </summary>
sealed class EstadoDeArquivos
{
    public bool NoHistorico;
    public bool SoRecebidos = true;
    /// <summary>"Mais opções" aberto: trocar a pasta de recebidos e voltar a perguntar dos dados móveis. Fechado por padrão (D4 da spec 030: ninguém precisa escolher pasta).</summary>
    public bool MaisOpcoes;
    public string? AmigoId;
    public List<ArquivoEscolhido> Escolhidos { get; } = new();

    public long TotalEscolhido => Escolhidos.Sum(a => a.Tamanho);
}

/// <summary>Um amigo que está na sala agora e pode receber (a tela não conhece a sala: quem a monta passa a lista).</summary>
sealed record AmigoDeArquivos(string Id, string Nick);

/// <summary>O que a tela pede à <c>MainActivity</c>: cada botão é uma ação nomeada, e a tela não sabe como ela é feita.</summary>
sealed record AcoesDeArquivos(
    Action Selecionar, Action<ArquivoEscolhido> TirarEscolhido, Action Enviar,
    Action<string> Aceitar, Action<string> Recusar, Action<string> Cancelar,
    Action<string, bool> ResponderRelay, Action<string, bool> ResponderDados,
    Action<ItemHistorico> Abrir, Action<ItemHistorico> MostrarNaPasta, Action<ItemHistorico> PedirDeNovo,
    Action LimparHistorico, Action Redesenhar,
    Action TrocarPasta, Action VoltarParaPastaPadrao, Action VoltarAPerguntarDados);
