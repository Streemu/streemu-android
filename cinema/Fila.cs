namespace Streemu.Filme;

/// <summary>
/// A fila de reprodução do anfitrião (vários episódios em sequência, pedido de 2026-11-18, depois de uma sessão de vários episódios): os caminhos dos arquivos, em ordem, e qual está
/// tocando. Só existe no PC de quem abriu o filme e some quando ele sai; quem assiste recebe só os nomes (filme_fila). Não toca em disco nem no mpv.
/// Uso: <c>var f = new Fila(); f.Iniciar(Fila.Ordenar(escolhidos)); f.Adicionar(mais); f.Mover(0, 3);</c>
/// </summary>
public sealed class Fila
{
    readonly List<string> _itens = new();

    public IReadOnlyList<string> Itens => _itens;
    /// <summary>O índice do que está tocando; -1 sem fila.</summary>
    public int Atual { get; private set; } = -1;
    public int Total => _itens.Count;
    public bool TemProximo => Atual >= 0 && Atual + 1 < _itens.Count;
    public bool TemAnterior => Atual > 0;
    /// <summary>Mais de um item: só então a fila aparece (um filme sozinho não tem fila).</summary>
    public bool Ativa => _itens.Count > 1;

    /// <summary>Começa uma fila nova (os caminhos já ordenados); o primeiro é o que toca.</summary>
    public void Iniciar(IEnumerable<string> caminhos)
    {
        _itens.Clear();
        foreach (var c in caminhos) if (!_itens.Contains(c, StringComparer.OrdinalIgnoreCase)) _itens.Add(c);
        Atual = _itens.Count > 0 ? 0 : -1;
    }

    /// <summary>Acrescenta ao fim, na ordem natural, sem repetir arquivo que já está na fila. Devolve quantos entraram.</summary>
    public int Adicionar(IEnumerable<string> caminhos)
    {
        int antes = _itens.Count;
        foreach (var c in Ordenar(caminhos)) if (!_itens.Contains(c, StringComparer.OrdinalIgnoreCase)) _itens.Add(c);
        if (Atual < 0 && _itens.Count > 0) Atual = 0;
        return _itens.Count - antes;
    }

    /// <summary>Tira um item que não está tocando (o que toca fica: sair dele é trocar de episódio).</summary>
    public bool Remover(int i)
    {
        if (i < 0 || i >= _itens.Count || i == Atual) return false;
        _itens.RemoveAt(i);
        if (i < Atual) Atual--;
        return true;
    }

    /// <summary>Leva o item <paramref name="de"/> para a posição <paramref name="para"/> (índice depois de tirá-lo); o que toca continua sendo o mesmo arquivo.</summary>
    public bool Mover(int de, int para)
    {
        if (de < 0 || de >= _itens.Count || para < 0 || para >= _itens.Count || de == para) return false;
        string item = _itens[de];
        _itens.RemoveAt(de);
        _itens.Insert(para, item);
        if (Atual == de) Atual = para;
        else if (de < Atual && para >= Atual) Atual--;
        else if (de > Atual && para <= Atual) Atual++;
        return true;
    }

    public bool Definir(int i)
    {
        if (i < 0 || i >= _itens.Count) return false;
        Atual = i;
        return true;
    }

    public void Limpar() { _itens.Clear(); Atual = -1; }

    /// <summary>Os nomes que a tela mostra: o nome do arquivo sem a extensão.</summary>
    public string[] Nomes => _itens.Select(c => Path.GetFileNameWithoutExtension(c)).ToArray();

    /// <summary>Ordem natural: "Ep 2" antes de "Ep 10", sem diferenciar maiúscula.</summary>
    public static List<string> Ordenar(IEnumerable<string> caminhos) =>
        caminhos.OrderBy(c => Path.GetFileName(c), Comparer<string>.Create(CompararNatural)).ToList();

    public static int CompararNatural(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int num = na.SequenceCompareTo(nb);
                if (num != 0) return num;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
