namespace Streemu.Filme;

/// <summary>Como vai o download (para a tela e para o anfitrião, 4.3).</summary>
public sealed record Progresso(string Fase, long Feito, long Total, double BytesPorSegundo)
{
    public int Pct => Total <= 0 ? 0 : (int)Math.Min(100, Feito * 100 / Total);
    public double EtaS => BytesPorSegundo > 1 ? (Total - Feito) / BytesPorSegundo : -1;

    public static string Faltam(double s) =>
        s < 0 ? "" : s < 60 ? $"falta{(s >= 2 ? "m" : "")} {Math.Max(1, (int)s)} s" : s < 3600 ? $"faltam {(int)Math.Ceiling(s / 60)} min" : $"faltam {(int)(s / 3600)} h {(int)(s % 3600 / 60)} min";
}
