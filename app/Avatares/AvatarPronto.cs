using System.Security.Cryptography;
using Android.Graphics;

namespace Streemu.Proto.Avatares;

/// <summary>Como o avatar viaja para os amigos: uma miniatura PNG parada ou a <see cref="TiraDeQuadros"/> de um GIF.</summary>
enum FormatoDoAvatar { Png, Tira }

/// <summary>
/// O avatar escolhido, pronto para mostrar e para enviar: os <see cref="Dados"/> são exatamente o que vai na mensagem da sala (e o que fica guardado no
/// aparelho); a imagem original nunca sai. <see cref="Aviso"/> diz por que um GIF ficou parado (ex.: grande demais), ou é null.
/// Uso: <c>var pronto = PreparadorDeAvatar.Preparar(contexto, uri); Sessao.Enviar(pronto.Dados, pronto.Formato, pronto.Hash);</c>
/// </summary>
sealed record AvatarPronto(byte[] Dados, FormatoDoAvatar Formato, Bitmap Primeiro, Animacao? Animacao, string? Aviso)
{
    /// <summary>Os 16 hex do SHA-256 dos dados (o mesmo cálculo do app Windows): quem já tem esse hash não precisa receber de novo.</summary>
    public string Hash { get; } = HashDe(Dados);

    public static string HashDe(byte[] dados) => Convert.ToHexString(SHA256.HashData(dados))[..16].ToLowerInvariant();

    /// <summary>Reabre um avatar guardado no aparelho (os mesmos bytes que foram enviados). Null se o arquivo não abre mais.</summary>
    public static AvatarPronto? DeDados(byte[] dados, FormatoDoAvatar formato)
    {
        if (formato == FormatoDoAvatar.Tira)
            return TiraDeQuadros.Ler(dados, out _) is { } animacao ? new AvatarPronto(dados, formato, animacao.Quadros[0], animacao, null) : null;
        var parado = BitmapFactory.DecodeByteArray(dados, 0, dados.Length);
        return parado == null ? null : new AvatarPronto(dados, formato, parado, null, null);
    }
}

/// <summary>A imagem escolhida não serve. A mensagem já vem pronta para a tela, em português, com o motivo e o que fazer.</summary>
sealed class AvatarInvalidoException : Exception
{
    public AvatarInvalidoException(string mensagem) : base(mensagem) { }
}
