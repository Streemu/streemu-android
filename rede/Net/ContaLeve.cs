using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Streemu.Net;

public sealed record GrupoDaConta(string Id, string Nome);

/// <summary>
/// Conta Streemu do protótipo (o celular e o proto-envio): o mesmo login do app Windows (spec 019), sem tela e sem o <c>AppState</c> dele.
/// Entrar: <see cref="IniciarAsync"/> dá o endereço da página, a pessoa entra no navegador e digita aqui o código de 6 dígitos (<see cref="RetirarAsync"/>).
/// Cada um é um dispositivo próprio da conta (<paramref name="apelido"/>). Quem chama decide como cifrar o segredo: DPAPI no Windows, Android Keystore no celular.
/// O arquivo guarda servidor, conta, dispositivo e o segredo cifrado. Nenhum texto de erro leva segredo, verificador nem código.
/// </summary>
public sealed class ContaLeve
{
    readonly string _arquivo, _apelido;
    readonly Func<byte[], byte[]> _cifrar, _abrir;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly SemaphoreSlim _renovando = new(1, 1);
    readonly object _gate = new();
    string? _pedido, _verificador, _segredoNovo, _servidorNovo;
    string? _acesso; long _acessoExpira;

    public string? Servidor { get; private set; }
    public string? ContaId { get; private set; }
    string? _dispositivo, _segredoCifrado;
    public IReadOnlyList<GrupoDaConta> Grupos { get; private set; } = Array.Empty<GrupoDaConta>();
    public bool TemConta => _dispositivo != null && _segredoCifrado != null;

    public ContaLeve(string arquivo, string apelido, Func<byte[], byte[]> cifrar, Func<byte[], byte[]> abrir)
    {
        _arquivo = arquivo; _apelido = apelido; _cifrar = cifrar; _abrir = abrir;
        Ler();
    }

    /// <summary>Começa o login e devolve o endereço da página de entrada (quem chama abre o navegador). Nulo e o motivo se o servidor não aceitou.</summary>
    public async Task<(string? Url, string? Erro)> IniciarAsync(string servidor)
    {
        servidor = servidor.TrimEnd('/');
        string verificador = B64(RandomNumberGenerator.GetBytes(32)), segredo = B64(RandomNumberGenerator.GetBytes(32));
        string desafio = B64(SHA256.HashData(Encoding.UTF8.GetBytes(verificador)));
        var (st, j) = await Chamar(servidor, "/conta/login/iniciar", new JsonObject { ["desafio"] = desafio, ["acao"] = "entrar" });
        if (st != HttpStatusCode.OK || j?["pedido"]?.GetValue<string>() is not string pedido)
            return (null, $"o servidor não aceitou começar o login ({(int)st} {Texto(j)})");
        lock (_gate) { _pedido = pedido; _verificador = verificador; _segredoNovo = segredo; _servidorNovo = servidor; }
        return ($"{servidor}/entrar?pedido={Uri.EscapeDataString(pedido)}", null);
    }

    /// <summary>Termina o login com o código que a página mostrou. Ok grava a sessão.</summary>
    public async Task<(bool Ok, string? Erro)> RetirarAsync(string codigo)
    {
        string? pedido, verificador, segredo, servidor;
        lock (_gate) { pedido = _pedido; verificador = _verificador; segredo = _segredoNovo; servidor = _servidorNovo; }
        if (pedido == null || verificador == null || segredo == null || servidor == null) return (false, "não há login em andamento: comece de novo");
        var limpo = new string(codigo.Where(char.IsDigit).ToArray());
        if (limpo.Length != 6) return (false, "o código tem 6 dígitos");
        var (st, j) = await Chamar(servidor, "/conta/login/retirar", new JsonObject
        {
            ["pedido"] = pedido, ["verificador"] = verificador, ["codigo"] = limpo, ["segredo"] = segredo, ["apelido"] = _apelido,
        });
        if (st != HttpStatusCode.OK || j?["dispositivo"]?.GetValue<string>() is not string disp)
        {
            var erro = j?["erro"]?.GetValue<string>();
            if (erro is "pedido_vencido" or "pedido_usado" or "muitas_tentativas") lock (_gate) { _pedido = null; _verificador = null; }
            return (false, Texto(j) ?? ((int)st).ToString());
        }
        lock (_gate) { _pedido = null; _verificador = null; _segredoNovo = null; }
        Servidor = servidor; _dispositivo = disp;
        _segredoCifrado = Convert.ToBase64String(_cifrar(Encoding.UTF8.GetBytes(segredo)));
        Guardar(j);
        return (true, null);
    }

    /// <summary>Token de acesso, renovando quando faltam menos de 10 minutos (ou sempre, com <paramref name="forcar"/>, depois de um 401). Nulo sem conta ou se não renovou.</summary>
    public async Task<string?> AcessoAsync(bool forcar = false)
    {
        if (!TemConta) return null;
        lock (_gate) if (!forcar && _acesso != null && _acessoExpira - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 600) return _acesso;
        await _renovando.WaitAsync();
        try
        {
            string segredo;
            try { segredo = Encoding.UTF8.GetString(_abrir(Convert.FromBase64String(_segredoCifrado!))); }
            catch { Apagar(); return null; }
            var (st, j) = await Chamar(Servidor!, "/conta/renovar", new JsonObject { ["dispositivo"] = _dispositivo, ["segredo"] = segredo, ["apelido"] = _apelido });
            if (st == HttpStatusCode.OK && j?["acesso"] != null) { Guardar(j); lock (_gate) return _acesso; }
            if (j?["erro"]?.GetValue<string>() == "conta_invalida") Apagar();
            return null;
        }
        finally { _renovando.Release(); }
    }

    /// <summary>Encerra este dispositivo no servidor e apaga o arquivo. Devolve se o servidor confirmou.</summary>
    public async Task<bool> SairAsync()
    {
        if (!TemConta) return true;
        bool ok = false;
        try
        {
            var segredo = Encoding.UTF8.GetString(_abrir(Convert.FromBase64String(_segredoCifrado!)));
            var (st, _) = await Chamar(Servidor!, "/conta/sair", new JsonObject { ["dispositivo"] = _dispositivo, ["segredo"] = segredo });
            ok = st == HttpStatusCode.OK;
        }
        catch { }
        Apagar();
        return ok;
    }

    void Guardar(JsonObject j)
    {
        lock (_gate) { _acesso = j["acesso"]?.GetValue<string>(); _acessoExpira = j["expira"]?.GetValue<long>() ?? 0; }
        ContaId = j["conta"]?.GetValue<string>() ?? ContaId;
        if (j["grupos"] is JsonArray gs)
            Grupos = gs.OfType<JsonObject>().Where(g => g["id"] != null)
                .Select(g => new GrupoDaConta(g["id"]!.GetValue<string>(), g["nome"]?.GetValue<string>() ?? "")).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
        File.WriteAllText(_arquivo, new JsonObject
        {
            ["servidor"] = Servidor, ["conta"] = ContaId, ["dispositivo"] = _dispositivo, ["segredo"] = _segredoCifrado,
            ["grupos"] = new JsonArray(Grupos.Select(g => (JsonNode)new JsonObject { ["id"] = g.Id, ["nome"] = g.Nome }).ToArray()),
        }.ToJsonString());
    }

    void Ler()
    {
        try
        {
            if (!File.Exists(_arquivo)) return;
            var j = JsonNode.Parse(File.ReadAllText(_arquivo)) as JsonObject;
            if (j == null) return;
            Servidor = j["servidor"]?.GetValue<string>(); ContaId = j["conta"]?.GetValue<string>();
            _dispositivo = j["dispositivo"]?.GetValue<string>(); _segredoCifrado = j["segredo"]?.GetValue<string>();
            if (j["grupos"] is JsonArray gs)
                Grupos = gs.OfType<JsonObject>().Select(g => new GrupoDaConta(g["id"]!.GetValue<string>(), g["nome"]?.GetValue<string>() ?? "")).ToList();
            if (Servidor == null) { _dispositivo = null; _segredoCifrado = null; }
        }
        catch { _dispositivo = null; _segredoCifrado = null; }
    }

    void Apagar()
    {
        lock (_gate) { _acesso = null; _acessoExpira = 0; }
        _dispositivo = null; _segredoCifrado = null; ContaId = null; Grupos = Array.Empty<GrupoDaConta>();
        try { File.Delete(_arquivo); } catch { }
    }

    async Task<(HttpStatusCode, JsonObject?)> Chamar(string servidor, string caminho, JsonObject corpo)
    {
        try
        {
            using var r = await _http.PostAsync(servidor + caminho, new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"));
            var texto = await r.Content.ReadAsStringAsync();
            JsonObject? j = null;
            try { j = JsonNode.Parse(texto) as JsonObject; } catch { }
            return (r.StatusCode, j);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (0, new JsonObject { ["erro"] = "sem resposta do servidor" }); }
    }

    static string? Texto(JsonObject? j) => j?["texto"]?.GetValue<string>() ?? j?["erro"]?.GetValue<string>();
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
