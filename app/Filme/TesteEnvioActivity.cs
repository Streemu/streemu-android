#if DEBUG
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// Só Debug. Roda a <see cref="BancadaEnvio"/> com um arquivo do aparelho e escreve no logcat (tag Streemu, linhas BANCADA).
/// adb shell am start -n br.com.streemu.proto.spike/streemu.proto.filme.TesteEnvioActivity --es arquivo /data/local/tmp/bancada.mkv --ei espectadores 3 --ei segundos 60 --ei pos 600 --ei tocar 1
/// <c>tocar 1</c> toca o arquivo pelo ExoPlayer ao mesmo tempo (a carga de decodificação do anfitrião); <c>espectadores 0</c> mede só a leitura da fonte.
/// </summary>
// ConfigurationChanges: sem isso o Android recria a tela ao girar para a horizontal, o OnDestroy cancela a rodada e o OnCreate começa outra por cima (as rodadas se atropelavam e os receptores caíam)
[Activity(Name = "streemu.proto.filme.TesteEnvioActivity", Label = "Teste envio", Exported = true,
          Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", ScreenOrientation = ScreenOrientation.Landscape,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class TesteEnvioActivity : Android.App.Activity
{
    readonly CancellationTokenSource _cts = new();
    PlayerFilme? _player;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        var video = new SurfaceView(this);
        SetContentView(video, new FrameLayout.LayoutParams(-1, -1));
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        string arquivo = Intent?.GetStringExtra("arquivo") ?? "";
        int espectadores = Intent?.GetIntExtra("espectadores", 3) ?? 3, segundos = Intent?.GetIntExtra("segundos", 60) ?? 60, pos = Intent?.GetIntExtra("pos", 600) ?? 600;
        bool tocar = (Intent?.GetIntExtra("tocar", 0) ?? 0) == 1;
        FonteExtrator.ForcarDescritor = (Intent?.GetIntExtra("fd", 0) ?? 0) == 1;   // exercita o caminho do seletor (descritor) com um arquivo comum
        if (arquivo == "") { Logcat.Error("Streemu", "BANCADA sem o extra arquivo"); Finish(); return; }
        if (tocar) _player = new PlayerFilme(this, video, Android.Net.Uri.FromFile(new Java.IO.File(arquivo))!, comecarTocando: false);
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_player != null) { await _player.Ir(pos); await _player.Pausar(false); }
                BancadaEnvio.Rodar(this, arquivo, pos, espectadores, segundos, ct);
            }
            catch (Exception e) { Logcat.Error("Streemu", "BANCADA " + e); }
            finally { RunOnUiThread(Finish); }
        });
    }

    protected override void OnDestroy()
    {
        _cts.Cancel();
        _player?.Fechar();
        base.OnDestroy();
    }
}
#endif
