using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Filme;

namespace Streemu.Arquivos;

/// <summary>Um arquivo do pedido: o nome que a pessoa vê, o tamanho e a identidade (<see cref="Identidade"/>), que diz qual arquivo é.</summary>
public sealed record ArquivoOferecido(string Nome, long Tamanho, string Identidade);

/// <summary>O que quem manda oferece (spec 020): um pedido com até <see cref="MensagemArquivo.MaxArquivos"/> arquivos, aceitos ou recusados juntos (D2).</summary>
public sealed record OfertaDeArquivos(string Pedido, IReadOnlyList<ArquivoOferecido> Arquivos)
{
    public long Total => Arquivos.Sum(a => a.Tamanho);
}

/// <summary>
/// As mensagens do envio de arquivo pela sala do grupo (spec 020). Tudo o que chega vem de outro PC: cada leitura confere o formato e
/// devolve nulo se algo não bate, em vez de confiar. A ligação segue o molde do Cinema invertido: quem RECEBE aceita mandando o código
/// da ligação dele (<see cref="Aceito"/>) e quem manda responde com o seu (<see cref="Ligar"/>). O <see cref="Visto"/> sai na hora em
/// que a oferta chega: sem ele em 10 s, quem manda sabe que o amigo está numa versão sem o envio de arquivo (o Worker não repassa
/// campos novos da lista de membros, `relay/worker.js:173`).
/// </summary>
public static class MensagemArquivo
{
    public const string Oferta = "arquivo_oferta", Visto = "arquivo_visto", Aceito = "arquivo_aceito", Ligar = "arquivo_ligar";
    public const string Recusado = "arquivo_recusado", Cancelado = "arquivo_cancelado", DeNovo = "arquivo_de_novo";
    /// <summary>Quem recebe avisa que todos os arquivos chegaram conferidos: sem isso, quem manda não sabe se acabou ou se o app fechou.</summary>
    public const string Concluido = "arquivo_concluido";
    public const int MaxArquivos = 20, MaxNome = 255;

    static readonly Regex IdDoPedido = new("^[A-Za-z0-9]{8,24}$");

    /// <summary>Ex.: <c>MensagemArquivo.CriarOferta(new("a1b2c3d4e5f6", [new("mumu.png", 4_200_000, id)]))</c>.</summary>
    public static JsonObject CriarOferta(OfertaDeArquivos oferta) => new()
    {
        ["tipo"] = Oferta,
        ["pedido"] = oferta.Pedido,
        ["arquivos"] = new JsonArray(oferta.Arquivos.Select(a => (JsonNode)new JsonObject
        {
            ["nome"] = a.Nome, ["tamanho"] = a.Tamanho, ["id"] = a.Identidade,
        }).ToArray()),
    };

    /// <summary>A oferta que chegou, ou nulo se não estiver no formato (fora dos limites, identidade que não bate com o tamanho).</summary>
    public static OfertaDeArquivos? LerOferta(JsonObject m)
    {
        if (Tipo(m) != Oferta || PedidoDe(m) is not string pedido || m["arquivos"] is not JsonArray lista) return null;
        if (lista.Count == 0 || lista.Count > MaxArquivos) return null;
        var arquivos = new List<ArquivoOferecido>();
        foreach (var item in lista)
        {
            if (LerArquivo(item) is not ArquivoOferecido a) return null;
            arquivos.Add(a);
        }
        return new OfertaDeArquivos(pedido, arquivos);
    }

    static ArquivoOferecido? LerArquivo(JsonNode? item)
    {
        if (item is not JsonObject o || Texto(o, "nome") is not string nome || Texto(o, "id") is not string id) return null;
        if (nome.Trim().Length == 0 || nome.Length > MaxNome || !Identidade.Valida(id)) return null;
        long tamanho = Numero(o, "tamanho");
        return tamanho > 0 && Identidade.Tamanho(id) == tamanho ? new ArquivoOferecido(nome, tamanho, id) : null;
    }

    /// <summary>Mensagem curta de um pedido (visto, recusado, cancelado). Ex.: <c>Simples(MensagemArquivo.Visto, "a1b2c3d4")</c>.</summary>
    public static JsonObject Simples(string tipo, string pedido, string? motivo = null)
    {
        var m = new JsonObject { ["tipo"] = tipo, ["pedido"] = pedido };
        if (motivo != null) m["motivo"] = motivo;
        return m;
    }

    /// <summary>Aceite ou resposta com o código da ligação (<see cref="Aceito"/> ou <see cref="Ligar"/>).</summary>
    public static JsonObject ComCodigo(string tipo, string pedido, string codigo) =>
        new() { ["tipo"] = tipo, ["pedido"] = pedido, ["codigo"] = codigo };

    /// <summary>
    /// "Pedir de novo" pelo histórico: quem recebeu pede o mesmo arquivo (D6), dito pela identidade. Leva um número de pedido novo:
    /// quem manda responde com uma oferta desse número (ou uma recusa), e quem pediu aceita essa oferta sem perguntar de novo.
    /// </summary>
    public static JsonObject CriarDeNovo(string pedido, ArquivoOferecido arquivo) => new()
    {
        ["tipo"] = DeNovo, ["pedido"] = pedido, ["nome"] = arquivo.Nome, ["tamanho"] = arquivo.Tamanho, ["id"] = arquivo.Identidade,
    };

    public static (string Pedido, ArquivoOferecido Arquivo)? LerDeNovo(JsonObject m) =>
        Tipo(m) == DeNovo && PedidoDe(m) is string p && LerArquivo(m) is ArquivoOferecido a ? (p, a) : null;

    /// <summary>O id do pedido, se estiver no formato; nulo se não (não vira chave de nada).</summary>
    public static string? PedidoDe(JsonObject m) => Texto(m, "pedido") is string p && IdDoPedido.IsMatch(p) ? p : null;

    public static string? CodigoDe(JsonObject m) => Texto(m, "codigo") is { Length: > 0 and < 2048 } c ? c : null;

    /// <summary>O motivo de uma recusa ou cancelamento, curto (aparece na tela de quem recebe).</summary>
    public static string? MotivoDe(JsonObject m) => Texto(m, "motivo") is string t ? (t.Length > 200 ? t[..200] : t) : null;

    static string? Tipo(JsonObject m) => Texto(m, "tipo");

    static string? Texto(JsonObject o, string campo) =>
        o[campo] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    static long Numero(JsonObject o, string campo) =>
        o[campo] is JsonValue v && v.TryGetValue(out long n) ? n : -1;
}
