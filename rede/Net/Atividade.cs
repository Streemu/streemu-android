using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>
/// Uma atividade recente do grupo, como o servidor da sala com conta manda (plano 019, etapa 9; relay/sala.js). Tipos: <c>live</c> {jogo, sala}, <c>live_fim</c> {sala, minutos},
/// <c>assistindo</c> {alvo, alvo_apelido}, <c>filme</c> {}, <c>entrou</c> {}, <c>saiu</c> {removido?}. <see cref="Hora"/> é em milissegundos Unix.
/// <see cref="GrupoId"/> é preenchido pela sala unida (o servidor de cada grupo não sabe o nome do outro). A sala antiga nunca tem atividades.
/// </summary>
public sealed record Atividade(long Id, long Hora, string Conta, string Apelido, string Tipo, JsonObject Dados, string GrupoId = "")
{
    /// <summary>Lê um item da mensagem; nulo se faltar o que não dá para inventar (conta, tipo, hora).</summary>
    public static Atividade? Ler(JsonNode? no, string grupoId = "")
    {
        if (no is not JsonObject o) return null;
        string? conta = Texto(o["conta"]), tipo = Texto(o["tipo"]);
        if (string.IsNullOrEmpty(conta) || string.IsNullOrEmpty(tipo)) return null;
        long hora = Numero(o["hora"]);
        if (hora <= 0) return null;
        return new Atividade(Numero(o["id"]), hora, conta, Texto(o["apelido"]) ?? "amigo", tipo, o["dados"] as JsonObject ?? new JsonObject(), grupoId);
    }

    public string? Campo(string nome) => Texto(Dados[nome]);
    public int Minutos => (int)Math.Clamp(Numero(Dados["minutos"]), 0, int.MaxValue);

    static string? Texto(JsonNode? n) { try { return n?.GetValue<string>(); } catch { return null; } }
    static long Numero(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<double>(out var d) ? (long)d : 0;
    }
}
