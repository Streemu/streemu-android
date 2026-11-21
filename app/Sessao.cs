using System.Text.Json.Nodes;
using Android.Content;
using Android.Graphics;
using Streemu.Net;

namespace Streemu.Proto;

/// <summary>
/// O que vive enquanto o app está aberto, de uma tela para outra: a conta, a sala do grupo escolhido, as miniaturas das lives e os avatares.
/// As telas assinam <see cref="Mudou"/> e redesenham (o evento vem de qualquer thread). Mensagens da sala que não são de imagem vão para
/// <see cref="Mensagem"/> (o player escuta o "aceito").
/// </summary>
static class Sessao
{
    public static ContaLeve Conta { get; private set; } = null!;
    public static GroupRoom? Sala { get; private set; }
    public static string? GrupoId { get; private set; }
    static ISharedPreferences _prefs = null!;
    static bool _iniciada;

    /// <summary>A conta, a sala, a lista de amigos, uma miniatura ou um avatar mudou.</summary>
    public static event Action? Mudou;
    /// <summary>Mensagem de um amigo que não é de imagem: (conta, apelido, mensagem).</summary>
    public static event Action<string, string, JsonObject>? Mensagem;
    public static event Action<string, JsonObject?>? NaoEntregue;

    public static void Iniciar(Context c)
    {
        if (_iniciada) return;
        _iniciada = true;
        _prefs = c.GetSharedPreferences("streemu", FileCreationMode.Private)!;
        Streemu.Util.Log.Line += l => Android.Util.Log.Info("Streemu", l);
        Conta = new ContaLeve(System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "conta.json"), "celular (protótipo)", Cofre.Cifrar, Cofre.Abrir);
        RelayConfig.Token = () => Conta.AcessoAsync();
        Telemetria.Iniciar(c, forcar => Conta.AcessoAsync(forcar), () => Conta.TemConta ? Conta.Servidor : null, () => Apelido);
        // os dados do aparelho uma vez por abertura (logcat e log.txt da telemetria); o perfil completo vai no perfil-<hash>.json
        Streemu.Util.Log.Info("APARELHO " + Aparelho.Resumo(c));
        if (Conta.TemConta) AbrirSala(_prefs.GetString("grupo", null));
    }

    /// <summary>O nome que os amigos veem na sala. Começa com o modelo do celular; a pessoa troca na aba Conta.</summary>
    public static string Apelido
    {
        get => _prefs.GetString("apelido", null) is { Length: > 0 } a ? a : "Celular";
        set
        {
            var limpo = value.Trim();
            if (limpo.Length > 24) limpo = limpo[..24];
            _prefs.Edit()!.PutString("apelido", limpo)!.Apply();
            Sala?.SetNick(Apelido);
            Avisar();
        }
    }

    public static string NomeDoGrupo => Sala?.GrupoNome is { Length: > 0 } n ? n : Conta.Grupos.FirstOrDefault(g => g.Id == GrupoId)?.Nome ?? "";

    /// <summary>Abre a sala do grupo (o último usado, se ainda for da conta; senão o primeiro).</summary>
    public static void AbrirSala(string? grupoId)
    {
        var gs = Conta.Grupos;
        if (grupoId == null || gs.All(g => g.Id != grupoId)) grupoId = gs.FirstOrDefault()?.Id;
        if (grupoId == GrupoId && Sala != null) return;
        FecharSala();
        GrupoId = grupoId;
        if (grupoId == null || Conta.ContaId == null || Conta.Servidor == null) { Avisar(); return; }
        _prefs.Edit()!.PutString("grupo", grupoId)!.Apply();
        var sala = new GroupRoom(GroupRoom.RoomUrl(Conta.Servidor, grupoId), Conta.ContaId, Apelido, forcar => Conta.AcessoAsync(forcar));
        sala.Changed += AoMudarSala;
        sala.AtividadesMudaram += Avisar;
        sala.Message += AoReceber;
        sala.Undeliverable += (para, msg) => NaoEntregue?.Invoke(para, msg);
        sala.Encerrada += _ => Avisar();
        Sala = sala;
        Avisar();
    }

    static void FecharSala()
    {
        var s = Sala;
        Sala = null;
        s?.Dispose();
        lock (_gate) { _miniaturas.Clear(); _avataresPedidos.Clear(); }
    }

    public static async Task SairDaContaAsync()
    {
        FecharSala();
        GrupoId = null;
        await Conta.SairAsync();
        Avisar();
    }

    static void Avisar() => Mudou?.Invoke();

    // ------------------------------------------------------------------ imagens: miniaturas das lives e avatares

    static readonly object _gate = new();
    static readonly Dictionary<string, Bitmap> _miniaturas = new();   // sala da live -> miniatura
    static readonly Dictionary<string, Bitmap> _avatares = new();     // conta -> avatar
    static readonly HashSet<string> _avataresPedidos = new();
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static Bitmap? Miniatura(string streamId) { lock (_gate) return _miniaturas.GetValueOrDefault(streamId); }
    public static Bitmap? Avatar(string conta) { lock (_gate) return _avatares.GetValueOrDefault(conta); }

    static void AoMudarSala()
    {
        var sala = Sala;
        if (sala is { Connected: true })
        {
            // o avatar de cada amigo, uma vez por sala (o PC manda o dele sozinho também; pedir cobre quem já estava lá)
            foreach (var m in sala.Friends)
            {
                bool pedir;
                lock (_gate) pedir = _avataresPedidos.Add(m.Id);
                if (pedir) _ = sala.SendTo(m.Id, new JsonObject { ["tipo"] = "avatar_pedir" });
            }
            var aoVivo = sala.Friends.Where(m => m.Live != null).Select(m => m.Live!.StreamId).ToHashSet();
            lock (_gate) foreach (var k in _miniaturas.Keys.Where(k => !aoVivo.Contains(k)).ToList()) _miniaturas.Remove(k);
        }
        Avisar();
    }

    /// <summary>Pede (ou deixa de pedir) as miniaturas de quem está ao vivo. O PC manda uma a cada ~8 s por um tempo; a aba Ao vivo pede de novo a cada 20 s.</summary>
    public static void PedirMiniaturas(bool ligar)
    {
        var sala = Sala;
        if (sala is not { Connected: true }) return;
        foreach (var m in sala.Friends.Where(m => m.Live != null))
            _ = sala.SendTo(m.Id, new JsonObject { ["tipo"] = "miniatura_pedir", ["ligar"] = ligar });
    }

    static void AoReceber(string de, string apelido, JsonObject msg)
    {
        switch (msg["tipo"]?.GetValue<string>())
        {
            case "miniatura": Miniatura(msg); return;
            case "avatar": _ = AvatarAsync(de, msg); return;
            case "avatar_pedir": case "miniatura_pedir": return;   // o celular não transmite nem tem avatar ainda
        }
        Mensagem?.Invoke(de, apelido, msg);
    }

    /// <summary>Tudo que vem de um amigo é tratado como não confiável: tamanho e dimensões conferidos antes de decodificar (limites do app no PC, spec 006).</summary>
    static void Miniatura(JsonObject msg)
    {
        try
        {
            if (msg["sala"]?.GetValue<string>() is not { Length: > 0 and <= 40 } sala || msg["dados"]?.GetValue<string>() is not { Length: > 0 and < 20_000 } b64) return;
            var jpeg = Convert.FromBase64String(b64);
            if (jpeg.Length > 12 * 1024) return;
            var bmp = Decodificar(jpeg, 192, 108);
            if (bmp == null) return;
            lock (_gate) _miniaturas[sala] = bmp;
            Avisar();
        }
        catch (FormatException) { }
    }

    static async Task AvatarAsync(string conta, JsonObject msg)
    {
        try
        {
            Bitmap? bmp = null;
            switch (msg["fonte"]?.GetValue<string>())
            {
                case "imagem" when msg["dados"]?.GetValue<string>() is { Length: > 0 and < 400_000 } b64:
                    bmp = Decodificar(Convert.FromBase64String(b64), 1024, 512);
                    // "tira" é uma fileira de quadros de uma animação: o primeiro quadrado basta para o celular
                    if (bmp != null && msg["formato"]?.GetValue<string>() == "tira" && bmp.Width > bmp.Height)
                        bmp = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Height, bmp.Height);
                    break;
                case "discord" when UrlDoDiscord(msg["id"]?.GetValue<string>(), msg["codigo"]?.GetValue<string>()) is { } url:
                    var bytes = await _http.GetByteArrayAsync(url);
                    if (bytes.Length < 2_000_000) bmp = Decodificar(bytes, 1024, 1024);
                    break;
                case "nenhum":
                    lock (_gate) _avatares.Remove(conta);
                    Avisar();
                    return;
            }
            if (bmp == null) return;
            lock (_gate) _avatares[conta] = bmp;
            Avisar();
        }
        catch (Exception e) when (e is FormatException or HttpRequestException or TaskCanceledException) { }
    }

    /// <summary>Só o endereço que o app escolhe: id numérico e código hex (a mesma regra do DiscordAvatarUrl do app Windows).</summary>
    static string? UrlDoDiscord(string? id, string? codigo)
    {
        if (id is not { Length: >= 15 and <= 22 } || !id.All(char.IsAsciiDigit)) return null;
        if (codigo == null) return null;
        var hex = codigo.StartsWith("a_", StringComparison.Ordinal) ? codigo[2..] : codigo;
        if (hex.Length != 32 || !hex.All(char.IsAsciiHexDigitLower)) return null;
        return $"https://cdn.discordapp.com/avatars/{id}/{codigo}.png?size=128";   // PNG mesmo para os animados: o primeiro quadro
    }

    static Bitmap? Decodificar(byte[] dados, int maxW, int maxH)
    {
        var o = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(dados, 0, dados.Length, o);
        if (o.OutWidth <= 0 || o.OutHeight <= 0 || o.OutWidth > maxW || o.OutHeight > maxH) return null;
        return BitmapFactory.DecodeByteArray(dados, 0, dados.Length);
    }
}
