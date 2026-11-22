using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Streemu.Util;

[assembly: Android.App.UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK")]

namespace Streemu.Proto.Filme;

/// <summary>
/// Mantém o celular anfitrião do Cinema vivo com o app em segundo plano (primeiro plano, tipo mediaPlayback), com a notificação "Você está no Cinema: título" e o botão "Encerrar".
/// O anfitrião de verdade mora na <see cref="FilmeActivity"/> (o ExoPlayer e a SurfaceView são dela); o serviço só segura o processo e a rede. Se a activity fechar, o anfitrião fecha e o serviço sai junto.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public class ServicoCinema : Service
{
    public const string AcaoEncerrar = "br.com.streemu.proto.ENCERRAR_CINEMA";
    const string Canal = "cinema", ExtraTitulo = "titulo";
    const int IdNotificacao = 4202;
    static ServicoCinema? _atual;

    /// <summary>Chamado pela FilmeActivity ao abrir como anfitriã. Ainda na thread da tela, com o app na frente (único jeito de começar um serviço em primeiro plano).</summary>
    public static void Iniciar(Context c, string titulo)
    {
        try { c.StartForegroundService(new Intent(c, typeof(ServicoCinema)).PutExtra(ExtraTitulo, titulo)); }
        catch (Exception e) { Log.Info("Cinema: não deu para iniciar o serviço (o anfitrião segue só com a tela aberta): " + e.Message); }
    }

    public static void Parar(Context c)
    {
        try { c.StopService(new Intent(c, typeof(ServicoCinema))); }
        catch (Exception e) { Log.Info("Cinema: parar o serviço: " + e.Message); }
    }

    /// <summary>A pessoa tocou em "Encerrar" na notificação.</summary>
    public static event Action? PediramEncerrar;

    /// <summary>Muda a linha de baixo da notificação (quantas pessoas, enviando o arquivo...).</summary>
    public static void Atualizar(string texto) => _atual?.Notificar(texto);

    string _titulo = "";
    long _desdeMs;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == AcaoEncerrar)
        {
            PediramEncerrar?.Invoke();
            if (!FilmeActivity.AnfitriaoAberto) { StopForeground(StopForegroundFlags.Remove); StopSelf(); }
            return StartCommandResult.NotSticky;
        }
        if (intent == null) { StopSelf(); return StartCommandResult.NotSticky; }
        _atual = this;
        _titulo = intent.GetStringExtra(ExtraTitulo) ?? "";
        _desdeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var canal = new NotificationChannel(Canal, "Cinema", NotificationImportance.Low) { Description = "Aparece enquanto você está numa sessão do Cinema." };
            canal.SetShowBadge(false);
            ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(canal);
            var n = Montar("Esperando amigos");
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(IdNotificacao, n, ForegroundService.TypeMediaPlayback);
            else StartForeground(IdNotificacao, n);
        }
        catch (Exception e) { Log.Info("Cinema: primeiro plano: " + e.Message); }
        return StartCommandResult.NotSticky;
    }

    void Notificar(string texto)
    {
        try { ((NotificationManager)GetSystemService(NotificationService)!).Notify(IdNotificacao, Montar(texto)); }
        catch (Exception e) { Log.Info("Cinema: notificação: " + e.Message); }
    }

    Notification Montar(string texto)
    {
        var abrir = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(FilmeActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var encerrar = PendingIntent.GetService(this, 1, new Intent(this, typeof(ServicoCinema)).SetAction(AcaoEncerrar), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var icone = Icon.CreateWithResource(this, Resource.Drawable.ic_ao_vivo)!;
        return new Notification.Builder(this, Canal)
            .SetSmallIcon(icone)!
            .SetContentTitle($"Você está no Cinema: {_titulo}")!
            .SetContentText(texto)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetWhen(_desdeMs)!
            .SetShowWhen(true)!
            .SetContentIntent(abrir)!
            .AddAction(new Notification.Action.Builder(icone, "Encerrar", encerrar).Build())!
            .Build()!;
    }

    public override void OnDestroy()
    {
        if (_atual == this) _atual = null;
        base.OnDestroy();
    }
}
