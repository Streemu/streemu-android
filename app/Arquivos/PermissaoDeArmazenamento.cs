using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

// Só até o Android 9 (API 28): no 10 em diante o app grava em Downloads pelo MediaStore sem permissão nenhuma, e a permissão nem existe mais.
[assembly: UsesPermission(Android.Manifest.Permission.WriteExternalStorage, MaxSdkVersion = 28)]

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// A permissão de escrita que o Android 8 e 9 exigem para gravar em <c>Downloads/Streemu</c> (spec 030, D4). Pedida uma vez, antes do primeiro
/// recebimento, com uma frase da própria tela explicando para que serve. No Android 10+ a resposta é sempre "tem": nada a pedir.
/// Ex.: <c>if (!PermissaoDeArmazenamento.Tem(this)) PermissaoDeArmazenamento.Pedir(this, codigo);</c>
/// </summary>
static class PermissaoDeArmazenamento
{
    public static bool Tem(Context c) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Q || c.CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    /// <summary>Mostra a pergunta do sistema. O resultado chega em <c>OnRequestPermissionsResult</c> da activity com o mesmo código.</summary>
    public static void Pedir(Activity activity, int codigo)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) return;
        activity.RequestPermissions([Android.Manifest.Permission.WriteExternalStorage], codigo);
    }
}
