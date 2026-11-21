using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Relay de reserva (TURN da Cloudflare). O app não guarda a chave da Cloudflare: ele pede credenciais
/// temporárias a um Worker do grupo (pasta relay/ do repositório), que só responde a quem tem conta Streemu (spec 022: a senha do grupo acabou).
/// O endereço do Worker vem de grupo.json (ao lado do .exe ou numa pasta acima dele, campo "relayUrl") ou do que foi salvo no app.
/// Um campo "senha" que sobrar em grupo.json ou no estado.json antigo é ignorado, sem aviso.
/// </summary>
public static class RelayConfig
{
    /// <summary>
    /// Endereço do Worker do grupo, embutido no app. Depois da spec 022 a URL deixou de ser segredo (a senha do grupo acabou, o Worker só atende quem tem
    /// conta), o botão Assistir do Discord já a mostra, e o instalador do site vem sem grupo.json: quem o baixa precisa achar o servidor mesmo assim.
    /// </summary>
    public const string UrlPadrao = "https://streemu-relay.streemu.workers.dev";

    /// <summary>
    /// Teste automático (STREEMU_TEST_DIR ou --local): NUNCA cai na <see cref="UrlPadrao"/>, nem lê grupo.json ou o estado (um grupo.json de desenvolvimento na raiz do
    /// repositório apontaria para a produção). Só vale uma variável de teste (STREEMU_ROOM_URL ou STREEMU_ATUALIZACAO_URL); sem ela <see cref="Load"/> devolve nulo
    /// e quem chama fica sem servidor. MainForm liga isto também para --local.
    /// </summary>
    public static bool ModoTeste { get; set; } = Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") != null;

    public sealed record Settings(string Url, string Source);
    public sealed record Credentials(IPEndPoint Server, string Username, string Password);

    /// <summary>grupo.json ao lado do .exe ou em até 6 pastas acima (ex.: um arquivo só na raiz da pasta Streemu).</summary>
    public static string? FindGroupFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "grupo.json");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Ordem: variável de teste (só em teste) → "relayUrl" do grupo.json (serve para apontar outro servidor) → o que foi salvo no app → <see cref="UrlPadrao"/>.
    /// Fora de teste nunca devolve nulo. <paramref name="urlSalva"/> é o endereço salvo no app (no Windows, <c>AppState.RelayUrl</c>).</summary>
    public static Settings? Load(string? urlSalva)
    {
        if (ModoTeste)
        {
            string? t = Environment.GetEnvironmentVariable("STREEMU_ROOM_URL") ?? Environment.GetEnvironmentVariable("STREEMU_ATUALIZACAO_URL");
            return string.IsNullOrWhiteSpace(t) ? null : new(t.Trim(), "variável de teste");
        }
        var file = FindGroupFile();
        if (file != null)
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(file));
                string? url = j?["relayUrl"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(url)) return new(url.Trim(), file);
                Log.Info($"{file} sem \"relayUrl\".");
            }
            catch (Exception e) { Log.Info($"{file} inválido: {e.Message}"); }
        }
        if (!string.IsNullOrWhiteSpace(urlSalva)) return new(urlSalva.Trim(), "configuração salva no app");
        return new(UrlPadrao, "endereço padrão do Streemu");
    }

    /// <summary>Dá o token de acesso da conta (ou nulo sem conta). Posto por MainForm.Conta.cs quando há sessão.</summary>
    public static Func<Task<string?>>? Token { get; set; }

    public const string TextoSemConta = "Entre na sua conta para usar o relay de reserva.";

    /// <summary>Manda a requisição com o token da conta. Sem conta não há pedido: o servidor só atende quem entrou (spec 022).
    /// <paramref name="criar"/> monta a requisição de novo a cada chamada (uma HttpRequestMessage não se reenvia).</summary>
    public static async Task<HttpResponseMessage> EnviarAsync(HttpClient http, Func<HttpRequestMessage> criar, CancellationToken ct = default)
    {
        string? token = null;
        try { if (Token != null) token = await Token(); } catch { }
        if (token == null) throw new InvalidOperationException(TextoSemConta);
        var r = criar();
        r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await http.SendAsync(r, ct);
    }

    /// <summary>O texto que o usuário lê quando o /turn não responde 200. O servidor manda `texto` em português no JSON quando sabe o motivo (cota do grupo,
    /// relay pausado, ritmo, conta sem grupo; spec 024): esse texto vale mais que qualquer um genérico daqui. Sem ele, os textos de antes.</summary>
    public static string TextoDoErro(HttpStatusCode status, string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["texto"] is JsonValue v && v.TryGetValue<string>(out var texto) && !string.IsNullOrWhiteSpace(texto)) return texto;
        }
        catch { /* corpo que não é JSON: cai nos textos de antes */ }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return "o servidor do grupo não aceitou sua conta para o relay. Entre de novo ou confira se você está em um grupo";
        return $"o servidor do grupo respondeu {(int)status}: {body}";
    }

    public static async Task<Credentials> FetchAsync(Settings s)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        HttpResponseMessage resp;
        try { resp = await EnviarAsync(http, () => new HttpRequestMessage(HttpMethod.Get, s.Url.TrimEnd('/') + "/turn") ); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com o servidor do grupo ({s.Url}): {e.Message}"); }
        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(TextoDoErro(resp.StatusCode, body));
            var j = JsonNode.Parse(body) ?? throw new InvalidOperationException("resposta vazia");
            string user = j["username"]?.GetValue<string>() ?? throw new InvalidOperationException("o servidor do grupo não devolveu credencial (confira os segredos do Worker)");
            string pass = j["credential"]?.GetValue<string>() ?? throw new InvalidOperationException("sem credential");
            var urls = j["urls"]?.AsArray().Select(u => u?.GetValue<string>() ?? "").ToList() ?? new List<string>();
            // prefere turn: com UDP (o túnel é UDP); a Cloudflare usa turn.cloudflare.com:3478
            string pick = urls.FirstOrDefault(u => u.StartsWith("turn:") && u.Contains("udp"))
                          ?? urls.FirstOrDefault(u => u.StartsWith("turn:") && !u.Contains("transport=tcp"))
                          ?? "turn:turn.cloudflare.com:3478";
            var hostPort = pick[5..].Split('?')[0];
            var parts = hostPort.Split(':');
            int port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;
            var addr = (await Dns.GetHostAddressesAsync(parts[0])).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            return new Credentials(new IPEndPoint(addr, port), user, pass);
        }
    }
}
