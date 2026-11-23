using Android.App;
using Android.Content;
using Android.Net;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Dados móveis e o envio de arquivo (spec 030, D5): saber se a rede de agora é medida (plano de dados) e lembrar o "não perguntar de novo".
/// Antes de BAIXAR mais de 100 MB numa rede medida o app pergunta quanto vai gastar; em Wi-Fi não pergunta. "Medida" é o que o sistema diz
/// (<c>IsActiveNetworkMetered</c>): vale também para um Wi-Fi que a pessoa marcou como medido, que é o que se quer.
/// Ex.: <c>fluxo.RedeMedida = () =&gt; RedeMovel.Medida(ctx);</c>
/// </summary>
static class RedeMovel
{
    const string Preferencias = "streemu", ChaveNaoPerguntar = "arquivos_dados_nao_perguntar";

    public static bool Medida(Context c)
    {
        try { return (c.GetSystemService(Context.ConnectivityService) as ConnectivityManager)?.IsActiveNetworkMetered ?? false; }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.RuntimeException) { return false; }
    }

    public static bool NaoPerguntar(Context c) => c.GetSharedPreferences(Preferencias, FileCreationMode.Private)!.GetBoolean(ChaveNaoPerguntar, false);

    public static void LembrarNaoPerguntar(Context c) =>
        c.GetSharedPreferences(Preferencias, FileCreationMode.Private)!.Edit()!.PutBoolean(ChaveNaoPerguntar, true)!.Apply();

    /// <summary>A pessoa mudou de ideia (ajuste da tela de arquivos): volta a perguntar.</summary>
    public static void VoltarAPerguntar(Context c) =>
        c.GetSharedPreferences(Preferencias, FileCreationMode.Private)!.Edit()!.Remove(ChaveNaoPerguntar)!.Apply();
}
