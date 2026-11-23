using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidUri = Android.Net.Uri;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// A pasta de recebidos que a pessoa escolhe pelo seletor de pastas do sistema (spec 030, D4): funciona em qualquer versão, sem permissão de
/// armazenamento, e é o único jeito de ter os recebidos fora da pasta do app no Android 8 e 9. O sistema não deixa escolher a raiz do
/// armazenamento nem a pasta Downloads inteira a partir do Android 11; a pessoa escolhe (ou cria) uma subpasta.
/// Ex.: <c>StartActivityForResult(PastaEscolhida.Seletor(), codigo);</c> e, no resultado, <c>PastaEscolhida.Guardar(this, dados.Data)</c>.
/// </summary>
static class PastaEscolhida
{
    public static Intent Seletor()
    {
        var i = new Intent(Intent.ActionOpenDocumentTree)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            i.PutExtra(DocumentsContract.ExtraInitialUri, DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:Download"));
        return i;
    }

    /// <summary>Guarda o acesso de leitura e escrita para depois que o app fechar. Devolve o texto que vai para o histórico e para <see cref="Recebidos"/>; lança com o motivo se o sistema recusar.</summary>
    public static string Guardar(Context c, AndroidUri arvore)
    {
        try { c.ContentResolver!.TakePersistableUriPermission(arvore, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission); }
        catch (Exception e) { throw new IOException($"O Android não guardou o acesso a {arvore}; esperado: uma pasta onde o app possa gravar. Escolha outra pasta. ({e.GetType().Name})"); }
        return arvore.ToString()!;
    }

    /// <summary>A pasta escolhida ainda dá permissão de escrita? Some se a pasta for apagada, o cartão sair ou o usuário revogar nos ajustes do sistema.</summary>
    public static bool AindaVale(Context c, string arvore)
    {
        try
        {
            return c.ContentResolver!.PersistedUriPermissions.Any(p => p.IsWritePermission && p.Uri?.ToString() == arvore);
        }
        catch (Exception e) { LogApp.Info($"[arquivo] não consegui ler as permissões guardadas ({e.GetType().Name})"); return false; }
    }
}
