using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Proto.Filme;

/// <summary>
/// As sessões de Cinema anunciadas na sala do grupo, como o MainForm.Filme.cs do Windows as guarda: id do anfitrião → anúncio "filme_aberto"
/// (o anfitrião repete a cada 5 s; some depois de 20 s sem repetir ou com "filme_fechado"). O anúncio fica como veio, mais dois campos
/// que o app guarda ao receber: "de" (o id do anfitrião na sala) e "recebido".
/// Campos do anúncio: tipo, filme (a identidade do arquivo), nome (título), anfitriao (o nome dele), stream, estado (aguardo|rolando|pausado), pos, pessoas, quem, quem_ids, capa, fila.
/// </summary>
static class CinemaSala
{
    static readonly object Gate = new();
    static readonly Dictionary<string, (JsonObject Msg, DateTime Visto)> Abertos = new();
    static Timer? _limpeza;

    /// <summary>"filme_aberto" ou "filme_fechado" de um amigo (a sala já carimbou o <paramref name="de"/>).</summary>
    public static bool Receber(string de, string apelido, JsonObject msg)
    {
        string? tipo = JsonSeguro.Texto(msg["tipo"]);
        if (tipo == "filme_aberto")
        {
            // a mensagem vem do PC de outra pessoa: sem identidade no formato certo (ou da minha própria conta) não vira cartão
            if (de == Sessao.Conta.ContaId || !Streemu.Filme.Identidade.Valida(JsonSeguro.Texto(msg["filme"]))) return true;
            msg["de"] = de;
            msg["recebido"] = Environment.TickCount64;
            bool novo, sessaoNova;
            lock (Gate)
            {
                bool tinha = Abertos.TryGetValue(de, out var a);
                sessaoNova = !tinha || a.Msg["filme"]?.ToJsonString() != msg["filme"]?.ToJsonString();   // filme diferente ou depois de filme_fechado
                novo = sessaoNova || a.Msg["estado"]?.ToJsonString() != msg["estado"]?.ToJsonString();
                Abertos[de] = (msg, DateTime.Now); _limpeza ??= new Timer(_ => Limpar(), null, 5000, 5000);
            }
            if (sessaoNova) Log.Info($"[filme] {apelido} abriu \"{JsonSeguro.Texto(msg["nome"])}\".");   // o anúncio se repete a cada poucos segundos: só a sessão nova entra no log
            if (novo) Sessao.Avisar();
            return true;
        }
        if (tipo == "filme_fechado")
        {
            bool tirou;
            lock (Gate) tirou = Abertos.Remove(de);
            if (tirou) Sessao.Avisar();
            return true;
        }
        return false;
    }

    /// <summary>As sessões de agora, a mais nova primeiro.</summary>
    public static IReadOnlyList<JsonObject> Anuncios()
    {
        lock (Gate) return Abertos.Values.OrderByDescending(v => v.Visto).Select(v => v.Msg).ToList();
    }

    public static JsonObject? De(string anfitriao)
    {
        lock (Gate) return Abertos.TryGetValue(anfitriao, out var a) ? a.Msg : null;
    }

    /// <summary>A sala trocou (outro grupo, saiu da conta): nenhuma sessão do grupo antigo vale.</summary>
    public static void Zerar()
    {
        lock (Gate) Abertos.Clear();
    }

    static void Limpar()
    {
        bool mudou;
        lock (Gate)
        {
            var velhos = Abertos.Where(kv => DateTime.Now - kv.Value.Visto > TimeSpan.FromSeconds(20)).Select(kv => kv.Key).ToList();
            foreach (var k in velhos) Abertos.Remove(k);
            mudou = velhos.Count > 0;
        }
        if (mudou) Sessao.Avisar();
    }
}
