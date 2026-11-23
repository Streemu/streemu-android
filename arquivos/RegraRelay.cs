namespace Streemu.Arquivos;

/// <summary>O que fazer com um envio quando a ligação passa pelo relay de reserva (spec 020, D1).</summary>
public enum DecisaoRelay
{
    /// <summary>Segue sem perguntar: caminho direto, ou pouco para a cota.</summary>
    Livre,
    /// <summary>Quem recebe confirma antes do primeiro byte, sabendo quanto vai gastar da cota.</summary>
    Avisar,
    /// <summary>Não sai: grande demais para a cota do grupo.</summary>
    Bloquear,
}

/// <summary>
/// D1 da spec 020: o relay (TURN da Cloudflare) é grátis até 1.000 GB/mês, dividido com as lives, e o Streemu não tem verba para
/// passar disso. O limite vale para o TOTAL do pedido, não por arquivo: por arquivo, 20 arquivos de 1,9 GB somariam 38 GB.
/// MB e GB decimais, como o resto da interface (<c>/ 1e6</c>).
/// </summary>
public static class RegraRelay
{
    public const long AteSemAviso = 100_000_000, Teto = 2_000_000_000;

    /// <summary>Ex.: <c>RegraRelay.Decidir(pelaReserva: true, totalBytes: 1_200_000_000)</c> → <see cref="DecisaoRelay.Avisar"/>.</summary>
    public static DecisaoRelay Decidir(bool pelaReserva, long totalBytes)
    {
        if (!pelaReserva || totalBytes <= AteSemAviso) return DecisaoRelay.Livre;
        return totalBytes <= Teto ? DecisaoRelay.Avisar : DecisaoRelay.Bloquear;
    }
}
