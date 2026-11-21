using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Junta numa linha só as entradas da MESMA pessoa que chegam da sala com ids diferentes: uma conexão fantasma que a sala ainda não fechou (o PC
/// suspendeu ou a rede caiu sem fechar o socket), uma reinstalação, dois usuários do Windows. A sala e o app já juntam o mesmo id; ids diferentes não
/// dava para juntar e o amigo aparecia duas ou três vezes na barra (um amigo 2x em 2026-11-18, outro 3x em 2026-11-08; docs/DESCOBERTAS.md).
/// A chave é o apelido (sem diferenciar maiúsculas): a conexão fantasma pode ser de uma versão anterior do app, então a versão não entra; fica a linha ao vivo e, senão, a mais nova (a última da lista, que a sala manda do mais velho para o mais novo).
/// Não junta quem não tem apelido de verdade ("amigo", o padrão) nem eu mesmo. Uso: <c>MesmaPessoa.Juntar(membros, meuId)</c>.
/// </summary>
public static class MesmaPessoa
{
    const string ApelidoPadrao = "amigo";

    /// <summary>A lista sem as repetições da mesma pessoa, na ordem original das linhas que ficaram.</summary>
    public static IReadOnlyList<Member> Juntar(IReadOnlyList<Member> membros, string meuId)
    {
        var grupos = membros.Where(m => m.Id != meuId && PodeJuntar(m)).GroupBy(Chave).Where(g => g.Select(m => m.Id).Distinct().Count() > 1).ToList();
        if (grupos.Count == 0) return membros;
        var descartados = new HashSet<string>();
        foreach (var g in grupos)
        {
            var fica = g.LastOrDefault(m => m.Live != null) ?? g.Last();
            foreach (var m in g.Where(m => m.Id != fica.Id)) descartados.Add(m.Id);
            AvisarUmaVez(g.Key, g.Select(m => m.Id).ToList());
        }
        return membros.Where(m => !descartados.Contains(m.Id)).ToList();
    }

    static bool PodeJuntar(Member m) => m.Nick.Trim().Length > 0 && !m.Nick.Trim().Equals(ApelidoPadrao, StringComparison.OrdinalIgnoreCase);

    static string Chave(Member m) => m.Nick.Trim().ToLowerInvariant();

    static readonly HashSet<string> JaAvisados = new();

    /// <summary>Uma linha no log por pessoa repetida (e por conjunto de ids), para descobrir a causa depois sem encher o arquivo a cada segundo.</summary>
    static void AvisarUmaVez(string chave, List<string> ids)
    {
        string marca = chave + "|" + string.Join(",", ids.OrderBy(i => i, StringComparer.Ordinal));
        string apelido = chave;
        lock (JaAvisados) { if (!JaAvisados.Add(marca)) return; }
        Log.Info($"Sala: \"{apelido}\" chegou {ids.Count}x com ids diferentes ({string.Join(", ", ids.Select(Curto))}); mostro uma só.");
    }

    static string Curto(string id) => id.Length <= 8 ? id : id[..8] + "…";
}
