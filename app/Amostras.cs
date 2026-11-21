namespace Streemu.Proto;

/// <summary>Atrasos medidos (ms): a janela desde a última leitura (mediana, p95, máximo) e o total da sessão, para o resumo.</summary>
sealed class Amostras
{
    readonly object _gate = new();
    readonly List<double> _janela = new(), _total = new();

    public void Add(double ms)
    {
        lock (_gate)
        {
            _janela.Add(ms);
            if (_total.Count < 200_000) _total.Add(ms);
        }
    }

    public Resumo? Janela() { lock (_gate) { var r = Resumo.De(_janela); _janela.Clear(); return r; } }
    public Resumo? Total() { lock (_gate) return Resumo.De(_total); }

    public sealed record Resumo(int N, double Mediana, double P95, double Max)
    {
        public static Resumo? De(List<double> l)
        {
            if (l.Count == 0) return null;
            var o = l.OrderBy(x => x).ToArray();
            return new(o.Length, o[o.Length / 2], o[(int)Math.Min(o.Length - 1, Math.Ceiling(o.Length * 0.95) - 1)], o[^1]);
        }
        public override string ToString() => $"{Mediana:0} ms (p95 {P95:0}, máx {Max:0}, n={N})";
    }
}
