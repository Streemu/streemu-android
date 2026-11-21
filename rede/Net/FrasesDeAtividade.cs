namespace Streemu.Net;

/// <summary>Uma linha do bloco "Atividade recente" do Início: o texto, a hora relativa e de que tipo é (para o ícone e para os testes).</summary>
public sealed record LinhaDeAtividade(string Texto, string Quando, string Tipo, long Hora, string GrupoId = "");

/// <summary>
/// Transforma as atividades do servidor nas frases do Início (plano 019, etapa 9). Regra pura: sem tela, sem rede, sem relógio de verdade
/// (<c>agora</c> vem de fora). <c>live</c> e <c>live_fim</c> da mesma <c>sala</c> viram UMA linha ("X transmitiu Y por 42 min"; sem o fim, "X está ao vivo: Y").
/// Uso: <c>FrasesDeAtividade.Montar(sala.Atividades, (conta, apelido) =&gt; apelidos.Nome(conta, apelido), DateTimeOffset.Now)</c>.
/// </summary>
public static class FrasesDeAtividade
{
    public const int MaxLinhas = 20;
    public const string Vazio = "Nada por aqui ainda. O que o grupo fizer aparece aqui.";

    /// <param name="nome">(conta, apelido do grupo) → o nome para mostrar na tela (o apelido local, se houver).</param>
    /// <param name="meuId">A conta de quem está vendo: o que ela fez vira "Você ..." e, quando é o alvo, "... assistir você" (sem ele, o nome dela, como no relatório).</param>
    public static IReadOnlyList<LinhaDeAtividade> Montar(IEnumerable<Atividade> itens, Func<string, string, string> nome, DateTimeOffset agora, int max = MaxLinhas, string? meuId = null)
    {
        var lista = itens.OrderByDescending(a => a.Hora).ThenByDescending(a => a.Id).ToList();
        // a sala de cada live: o fim dela e o início dela viram uma linha só (chave: grupo + sala, porque dois grupos têm a mesma live)
        string Chave(Atividade a) => a.GrupoId + "|" + a.Campo("sala");
        var fins = lista.Where(a => a.Tipo == "live_fim" && a.Campo("sala") != null).GroupBy(Chave).ToDictionary(g => g.Key, g => g.First());
        var inicios = lista.Where(a => a.Tipo == "live" && a.Campo("sala") != null).GroupBy(Chave).ToDictionary(g => g.Key, g => g.First());
        var linhas = new List<LinhaDeAtividade>();
        foreach (var a in lista)
        {
            bool eu = meuId != null && a.Conta == meuId;
            string quem = eu ? "Você" : nome(a.Conta, a.Apelido);
            string alvo = meuId != null && a.Campo("alvo") == meuId ? "você" : nome(a.Campo("alvo") ?? "", a.Campo("alvo_apelido") ?? "alguém");
            LinhaDeAtividade? linha = a.Tipo switch
            {
                "live" => a.Campo("sala") is { } s && fins.ContainsKey(a.GrupoId + "|" + s)
                    ? null // o live_fim dessa sala fala por ela
                    : Linha(a, $"{quem} está ao vivo{(Jogo(a) is { Length: > 0 } j ? ": " + j : "")}", agora),
                "live_fim" => FimDaLive(a, quem, inicios, Chave(a), agora),
                "assistindo" => Linha(a, $"{quem} começou a assistir {alvo}", agora),
                "filme" => Linha(a, $"{quem} abriu um filme", agora),
                "entrou" => Linha(a, $"{quem} entrou no grupo", agora),
                "saiu" => Linha(a, $"{quem} saiu do grupo", agora),
                _ => null, // tipo que este app não conhece (servidor mais novo): ignora
            };
            if (linha != null) linhas.Add(linha);
            if (linhas.Count >= max) break;
        }
        return linhas;
    }

    static LinhaDeAtividade FimDaLive(Atividade fim, string quem, Dictionary<string, Atividade> inicios, string chave, DateTimeOffset agora)
    {
        string jogo = inicios.TryGetValue(chave, out var inicio) ? Jogo(inicio) : "";
        return Linha(fim, $"{quem} transmitiu{(jogo.Length > 0 ? " " + jogo : "")} por {Duracao(fim.Minutos)}", agora);
    }

    static string Jogo(Atividade a) => a.Campo("jogo")?.Trim() ?? "";

    static LinhaDeAtividade Linha(Atividade a, string texto, DateTimeOffset agora) => new(texto, Quando(a.Hora, agora), a.Tipo, a.Hora, a.GrupoId);

    /// <summary>"42 min", "1 h", "2 h 05 min".</summary>
    public static string Duracao(int minutos)
    {
        if (minutos < 1) return "menos de 1 min";
        if (minutos < 60) return $"{minutos} min";
        int h = minutos / 60, m = minutos % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m:00} min";
    }

    /// <summary>"agora", "há 5 min", "há 2 h", "ontem", "há 3 dias". Hora no futuro (relógio diferente do servidor) vira "agora".</summary>
    public static string Quando(long horaUnixMs, DateTimeOffset agora)
    {
        horaUnixMs = Streemu.Util.JsonSeguro.HoraUnixMs(horaUnixMs);
        if (horaUnixMs == 0) return "agora";
        var d = agora - DateTimeOffset.FromUnixTimeMilliseconds(horaUnixMs);
        if (d.TotalMinutes < 1) return "agora";
        if (d.TotalMinutes < 60) return $"há {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"há {(int)d.TotalHours} h";
        if (d.TotalDays < 2) return "ontem";
        return $"há {(int)d.TotalDays} dias";
    }
}
