using Android.App;
using Android.Content;
using AndroidX.Core.Content;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Deixa outro app abrir um arquivo da pasta do Streemu (só a subpasta Recebidos da pasta do app e a pasta Download/Streemu). Existe para o "Abrir" no Android 8 e 9, onde o arquivo
/// fica na pasta do app até a pessoa escolher outra: sem um provedor, o Android recusa entregar o caminho a outro app. No Android 10+ o
/// arquivo está no MediaStore e este provedor não é usado. Exportado = falso: só quem recebe a permissão do Intent consegue ler.
/// </summary>
[ContentProvider(new[] { "${applicationId}.arquivos" }, Name = "streemu.proto.arquivos.ProvedorDeRecebidos", Exported = false, GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/arquivos_recebidos_paths")]
public class ProvedorDeRecebidos : FileProvider
{
}
