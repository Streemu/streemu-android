using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// "Compartilhar → Streemu" (spec 030, D8): a porta de entrada dos arquivos que outros apps mandam compartilhar (galeria, gerenciador de arquivos,
/// câmera). Não desenha nada: repassa os arquivos para a <c>MainActivity</c>, que abre a aba Arquivos com eles já escolhidos e pede o amigo.
/// <para>
/// <b>Vem desligada no manifesto</b> (<c>Enabled = false</c>) e <see cref="EnvioDeArquivos.Iniciar"/> a liga só quando a seção está liberada: assim,
/// enquanto a seção está escondida, o Streemu não aparece na folha de compartilhar do sistema (um filtro na <c>MainActivity</c> apareceria sempre).
/// </para>
/// O Android dá a esta atividade só um acesso temporário de leitura; o repasse leva a permissão junto (<see cref="ActivityFlags.GrantReadUriPermission"/>).
/// Esse acesso some quando o app fecha, então "Pedir de novo" de um arquivo compartilhado, depois de reabrir o app, pode dizer que o arquivo não está mais acessível.
/// </summary>
[Activity(Name = "streemu.proto.arquivos.CompartilharActivity", Exported = true, Enabled = false, ExcludeFromRecents = true,
          Theme = "@android:style/Theme.Translucent.NoTitleBar", Label = NomeApp.Nome)]
[IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public class CompartilharActivity : Activity
{
    /// <summary>A ação com que a <c>MainActivity</c> reconhece um compartilhamento já repassado.</summary>
    public const string Acao = "br.com.streemu.proto.COMPARTILHAR_ARQUIVOS";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var clip = Intent?.ClipData ?? ClipDeExtraStream(Intent);
        if (clip == null || clip.ItemCount == 0)
        {
            Toast.MakeText(this, "O Streemu só recebe arquivos. Escolha um arquivo, foto ou vídeo para compartilhar.", ToastLength.Long)?.Show();
            Finish();
            return;
        }
        LogApp.Info($"[arquivo] compartilhamento recebido: {clip.ItemCount} item(ns)");
        var repasse = new Intent(this, typeof(MainActivity)).SetAction(Acao)!.AddFlags(ActivityFlags.GrantReadUriPermission)!;
        repasse.ClipData = clip;
        StartActivity(repasse);
        Finish();
    }

    /// <summary>Apps antigos mandam só o <c>EXTRA_STREAM</c>, sem <c>ClipData</c>: monta um para o repasse levar a permissão.</summary>
    static ClipData? ClipDeExtraStream(Intent? i)
    {
        if (i == null) return null;
        if (i.Action == Intent.ActionSendMultiple && i.GetParcelableArrayListExtra(Intent.ExtraStream) is { Count: > 0 } lista)
        {
            var clip = ClipData.NewRawUri("arquivos", lista[0] as Android.Net.Uri);
            for (int n = 1; n < lista.Count; n++) if (lista[n] is Android.Net.Uri u) clip.AddItem(new ClipData.Item(u));
            return clip;
        }
        return i.GetParcelableExtra(Intent.ExtraStream) is Android.Net.Uri um ? ClipData.NewRawUri("arquivo", um) : null;
    }

    /// <summary>Liga ou desliga a porta, conforme a seção está liberada. Chamado por <see cref="EnvioDeArquivos.Iniciar"/>.</summary>
    internal static void Ligar(Context c, bool ligada)
    {
        try
        {
            c.PackageManager!.SetComponentEnabledSetting(new ComponentName(c, Java.Lang.Class.FromType(typeof(CompartilharActivity))),
                ligada ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled, ComponentEnableOption.DontKillApp);
        }
        catch (Exception e) { LogApp.Info($"[arquivo] não consegui {(ligada ? "ligar" : "desligar")} o Compartilhar ({e.GetType().Name})"); }
    }
}
