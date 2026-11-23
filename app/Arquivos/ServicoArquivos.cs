using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using LogApp = Streemu.Util.Log;

[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission(Android.Manifest.Permission.WakeLock)]

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Mantém o app vivo enquanto há envio ou recebimento de arquivo (spec 030, T2 e T6): serviço em primeiro plano do tipo <c>dataSync</c>, com a
/// notificação de andamento, um WakeLock parcial e um WifiLock acordados só enquanto houver transferência. O fluxo de verdade mora no
/// <see cref="Streemu.Arquivos.FluxoDeArquivos"/>; o serviço só segura o processo e a rede (como o <c>ServicoCinema</c>).
/// <para>
/// <b>Só começa por uma ação da pessoa</b> (oferecer, aceitar, tocar em Aceitar na notificação): o Android 12+ não deixa começar serviço em
/// primeiro plano com o app em segundo plano, e o 15+ dá 6 h por 24 h somadas a serviços <c>dataSync</c>. Ao fim do limite o sistema chama
/// <see cref="OnTimeout"/>: o serviço para em poucos segundos (senão o app cai) e avisa "o Android pausou, toque para continuar"; tocar abre o
/// app, o que zera o relógio, e <see cref="EnvioDeArquivos.AoVoltarParaFrente"/> religa o serviço se ainda houver pedido andando.
/// </para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class ServicoArquivos : Service
{
    static ServicoArquivos? _atual;
    PowerManager.WakeLock? _wake;
    WifiManager.WifiLock? _wifi;

    public static bool Rodando => _atual != null;

    /// <summary>Chamado com o app na frente (ou por uma ação de notificação, que o Android também aceita). Se o sistema recusar, a transferência segue só enquanto o app estiver aberto.</summary>
    internal static void Iniciar(Context c)
    {
        try { c.StartForegroundService(new Intent(c, typeof(ServicoArquivos))); }
        catch (Exception e) { LogApp.Info($"[arquivo] não deu para iniciar o serviço ({e.GetType().Name}: {e.Message}); a transferência segue só com o app aberto"); }
    }

    internal static void Parar(Context c)
    {
        try { c.StopService(new Intent(c, typeof(ServicoArquivos))); }
        catch (Exception e) { LogApp.Info("[arquivo] parar o serviço: " + e.Message); }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _atual = this;
        try
        {
            NotificacaoArquivos.Preparar(this);
            var n = NotificacaoArquivos.Andamento(this, PresencaDeArquivos.TextoDeAndamento(), -1);
            // o tipo só existe a partir do Android 10; no 14+ o sistema exige que seja o declarado no manifesto
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificacaoArquivos.IdDoServico, n, ForegroundService.TypeDataSync);
            else StartForeground(NotificacaoArquivos.IdDoServico, n);
            Segurar();
        }
        catch (Exception e) { LogApp.Info($"[arquivo] primeiro plano: {e.GetType().Name}: {e.Message}"); }
        return StartCommandResult.NotSticky;
    }

    /// <summary>Android 15+: o limite de 6 h por 24 h do <c>dataSync</c> acabou. Em poucos segundos o serviço tem de parar.</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        base.OnTimeout(startId, fgsType);
        LogApp.Info("[arquivo] o Android deu o tempo do serviço por encerrado (6 h por 24 h).");
        PresencaDeArquivos.AoTimeoutDoSistema(this);
        StopSelf();
    }

    void Segurar()
    {
        if (_wake == null)
        {
            _wake = (GetSystemService(PowerService) as PowerManager)?.NewWakeLock(WakeLockFlags.Partial, "streemu:arquivos");
            _wake?.SetReferenceCounted(false);
        }
        if (_wake is { IsHeld: false }) _wake.Acquire();
        if (_wifi == null && ApplicationContext?.GetSystemService(WifiService) is WifiManager wifi)
        {
            // sem isto o Wi-Fi dorme com a tela apagada e a transferência cai
            _wifi = OperatingSystem.IsAndroidVersionAtLeast(29) ? wifi.CreateWifiLock(WifiMode.FullLowLatency, "streemu:arquivos") : wifi.CreateWifiLock(WifiMode.FullHighPerf, "streemu:arquivos");
            _wifi?.SetReferenceCounted(false);
        }
        if (_wifi is { IsHeld: false }) _wifi.Acquire();
    }

    public override void OnDestroy()
    {
        try { if (_wake is { IsHeld: true }) _wake.Release(); } catch (Exception e) { LogApp.Info("[arquivo] soltar WakeLock: " + e.Message); }
        try { if (_wifi is { IsHeld: true }) _wifi.Release(); } catch (Exception e) { LogApp.Info("[arquivo] soltar WifiLock: " + e.Message); }
        if (_atual == this) _atual = null;
        base.OnDestroy();
    }
}
