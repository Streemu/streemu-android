using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using LogApp = Streemu.Util.Log;

[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_SPECIAL_USE")]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission(Android.Manifest.Permission.ReceiveBootCompleted)]

namespace Streemu.Proto.Avisos;

/// <summary>
/// Mantém o app vivo, com a sala do grupo conectada, para avisar de live e de pedido de arquivo com o app fechado. Serviço em primeiro plano do tipo
/// <c>specialUse</c> (o <c>dataSync</c> tem limite de 6 h por 24 h no Android 15+, e este precisa ficar ligado o dia todo; o app não passa pela Play
/// Store). A notificação fixa é discreta, sem som. Quem liga é o <see cref="AvisosDeSegundoPlano"/>.
/// <para>
/// Limite conhecido: com o aparelho parado e a tela apagada por muito tempo o Android entra em Doze e segura a rede, e o aviso pode chegar atrasado;
/// alguns fabricantes (Xiaomi/POCO) também matam serviços. A sala reconecta sozinha quando a rede volta.
/// </para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[Property("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "Manter a conexão com a sala do grupo para avisar de lives e de arquivos recebidos")]
public class ServicoDeAvisos : Service
{
    public const string Canal = "avisos-servico";
    const int IdDoServico = 4401;

    internal static void Iniciar(Context c)
    {
        try { c.StartForegroundService(new Intent(c, typeof(ServicoDeAvisos))); }
        catch (Exception e) { LogApp.Info($"[avisos] não deu para iniciar o serviço ({e.GetType().Name}: {e.Message}); os avisos só saem com o app aberto"); }
    }

    internal static void Parar(Context c)
    {
        try { c.StopService(new Intent(c, typeof(ServicoDeAvisos))); }
        catch (Exception e) { LogApp.Info("[avisos] parar o serviço: " + e.Message); }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var n = Notificacao(this);
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(IdDoServico, n, ForegroundService.TypeSpecialUse);
            else StartForeground(IdDoServico, n);
            // processo novo (boot, atualização, sistema que matou e recriou): sem a Sessao de pé não há sala nem fluxo de arquivos
            Sessao.Iniciar(ApplicationContext!);
            if (!Sessao.Conta.TemConta) StopSelf();   // saiu da conta
        }
        catch (Exception e) { LogApp.Info($"[avisos] primeiro plano: {e.GetType().Name}: {e.Message}"); }
        return StartCommandResult.Sticky;
    }

    static Notification Notificacao(Context c)
    {
        var gerente = (NotificationManager)c.GetSystemService(NotificationService)!;
        var canal = new NotificationChannel(Canal, "Streemu conectado", NotificationImportance.Min) { Description = "O aviso fixo de que o Streemu está de olho nas lives e nos arquivos do seu grupo." };
        canal.SetShowBadge(false);
        gerente.CreateNotificationChannel(canal);
        var abrir = PendingIntent.GetActivity(c, 0, new Intent(c, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Builder(c, Canal)
            .SetSmallIcon(Icon.CreateWithResource(c, Resource.Drawable.ic_ao_vivo)!)!
            .SetContentTitle("Streemu conectado")!.SetContentText("Avisando de lives e arquivos do seu grupo")!
            .SetOngoing(true)!.SetOnlyAlertOnce(true)!.SetContentIntent(abrir)!.Build()!;
    }
}

/// <summary>Reinício do aparelho e atualização do app: o Android mata o serviço nos dois casos, e sem isto os avisos só voltariam quando a pessoa abrisse o app.</summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public class ReceptorDeBoot : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced)) return;
        AvisosDeSegundoPlano.IniciarDoBoot(context);
    }
}
