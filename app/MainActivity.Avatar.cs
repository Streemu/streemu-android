using Android.Content;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Avatares;
using Streemu.Proto.Ui;

namespace Streemu.Proto;

/// <summary>
/// Escolher a própria foto ou GIF de avatar (spec 005): o avatar do cartão "Você", em Mais, abre um menu; o seletor de imagens do Android devolve a escolha
/// (sem permissão nova: ele entrega acesso só àquela imagem), a imagem é preparada fora da tela e vira o avatar que os amigos recebem.
/// </summary>
public partial class MainActivity
{
    const int CodigoEscolherAvatar = 50;

    /// <summary>A folha de ações do avatar: Escolher foto ou GIF e, com foto escolhida, Remover. Ex.: <c>avatar.Click += (_, _) => MenuDoAvatar();</c></summary>
    void MenuDoAvatar()
    {
        var acoes = new List<AcaoDaFolha> { new(Resource.Drawable.ic_imagem, "Escolher foto ou GIF", EscolherImagemDeAvatar) };
        if (MeuAvatar.Atual != null) acoes.Add(new(Resource.Drawable.ic_apagar, "Remover foto", MeuAvatar.Remover, Perigosa: true));
        FolhaDeAcoes.Mostrar(this, "Sua foto de perfil", acoes);
    }

    /// <summary>
    /// No Android 13 ou mais, o seletor de fotos do próprio sistema (mostra todas as imagens, GIFs inclusive); antes, o seletor de documentos com filtro de imagem.
    /// O chooser genérico (ACTION_GET_CONTENT) deixava a galeria do fabricante decidir o que listar, e nela o GIF baixado não aparecia (relato de 2026-11-25).
    /// </summary>
    void EscolherImagemDeAvatar()
    {
        var pedido = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? new Intent("android.provider.action.PICK_IMAGES")   // sem o tipo, o seletor também oferece vídeos curtos
            : new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable);
        pedido.SetType("image/*");
        try { StartActivityForResult(pedido, CodigoEscolherAvatar); }
        catch (ActivityNotFoundException) { Toast.MakeText(this, "Este celular não tem um app para escolher imagens (galeria ou arquivos).", ToastLength.Long)?.Show(); }
    }

    void AoEscolherAvatar(Result resultado, Intent? dados)
    {
        // o seletor de fotos do sistema devolve a imagem em ClipData (e só às vezes em Data); o de documentos, em Data
        var uri = dados?.Data ?? (dados?.ClipData is { ItemCount: > 0 } clipe ? clipe.GetItemAt(0)?.Uri : null);
        Streemu.Util.Log.Info($"AVATAR seletor respondeu: {resultado}, imagem {(uri != null ? "recebida" : "ausente")}");
        if (resultado != Result.Ok || uri == null) return;
        Toast.MakeText(this, "Preparando a foto...", ToastLength.Short)?.Show();
        _ = Task.Run(() => PrepararEAplicarAvatar(uri));
    }

    /// <summary>Fora da thread da tela: decodificar um GIF ou uma foto grande leva segundos. Qualquer falha vira uma frase na tela, nunca fecha o app.</summary>
    void PrepararEAplicarAvatar(Android.Net.Uri uri)
    {
        try
        {
            var pronto = PreparadorDeAvatar.Preparar(this, uri);
            RunOnUiThread(() => { MeuAvatar.Definir(pronto); AvisarAvatar(pronto.Aviso ?? "Foto atualizada. Seus amigos já podem ver."); });
        }
        catch (AvatarInvalidoException e) { RunOnUiThread(() => AvisarAvatar(e.Message)); }
        catch (Exception e)   // o arquivo é de qualquer app: o decodificador pode falhar de jeitos que não dá para listar
        {
            Streemu.Util.Log.Info($"AVATAR não preparou a imagem: {e.GetType().Name}: {e.Message}");
            RunOnUiThread(() => AvisarAvatar($"Não consegui preparar essa imagem ({e.GetType().Name}). Escolha outra."));
        }
    }

    void AvisarAvatar(string texto) => Toast.MakeText(this, texto, ToastLength.Long)?.Show();
}
