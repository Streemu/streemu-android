using System.Text.Json.Nodes;
using Android.Content;
using Android.Graphics;
using Streemu.Net;
using Streemu.Proto.Transmissao;

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
    public static bool Iniciada { get; private set; }
    public static bool AguardandoAtualizacao { get; private set; }

    /// <summary>A conta, a sala, a lista de amigos, uma miniatura ou um avatar mudou.</summary>
    public static event Action? Mudou;
    /// <summary>Mensagem de um amigo que não é de imagem: (conta, apelido, mensagem).</summary>
    public static event Action<string, string, JsonObject>? Mensagem;
    public static event Action<string, JsonObject?>? NaoEntregue;

    public static void Iniciar(Context c, bool aguardarAtualizacao = false)
    {
        if (Iniciada) return;
        Atualizacao.Iniciar(c);
        Iniciada = true;
        AguardandoAtualizacao = aguardarAtualizacao;
        _prefs = c.GetSharedPreferences("streemu", FileCreationMode.Private)!;
        Streemu.Util.Log.Line += l => Android.Util.Log.Info("Streemu", l);
        Conta = new ContaLeve(System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "conta.json"), "celular (protótipo)", Cofre.Cifrar, Cofre.Abrir, ApelidoAntigo);
        Conta.NomeMudou += AoMudarNome;
        RelayConfig.Token = () => Conta.AcessoAsync();
        Telemetria.Iniciar(c, forcar => Conta.AcessoAsync(forcar), () => Conta.TemConta ? Conta.Servidor : null, () => Apelido);
        Atualizacao.RegistrarResultadoPendente();
        // os dados do aparelho uma vez por abertura (logcat e log.txt da telemetria); o perfil completo vai no perfil-<hash>.json
        Streemu.Util.Log.Info("APARELHO " + Aparelho.Resumo(c));
        if (Conta.TemConta) AbrirSala(_prefs.GetString("grupo", null));
    }

    /// <summary>
    /// O nome que os amigos veem: o nome da conta (spec 027), igual em todos os aparelhos. Quem ainda não tem nome usa o apelido que salvou antes
    /// e, em último caso, "amigo", só para nunca mandar vazio. Troca-se com <see cref="ContaLeve.MudarNomeAsync"/>.
    /// </summary>
    public static string Apelido => Conta.Nome ?? ApelidoAntigo() ?? "amigo";

    /// <summary>O apelido que a pessoa salvou antes da spec 027 (preferência <c>apelido</c>, que agora só serve para a migração). "Celular" era o padrão e não conta.</summary>
    static string? ApelidoAntigo() =>
        _prefs.GetString("apelido", null)?.Trim() is { Length: > 0 } a && !a.Equals("Celular", StringComparison.OrdinalIgnoreCase) ? a : null;

    /// <summary>Sugestão para o diálogo "Escolha seu nome": o apelido antigo, se houver; senão vazio.</summary>
    public static string SugestaoDeNome => ApelidoAntigo() ?? "";

    /// <summary>
    /// A conta (já confirmada pelo servidor) ficou sem nome e a pessoa ainda não viu o diálogo "Escolha seu nome". Ele aparece uma vez só:
    /// quem chama mostra e chama <see cref="NomeOferecido"/>.
    /// </summary>
    public static bool PrecisaEscolherNome => Iniciada && Conta.TemConta && Conta.NomeConhecido && Conta.Nome == null && !_prefs.GetBoolean("nome_oferecido", false);

    public static void NomeOferecido() => _prefs.Edit()!.PutBoolean("nome_oferecido", true)!.Apply();

    static void AoMudarNome()
    {
        Sala?.SetNick(Apelido);
        Avisar();
    }

    public static string NomeDoGrupo => Sala?.GrupoNome is { Length: > 0 } n ? n : Conta.Grupos.FirstOrDefault(g => g.Id == GrupoId)?.Nome ?? "";

    /// <summary>Abre a sala do grupo (o último usado, se ainda for da conta; senão o primeiro).</summary>
    public static void AbrirSala(string? grupoId)
    {
        var gs = Conta.Grupos;
        // sala travada pela atualização: guarda a escolha (o grupo em que a pessoa acabou de entrar, por exemplo) para quando ela abrir
        if (Atualizacao.BloqueiaSala || AguardandoAtualizacao)
        {
            if (grupoId != null && gs.Any(g => g.Id == grupoId)) _prefs.Edit()!.PutString("grupo", grupoId)!.Apply();
            return;
        }
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

    public static void AplicarBloqueioDeAtualizacao()
    {
        if (!Iniciada) return;
        AguardandoAtualizacao = false;
        if (Atualizacao.BloqueiaSala) FecharSala();
        else if (Sala == null && Conta.TemConta) AbrirSala(_prefs.GetString("grupo", null));
        Avisar();
    }

    static void FecharSala()
    {
        var s = Sala;
        Sala = null;
        // o Dispose da sala espera o WebSocket fechar (até ~0,8 s): fora da thread da tela, que chama isto ao trocar de grupo e ao sair da conta
        if (s != null) _ = Task.Run(() => { try { s.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[sala] fechar: " + e.Message); } });
        lock (_gate) { _miniaturas.Clear(); _avataresPedidos.Clear(); }
    }

    public static async Task SairDaContaAsync()
    {
        FecharSala();
        GrupoId = null;
        await Conta.SairAsync();
        Avisar();
    }

    /// <summary>Quem mudou algo que a tela mostra fora da sala (a lista de grupos da conta, depois de criar, entrar ou sair) avisa por aqui.</summary>
    public static void Avisar() => Mudou?.Invoke();

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
        // a sala difunde o nome da minha conta: se a troca foi feita em outro aparelho (o PC, por exemplo), mostro esse
        if (sala?.Find(sala.MyId) is { } eu && eu.Nick.Trim() is { Length: > 0 } nick && !nick.Equals("amigo", StringComparison.OrdinalIgnoreCase)) Conta.AdotarNomeDaSala(nick);
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
            // a v1 de transmitir não manda miniatura nem avatar (o cartão do amigo fica sem imagem): o PC que pede não recebe resposta
            case "avatar_pedir": case "miniatura_pedir": return;
            // o lado de quem transmite (Anfitriao): quem quer assistir a esta live
            case "quero_assistir":
                if (Anfitriao.Ativo is { } ao) ao.AoReceber(de, apelido, msg);
                else RecusarSemLive(de, msg);
                return;
            // "parar" e "reabrir_player" servem aos dois lados. Se a sessão é de um espectador da minha live, é do Anfitriao; senão segue para o player
            // (que também confere a sessão: um "parar" de um espectador meu nunca fecha o meu próprio player por engano)
            case "parar" or "reabrir_player" when Anfitriao.Ativo is { } host && host.TemSessao(Streemu.Util.JsonSeguro.Texto(msg["sessao"]) ?? ""):
                host.AoReceber(de, apelido, msg);
                return;
        }
        Mensagem?.Invoke(de, apelido, msg);
    }

    /// <summary>Pediram para assistir uma live minha que já acabou (a lista do amigo estava velha): responde "recusado", como o app Windows. Da minha própria conta, ignora (a sala entrega por conta).</summary>
    static void RecusarSemLive(string de, JsonObject msg)
    {
        var sid = Streemu.Util.JsonSeguro.Texto(msg["sessao"]);
        if (string.IsNullOrEmpty(sid) || de == Conta.ContaId || Sala is not { } sala) return;
        _ = sala.SendTo(de, new JsonObject { ["tipo"] = "recusado", ["sessao"] = sid, ["motivo"] = "O celular já parou de transmitir." });
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
