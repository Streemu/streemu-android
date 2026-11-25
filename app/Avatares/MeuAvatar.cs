using Android.Content;

namespace Streemu.Proto.Avatares;

/// <summary>
/// O avatar que a própria pessoa escolheu neste celular (spec 005): guardado no armazenamento do app, para sobreviver ao fechar e abrir, e igual ao que é
/// enviado aos amigos. Por aparelho, como no app Windows. Sem foto escolhida, <see cref="Atual"/> é null e todo mundo vê a inicial colorida.
/// Uso: <c>MeuAvatar.Iniciar(contexto); MeuAvatar.Definir(pronto); if (MeuAvatar.Atual is { } a) ...</c>
/// </summary>
static class MeuAvatar
{
    const string NomeParado = "atual.png", NomeTira = "atual.tira";
    static string _pasta = "";

    public static AvatarPronto? Atual { get; private set; }
    /// <summary>A escolha mudou (ou foi removida). Vem da thread de quem chamou <see cref="Definir"/> ou <see cref="Remover"/>.</summary>
    public static event Action? Mudou;

    /// <summary>Lê o avatar guardado, se houver. Chamar uma vez ao abrir o app.</summary>
    public static void Iniciar(Context c)
    {
        _pasta = Path.Combine(c.FilesDir!.AbsolutePath, "avatar");
        Atual = Abrir(NomeParado, FormatoDoAvatar.Png) ?? Abrir(NomeTira, FormatoDoAvatar.Tira);
    }

    public static void Definir(AvatarPronto novo)
    {
        Atual = novo;
        Guardar(novo);
        Mudou?.Invoke();
    }

    public static void Remover()
    {
        Atual = null;
        Apagar(NomeParado);
        Apagar(NomeTira);
        Mudou?.Invoke();
    }

    static AvatarPronto? Abrir(string nome, FormatoDoAvatar formato)
    {
        var caminho = Path.Combine(_pasta, nome);
        try { return File.Exists(caminho) ? AvatarPronto.DeDados(File.ReadAllBytes(caminho), formato) : null; }
        catch (IOException e) { Streemu.Util.Log.Info($"AVATAR não abriu o guardado ({formato}): {e.Message}"); return null; }
    }

    /// <summary>Guarda a cópia para a próxima abertura. Se não der, o avatar continua valendo até fechar o app.</summary>
    static void Guardar(AvatarPronto avatar)
    {
        try
        {
            Directory.CreateDirectory(_pasta);
            Apagar(avatar.Formato == FormatoDoAvatar.Tira ? NomeParado : NomeTira);
            File.WriteAllBytes(Path.Combine(_pasta, avatar.Formato == FormatoDoAvatar.Tira ? NomeTira : NomeParado), avatar.Dados);
        }
        catch (IOException e) { Streemu.Util.Log.Info($"AVATAR não consegui guardar ({avatar.Dados.Length} bytes): {e.Message}"); }
    }

    static void Apagar(string nome)
    {
        try { File.Delete(Path.Combine(_pasta, nome)); }
        catch (IOException e) { Streemu.Util.Log.Info($"AVATAR não consegui apagar o guardado ({nome}): {e.Message}"); }
    }
}
