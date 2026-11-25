using System.Text.Json.Nodes;
using Android.Graphics;
using Streemu.Proto.Avatares;

namespace Streemu.Proto;

/// <summary>
/// O envio do meu avatar aos amigos (spec 005 do celular; o mesmo protocolo do <c>MainForm.AvatarRede.cs</c> do app Windows): manda a TODOS da sala quando
/// o avatar muda e quando alguém aparece, e de novo a quem pede (<c>avatar_pedir</c>). Lembra o que cada amigo já recebeu (a assinatura) para não repetir.
/// </summary>
static partial class Sessao
{
    /// <summary>Conta do amigo -> assinatura do último avatar que mandei a ele ("none" ou o hash). Esquecido ao trocar de sala.</summary>
    static readonly Dictionary<string, string> _avatarEnviadoA = new();

    /// <summary>Manda o avatar de agora a quem ainda não o tem. Sem sala conectada não faz nada (e não marca ninguém como atendido).</summary>
    public static void AnunciarAvatar()
    {
        var sala = Sala;
        if (sala is not { Connected: true }) return;
        string assinatura = MeuAvatar.Atual?.Hash ?? "none";
        var amigos = sala.Friends.Select(f => f.Id).ToList();
        foreach (var id in amigos.Where(id => DevoEnviar(id, assinatura))) _ = sala.SendTo(id, MensagemDoMeuAvatar());
        lock (_gate) foreach (var foi in _avatarEnviadoA.Keys.Where(k => !amigos.Contains(k)).ToList()) _avatarEnviadoA.Remove(foi);
    }

    /// <summary>Marca o amigo como atendido e diz se há o que mandar. Quem nunca recebeu nada e agora não tem foto já mostra a inicial colorida: não manda.</summary>
    static bool DevoEnviar(string amigo, string assinatura)
    {
        lock (_gate)
        {
            bool jaRecebeu = _avatarEnviadoA.TryGetValue(amigo, out var enviado);
            if (jaRecebeu && enviado == assinatura) return false;
            _avatarEnviadoA[amigo] = assinatura;
            return assinatura != "none" || jaRecebeu;
        }
    }

    /// <summary>Um objeto novo a cada chamada: um JsonNode só pode ter um "pai", então reusar o mesmo para dois amigos lançaria.</summary>
    static JsonObject MensagemDoMeuAvatar() => MeuAvatar.Atual is { } meu
        ? new JsonObject
        {
            ["tipo"] = "avatar", ["fonte"] = "imagem", ["formato"] = meu.Formato == FormatoDoAvatar.Tira ? "tira" : "png",
            ["hash"] = meu.Hash, ["dados"] = Convert.ToBase64String(meu.Dados),
        }
        : new JsonObject { ["tipo"] = "avatar", ["fonte"] = "nenhum" };

    /// <summary>
    /// O avatar que um amigo mandou como imagem. Parado: a miniatura. "tira": os quadros de um GIF, guardados para animar ao toque; o primeiro quadro é o
    /// que aparece parado. Devolve null se não servir (a imagem anterior do amigo fica como estava).
    /// </summary>
    static Bitmap? ImagemDeAmigo(string conta, byte[] dados, string? formato)
    {
        if (formato != "tira")
        {
            var parado = Decodificar(dados, 1024, 512);
            if (parado != null) lock (_gate) _animacoes.Remove(conta);
            return parado;
        }
        var animacao = TiraDeQuadros.Ler(dados, out var motivo);
        if (animacao == null) { Streemu.Util.Log.Info($"AVATAR a tira de um amigo não serviu: {motivo}"); return null; }
        lock (_gate) _animacoes[conta] = animacao;
        return animacao.Quadros[0];
    }

    /// <summary>O amigo abriu o app e quer o meu avatar: esquece que já mandei e manda de novo.</summary>
    static void AoPedirAvatar(string amigo)
    {
        lock (_gate) _avatarEnviadoA.Remove(amigo);
        AnunciarAvatar();
    }

    static void EsquecerEnviosDeAvatar() { lock (_gate) _avatarEnviadoA.Clear(); }
}
