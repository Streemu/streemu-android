namespace Streemu.Arquivos;

/// <summary>Onde o arquivo recebido é entregue depois de conferido (spec 030, seção 4).</summary>
public enum DestinoDeRecebidos
{
    /// <summary>A pasta pública <c>Downloads/Streemu</c>, pelo MediaStore, sem permissão de armazenamento (Android 10+).</summary>
    Downloads,
    /// <summary>A mesma <c>Downloads/Streemu</c> no Android 8 e 9, gravando direto na pasta, com a permissão de escrita que só essas versões pedem.</summary>
    DownloadsDireto,
    /// <summary>A pasta que a pessoa escolheu pelo seletor de pastas (opção avançada; qualquer versão).</summary>
    PastaEscolhida,
    /// <summary>Último recurso: a pasta do próprio app (Android/data), funda e difícil de achar. Só no Android 8 e 9 enquanto a pessoa não deu a permissão de escrita.</summary>
    PastaDoApp,
}

/// <summary>
/// A regra de "para onde vai" por versão do Android, separada do código que fala com o sistema para poder ser testada para todas as versões
/// sem aparelho (spec 030, D4 e D9). **Os recebidos ficam sempre numa pasta fixa e fácil de achar, <c>Downloads/Streemu</c>** (decisão de produto,
/// 2026-11-23: quem não é hábil não pode ter de procurar em pastas internas). Antes do Android 10 (API 29) o MediaStore não grava em
/// Downloads, então ali o app pede, uma vez e com explicação, a permissão de escrita (que só existe até o Android 9).
/// Ex.: <c>PoliticaDeDestino.Decidir(sdk: 28, temPastaEscolhida: false, temPermissaoDeEscrita: true)</c> → <see cref="DestinoDeRecebidos.DownloadsDireto"/>.
/// </summary>
public static class PoliticaDeDestino
{
    public const int PrimeiraComDownloadsPeloMediaStore = 29;

    public static DestinoDeRecebidos Decidir(int sdk, bool temPastaEscolhida, bool temPermissaoDeEscrita)
    {
        if (temPastaEscolhida) return DestinoDeRecebidos.PastaEscolhida;
        if (sdk >= PrimeiraComDownloadsPeloMediaStore) return DestinoDeRecebidos.Downloads;
        return temPermissaoDeEscrita ? DestinoDeRecebidos.DownloadsDireto : DestinoDeRecebidos.PastaDoApp;
    }

    /// <summary>
    /// A tela deve pedir a permissão de escrita antes do primeiro recebimento: só no Android 8 e 9, sem pasta escolhida e sem a permissão.
    /// Se a pessoa negar, o arquivo ainda chega (na pasta do app) e a tela diz como liberar.
    /// </summary>
    public static bool PrecisaPedirPermissao(int sdk, bool temPastaEscolhida, bool temPermissaoDeEscrita) =>
        sdk < PrimeiraComDownloadsPeloMediaStore && !temPastaEscolhida && !temPermissaoDeEscrita;

    /// <summary>O arquivo conferido ainda é copiado para fora da pasta do app (pior caso de espaço: o arquivo duas vezes).</summary>
    public static bool CopiaNoFim(DestinoDeRecebidos destino) => destino != DestinoDeRecebidos.PastaDoApp;
}
