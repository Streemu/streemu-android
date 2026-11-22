namespace Streemu.Filme;

/// <summary>Um bloco do mpegts (uma unidade de acesso, múltiplo de 188 bytes) com o carimbo do quadro, em s do filme. <see cref="Chave"/>: o bloco começa com PAT+PMT e um quadro-chave.</summary>
public readonly record struct BlocoTs(byte[] Dados, bool Chave, double PtsS, bool Video);

/// <summary>
/// De onde o <see cref="EnvioFilmeCopia"/> tira o mpegts do filme (spec 028, 5.2). No celular é o MediaExtractor → TsMux; no teste, um gerador. Uma instância por época: <see cref="Abrir"/> uma vez,
/// <see cref="Proximo"/> até acabar, <see cref="IDisposable.Dispose"/>. Tudo roda na thread da época, uma por vez.
/// </summary>
public interface IFonteTs : IDisposable
{
    /// <summary>Começa a ler em <paramref name="posS"/> (o quadro-chave anterior, para o vídeo decodificar). Pode levar segundos num arquivo grande.</summary>
    void Abrir(double posS);

    /// <summary>O próximo bloco, na ordem em que o filme anda; nulo no fim do arquivo.</summary>
    BlocoTs? Proximo();
}
