using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Resposta de uma operação de grupo. <see cref="Texto"/> é uma frase em português pronta para mostrar (só quando falhou);
/// <see cref="Convite"/> e <see cref="Papel"/> só vêm de <c>InfoDoGrupoAsync</c> (e o convite também de criar).
/// </summary>
public sealed record ResultadoGrupo(bool Ok, string? Erro = null, string? Texto = null, string? Id = null, string? Nome = null, string? Convite = null, string? Papel = null);

/// <summary>Papel e tamanho de um grupo da conta, do <c>GET /conta</c>. <see cref="Membros"/> nulo se o servidor não mandou.</summary>
public sealed record DetalheDoGrupo(bool Dono, int? Membros);

/// <summary>
/// Operações de grupo da conta no celular (as mesmas do app Windows, <c>ContaGrupos.cs</c>): criar, entrar por convite, sair e ver o convite.
/// Nenhum método daqui lança: falha de rede, disco ou resposta estranha do servidor vira <see cref="ResultadoGrupo"/> com uma frase.
/// Depois de criar, entrar ou sair o token é renovado na hora, porque é o /conta/renovar que traz a lista de grupos atualizada.
/// </summary>
public sealed partial class ContaLeve
{
    public const string TextoSemRede = "Sem conexão com o servidor. Confira a internet e tente de novo.";
    public const string FraseSessaoEncerrada = "Sua sessão foi encerrada. Entre de novo.";
    const string TextoGenerico = "Não deu certo. Tente de novo em instantes.";

    /// <summary>Alfabeto do convite (o mesmo do servidor): sem I, L, O, 0 e 1, que se confundem na leitura.</summary>
    const string AlfabetoConvite = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    string? _site;

    /// <summary>Código do servidor (<c>erro</c>) para frase da pessoa. O texto do servidor serve de reserva. As frases são as do app Windows.</summary>
    public static string FraseDeGrupo(string? erro, string? textoServidor = null) => erro switch
    {
        "convite_invalido" => "Esse convite não vale mais. Peça um novo a quem te convidou.",
        "grupo_cheio" => "Esse grupo já está cheio. Peça ao dono para abrir espaço.",
        "muitos_grupos" => "Você já está no máximo de grupos. Saia de um para entrar em outro.",
        "muitas_tentativas" => "Muitos convites errados em pouco tempo. Espere uns 10 minutos e tente de novo.",
        "nao_membro" => "Você não está mais neste grupo. A lista vai ser atualizada.",
        "so_dono" => "Só quem criou o grupo faz isso.",
        "token_recusado" or "conta_invalida" => FraseSessaoEncerrada,
        "requisicao_invalida" => string.IsNullOrWhiteSpace(textoServidor) ? "O servidor não entendeu o pedido. Confira o que digitou e tente de novo." : textoServidor,
        "servidor_ocupado" => "O servidor está ocupado agora. Tente de novo em instantes.",
        "nao_encontrado" => "Esse grupo não existe mais. A lista vai ser atualizada.",
        _ => string.IsNullOrWhiteSpace(textoServidor) ? TextoGenerico : textoServidor,
    };

    // ------------------------------------------------------------------ convite: texto livre -> código

    // um link de convite dentro de um texto maior (a mensagem que o amigo mandou): streemu://grupo/<código> ou https://<site>/g/<código>
    static readonly Regex _linkDeConvite = new(@"(?:streemu://grupo/|https://[^\s/]+/g/)([A-Za-z0-9-]{16,24})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// O código de convite (16 letras e números, maiúsculo, sem hífens) de um texto colado ou de um link, ou null.
    /// Aceita o código puro (com ou sem hífens, em qualquer caixa), https://www.streemu.com.br/g/&lt;código&gt;, streemu://grupo/&lt;código&gt;
    /// e uma mensagem inteira que traga um desses links.
    /// </summary>
    public static string? NormalizarConvite(string? texto)
    {
        var t = (texto ?? "").Trim();
        if (t.Length == 0 || t.Length > 2000) return null;
        foreach (Match m in _linkDeConvite.Matches(t))
            if (CodigoValido(m.Groups[1].Value) is { } c) return c;
        return CodigoValido(t);
    }

    static string? CodigoValido(string bruto)
    {
        var limpo = new string(bruto.Where(ch => ch != '-' && !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return limpo.Length == 16 && limpo.All(ch => AlfabetoConvite.Contains(ch)) ? limpo : null;
    }

    /// <summary>Os 8 primeiros caracteres do convite são o id do grupo.</summary>
    public static string IdDoConvite(string codigo) => codigo[..8];

    /// <summary>O link para mandar a um amigo: <c>{site}/g/{código}</c>. O convite pode vir com hífens (XXXX-XXXX-XXXX-XXXX).</summary>
    public static string LinkDoConvite(string site, string convite) => $"{site.TrimEnd('/')}/g/{CodigoValido(convite) ?? convite}";

    /// <summary>
    /// O endereço do site (<c>site</c> do /conta/config, só origem https), onde os links de convite abrem. Guardado depois da primeira resposta.
    /// Sem resposta, sem o campo ou com algo que não é uma origem https, usa o servidor da conta (sem guardar quando foi falha de rede).
    /// </summary>
    public async Task<string?> SiteAsync()
    {
        if (_site != null) return _site;
        var servidor = Servidor;
        if (servidor == null) return null;
        try
        {
            var (st, j) = await EnviarAsync(HttpMethod.Get, servidor + "/conta/config", null, null);
            if (st != HttpStatusCode.OK) return servidor;
            var site = JsonSeguro.Texto(j?["site"]);
            if (site != null && Uri.TryCreate(site.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
                && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0 && u.UserInfo.Length == 0)
                return _site = u.GetLeftPart(UriPartial.Authority);
            return _site = servidor;
        }
        catch (Exception) { return servidor; }
    }

    // ------------------------------------------------------------------ chamadas

    /// <summary>
    /// GET ou POST com o token da conta. Um 401 renova o token e repete uma vez; <c>conta_invalida</c> apaga a conta.
    /// Quando não há resposta utilizável, <c>Texto</c> traz a frase (sem conta, sessão encerrada ou sem rede).
    /// </summary>
    async Task<(HttpStatusCode Status, JsonObject? Json, string? Texto)> ChamarComTokenAsync(HttpMethod metodo, string caminho, JsonObject? corpo)
    {
        bool tinhaConta = TemConta;
        var servidor = Servidor;
        (HttpStatusCode Status, JsonObject? Json) r = default;
        for (int tentativa = 0; tentativa < 2; tentativa++)
        {
            var token = await AcessoAsync(forcar: tentativa == 1);
            if (token == null || servidor == null)
                return (0, null, TemConta ? TextoSemRede : tinhaConta ? FraseSessaoEncerrada : "Entre na sua conta primeiro.");
            r = await EnviarAsync(metodo, servidor + caminho, corpo, token);
            if (r.Status == 0) return (0, null, TextoSemRede);
            if (r.Status != HttpStatusCode.Unauthorized) break;
        }
        if (JsonSeguro.Texto(r.Json?["erro"]) == "conta_invalida") { Apagar(); return (0, null, FraseSessaoEncerrada); }
        return (r.Status, r.Json, null);
    }

    /// <summary>Uma chamada HTTP. Status 0 = sem resposta (rede, tempo esgotado ou endereço ruim); nunca lança.</summary>
    async Task<(HttpStatusCode Status, JsonObject? Json)> EnviarAsync(HttpMethod metodo, string url, JsonObject? corpo, string? token)
    {
        try
        {
            using var req = new HttpRequestMessage(metodo, url);
            if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (corpo != null) req.Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var texto = await resp.Content.ReadAsStringAsync();
            JsonObject? j = null;
            try { j = JsonNode.Parse(texto) as JsonObject; } catch { }
            return (resp.StatusCode, j);
        }
        catch (Exception) { return (0, null); }
    }

    /// <summary>Falha do servidor (ou sessão encerrada) como resultado. Se o grupo sumiu para esta conta, a lista local é refeita.</summary>
    async Task<ResultadoGrupo> FalhaAsync(JsonObject? json, string? textoDeConta = null)
    {
        if (textoDeConta != null) return new(false, "sem_conta", textoDeConta);
        var erro = JsonSeguro.Texto(json?["erro"]) ?? "erro";
        if (erro is "nao_membro" or "nao_encontrado") await AtualizarGruposAsync();
        return new(false, erro, FraseDeGrupo(erro, JsonSeguro.Texto(json?["texto"])));
    }

    /// <summary>Depois de criar, entrar ou sair: renova o token, e o /conta/renovar traz a lista de grupos que <c>Guardar</c> guarda em <see cref="Grupos"/>.</summary>
    async Task AtualizarGruposAsync()
    {
        try { await AcessoAsync(forcar: true); }
        catch (Exception e) { Log.Info("[conta] atualizar grupos: " + e.GetType().Name); }
    }

    static ResultadoGrupo FalhaInesperada(Exception e)
    {
        Log.Info("[conta] grupo: " + e.GetType().Name + ": " + e.Message);
        return new(false, "erro", TextoGenerico);
    }

    // ------------------------------------------------------------------ nome da conta (spec 027)

    /// <summary>Tamanho máximo do nome da conta (o mesmo do servidor).</summary>
    public const int NomeMaximo = 24;

    /// <summary>
    /// Troca o nome da conta (<c>POST /conta/nome</c>), que vale em todos os aparelhos e grupos. Ok = o servidor guardou;
    /// senão <c>Texto</c> é a frase para mostrar (a do servidor, quando ele manda). Não lança. Um 401 renova o token e tenta mais uma vez.
    /// </summary>
    public async Task<(bool Ok, string? Texto)> MudarNomeAsync(string nome)
    {
        try
        {
            nome = string.Join(' ', (nome ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (nome.Length == 0 || nome.Length > NomeMaximo) return (false, "O nome precisa ter de 1 a 24 letras.");
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Post, "/conta/nome", new JsonObject { ["nome"] = nome });
            if (texto != null) return (false, texto);
            if (st == HttpStatusCode.OK)
            {
                var novo = JsonSeguro.Texto(j?["nome"]) is { Length: > 0 } n ? n : nome;
                bool mudou = novo != Nome;
                Nome = novo; NomeConhecido = true;
                try { Persistir(); } catch (Exception e) { Log.Info("[conta] guardar nome: " + e.GetType().Name); }
                if (mudou) NomeMudou?.Invoke();
                return (true, null);
            }
            var erro = JsonSeguro.Texto(j?["erro"]);
            var doServidor = JsonSeguro.Texto(j?["texto"]);
            return (false, erro switch
            {
                "nome_invalido" => string.IsNullOrWhiteSpace(doServidor) ? "Esse nome não vale. Use de 1 a 24 letras." : doServidor,
                "muitas_trocas" => string.IsNullOrWhiteSpace(doServidor) ? "Você já trocou o nome muitas vezes hoje. Tente amanhã." : doServidor,
                _ => FraseDeGrupo(erro, doServidor),
            });
        }
        catch (Exception e) { Log.Info("[conta] mudar nome: " + e.GetType().Name + ": " + e.Message); return (false, TextoGenerico); }
    }

    /// <summary>A sala difundiu outro nome para a minha conta (troca feita em outro aparelho): guarda e avisa. Só vale com a conta já nomeada.</summary>
    public void AdotarNomeDaSala(string nome)
    {
        nome = (nome ?? "").Trim();
        if (nome.Length == 0 || Nome == null || nome == Nome || !TemConta) return;
        Nome = nome;
        try { Persistir(); } catch (Exception e) { Log.Info("[conta] guardar nome: " + e.GetType().Name); }
        NomeMudou?.Invoke();
    }

    // ------------------------------------------------------------------ operações

    public async Task<ResultadoGrupo> CriarGrupoAsync(string nome, string apelido)
    {
        try
        {
            nome = (nome ?? "").Trim();
            if (nome.Length == 0) return new(false, "requisicao_invalida", "Dê um nome ao grupo.");
            if (nome.Length > 40) nome = nome[..40].TrimEnd();
            apelido = (apelido ?? "").Trim();
            if (apelido.Length > 24) apelido = apelido[..24];
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Post, "/grupos", new JsonObject { ["nome"] = nome, ["apelido"] = apelido });
            if (texto != null) return await FalhaAsync(null, texto);
            if (st != HttpStatusCode.OK || JsonSeguro.Texto(j?["id"]) is not { Length: > 0 } id) return await FalhaAsync(j);
            await AtualizarGruposAsync();
            return new(true, Id: id, Nome: JsonSeguro.Texto(j?["nome"]) ?? nome, Convite: JsonSeguro.Texto(j?["convite"]));
        }
        catch (Exception e) { return FalhaInesperada(e); }
    }

    /// <summary>Entra por convite. <paramref name="convite"/> pode ter hífens; quem tem texto livre passa antes por <see cref="NormalizarConvite"/>.</summary>
    public async Task<ResultadoGrupo> EntrarNoGrupoAsync(string convite, string apelido)
    {
        try
        {
            apelido = (apelido ?? "").Trim();
            if (apelido.Length > 24) apelido = apelido[..24];
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Post, "/grupos/entrar", new JsonObject { ["convite"] = convite, ["apelido"] = apelido });
            if (texto != null) return await FalhaAsync(null, texto);
            if (st != HttpStatusCode.OK || JsonSeguro.Texto(j?["id"]) is not { Length: > 0 } id) return await FalhaAsync(j);
            await AtualizarGruposAsync();
            return new(true, Id: id, Nome: JsonSeguro.Texto(j?["nome"]));
        }
        catch (Exception e) { return FalhaInesperada(e); }
    }

    public async Task<ResultadoGrupo> SairDoGrupoAsync(string id)
    {
        try
        {
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Post, "/grupos/" + Uri.EscapeDataString(id) + "/sair", new JsonObject());
            if (texto != null) return await FalhaAsync(null, texto);
            if (st != HttpStatusCode.OK) return await FalhaAsync(j);
            await AtualizarGruposAsync();
            return new(true, Id: id);
        }
        catch (Exception e) { return FalhaInesperada(e); }
    }

    /// <summary>Nome, papel (<c>dono</c> ou <c>membro</c>) e convite do grupo.</summary>
    public async Task<ResultadoGrupo> InfoDoGrupoAsync(string id)
    {
        try
        {
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Get, "/grupos/" + Uri.EscapeDataString(id), null);
            if (texto != null) return await FalhaAsync(null, texto);
            if (st != HttpStatusCode.OK || j == null) return await FalhaAsync(j);
            return new(true, Id: JsonSeguro.Texto(j["id"]) ?? id, Nome: JsonSeguro.Texto(j["nome"]) ?? "", Papel: JsonSeguro.Texto(j["papel"]) ?? "membro",
                Convite: JsonSeguro.Texto(j["convite"]));
        }
        catch (Exception e) { return FalhaInesperada(e); }
    }

    /// <summary>
    /// O papel e o tamanho de cada grupo da conta, pelo <c>GET /conta</c> (o <c>/conta/renovar</c> só traz id e nome). O servidor pergunta a cada
    /// sala, então é mais lento que a lista. Nulo se falhou: quem mostra fica só com os nomes. Ex.: <c>var d = await conta.DetalhesDosGruposAsync();</c>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DetalheDoGrupo>?> DetalhesDosGruposAsync()
    {
        try
        {
            var (st, j, texto) = await ChamarComTokenAsync(HttpMethod.Get, "/conta", null);
            if (texto != null || st != HttpStatusCode.OK || j?["grupos"] is not JsonArray gs) return null;
            var r = new Dictionary<string, DetalheDoGrupo>();
            foreach (var g in gs.OfType<JsonObject>())
                if (JsonSeguro.Texto(g["id"]) is { } id)
                    r[id] = new DetalheDoGrupo(JsonSeguro.Texto(g["papel"]) == "dono", JsonSeguro.Inteiro(g["membros"]) is { } n and >= 0 and <= 1000 ? (int)n : null);
            return r;
        }
        catch (Exception e) { Log.Info("[conta] detalhes dos grupos: " + e.GetType().Name); return null; }
    }
}
