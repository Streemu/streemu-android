#if DEBUG
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Diagnostics;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// Só Debug. Toca um arquivo pelo PlayerFilme e escreve a posição no logcat (tag Streemu).
/// adb shell am start -n br.com.streemu.proto.dev/streemu.proto.filme.TesteFilmeActivity --es arquivo /data/local/tmp/x.mp4 --es roteiro 1
/// <para>
/// F3: <c>--es ts /data/local/tmp/x.ts</c> toca um MPEG-TS pelo cache da transmissão (<see cref="CacheTs"/> + <see cref="PlayerStream"/>) em vez do arquivo direto: um escritor
/// despeja o arquivo em pedaços de 1316 bytes como o SRT faria (<c>--ei ritmo</c> = vezes o tempo real, 0 = de uma vez; <c>--ei ram_mb</c> e <c>--ei anel_mb</c> baixam os tetos do cache
/// para passar para o arquivo; <c>--ei epoca2_s</c> = depois de tantos s, abre uma época nova com o arquivo de novo, como o anfitrião faz). <c>--es roteiro 2</c> é o roteiro de pulos do cache.
/// </para>
/// </summary>
[Activity(Name = "streemu.proto.filme.TesteFilmeActivity", Label = "Teste filme", Exported = true,
          Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", ScreenOrientation = ScreenOrientation.Landscape)]
public class TesteFilmeActivity : Android.App.Activity
{
    PlayerFilme? _player;
    PlayerStream? _stream;
    readonly CancellationTokenSource _cts = new();
    double _vel = 1.0;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        var sv = new SurfaceView(this);
        SetContentView(sv, new FrameLayout.LayoutParams(-1, -1));
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        var arquivo = Intent?.GetStringExtra("arquivo");
        var roteiro = Intent?.GetStringExtra("roteiro") == "1";
        if (Intent?.GetStringExtra("ts") is { Length: > 0 } ts) { TocarTs(sv, ts); return; }
        if (string.IsNullOrEmpty(arquivo)) { Logcat.Error("Streemu", "FILME sem extra arquivo"); return; }
        _player = new PlayerFilme(this, sv, Android.Net.Uri.FromFile(new Java.IO.File(arquivo))!);
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Registrar(ct); } catch (Exception e) when (e is not System.OperationCanceledException) { Logcat.Error("Streemu", "FILME registrar " + e); }
        });
        if (roteiro) _ = Task.Run(async () =>
        {
            try { await Roteiro(ct); } catch (Exception e) when (e is not System.OperationCanceledException) { Logcat.Error("Streemu", "FILME roteiro " + e); }
        });
    }

    // ------------------------------------------------------------------ F3: um TS pelo cache

    void TocarTs(SurfaceView sv, string ts)
    {
        var ct = _cts.Token;
        int ritmo = Intent?.GetIntExtra("ritmo", 4) ?? 4, ramMb = Intent?.GetIntExtra("ram_mb", 64) ?? 64, anelMb = Intent?.GetIntExtra("anel_mb", 512) ?? 512, epoca2 = Intent?.GetIntExtra("epoca2_s", 0) ?? 0;
        string roteiro = Intent?.GetStringExtra("roteiro") ?? "";
        var ps = _stream = new PlayerStream(this, sv, comecarTocando: false) { LimiteRam = ramMb * 1024L * 1024, CapArquivo = anelMb * 1024L * 1024 };
        ps.Recarregar += () => Logcat.Info("Streemu", "FILME (teste) recarregar pedido");
        IDisposable? Escritor(CacheTs c) => new EscritorDeArquivo(ts, c, ritmo);
        ps.Epoca(1, 0, Escritor);
        _ = Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    var c = ps.CacheAtual;
                    var (b, pl) = await ps.BaseEPlayerAsync();
                    var (estado, buf) = await ps.EstadoAsync();
                    Logcat.Info("Streemu", FormattableString.Invariant($"FILME ts t={sw.Elapsed.TotalSeconds:F1} pos={await ps.PosicaoAsync():F2} base={b:F2} player={pl:F2} estado={estado} buffer={buf:F1} cache={(c?.Bytes ?? 0) / 1048576.0:F1}MB arquivo={c?.EmArquivo} chaves={c?.Chaves} pts={c?.PrimeiraChave:F1}..{c?.PtsMax:F1}"));
                    await Task.Delay(1000, ct);
                }
            }
            catch (Exception e) when (e is not System.OperationCanceledException) { Logcat.Error("Streemu", "FILME ts registrar " + e); }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                void L(string s) => Logcat.Info("Streemu", "FILME roteiro: " + s);
                while (ps.CacheAtual is not { } c0 || double.IsNaN(c0.PrimeiraChave)) await Task.Delay(100, ct);
                double p0 = ps.CacheAtual!.PrimeiraChave;
                L(FormattableString.Invariant($"primeiro quadro-chave em {p0:F2} s: Ir + tocar"));
                var sw = Stopwatch.StartNew();
                await ps.Ir(p0); await ps.EsperarIrAsync(); await ps.Pausar(false);
                L(FormattableString.Invariant($"tocando em {sw.ElapsedMilliseconds} ms, pos={await ps.PosicaoAsync():F2}"));
                if (epoca2 > 0)
                {
                    await Task.Delay(epoca2 * 1000, ct);
                    L(FormattableString.Invariant($"época 2 (o anfitrião reabriu o envio), tocando em {await ps.PosicaoAsync():F2}"));
                    ps.Epoca(2, 0, Escritor);
                }
                if (roteiro != "2") return;
                await Task.Delay(8000, ct);
                foreach (double d in new[] { 20.0, 3.0, 31.5, 9.0 })
                {
                    sw.Restart();
                    L(FormattableString.Invariant($"ir {p0 + d:F2} (+{d} s do primeiro quadro)"));
                    await ps.Pausar(true); await ps.Ir(p0 + d); bool ok = await ps.EsperarIrAsync();
                    L(FormattableString.Invariant($"ir pronto={ok} em {sw.ElapsedMilliseconds} ms, pos={await ps.PosicaoAsync():F3} (pedi {p0 + d:F3})"));
                    await ps.Pausar(false);
                    await Task.Delay(4000, ct);
                    L(FormattableString.Invariant($"4 s depois pos={await ps.PosicaoAsync():F3} (esperado ~{p0 + d + 4:F1})"));
                }
                L("ir além do que chegou");
                await ps.Pausar(true); sw.Restart(); await ps.Ir(p0 + 500); L($"Ir além voltou em {sw.ElapsedMilliseconds} ms (esperado ~15 s e um pedido de recarregar)");
            }
            catch (Exception e) when (e is not System.OperationCanceledException) { Logcat.Error("Streemu", "FILME ts roteiro " + e); }
        });
    }

    /// <summary>Despeja um arquivo TS no cache em pedaços de 1316 bytes, a <c>ritmo</c> vezes o tempo real (pelos bytes e um bitrate médio; o arquivo de teste tem 60 s), ou de uma vez.</summary>
    sealed class EscritorDeArquivo : IDisposable
    {
        volatile bool _parar;
        public EscritorDeArquivo(string caminho, CacheTs cache, int ritmo)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(caminho);
                    double bps = bytes.Length / 60.0;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < bytes.Length && !_parar; i += 1316)
                    {
                        int n = Math.Min(1316, bytes.Length - i);
                        var pedaco = new byte[n]; Buffer.BlockCopy(bytes, i, pedaco, 0, n);
                        cache.Escrever(pedaco, n);
                        if (ritmo > 0) { double devia = (i + n) / (bps * ritmo); int dorme = (int)((devia - sw.Elapsed.TotalSeconds) * 1000); if (dorme > 5) Thread.Sleep(dorme); }
                    }
                    cache.Terminar();
                    Logcat.Info("Streemu", $"FILME (teste) escritor acabou: {bytes.Length / 1048576.0:F1} MB em {sw.Elapsed.TotalSeconds:F1} s");
                }
                catch (Exception e) { cache.Terminar(e.Message); }
            }) { IsBackground = true };
            t.Start();
        }
        public void Dispose() => _parar = true;
    }

    async Task Registrar(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _player is { Saiu: false } p)
        {
            var pos = await p.PosicaoAsync();
            var aud = await p.PosicaoAudioAsync();
            Logcat.Info("Streemu", FormattableString.Invariant($"FILME pos={pos:F2} audio={aud:F2} vel={_vel:F2}"));
            await Task.Delay(1000, ct);
        }
    }

    async Task Roteiro(CancellationToken ct)
    {
        var p = _player!;
        void L(string s) => Logcat.Info("Streemu", "FILME roteiro: " + s);
        await Task.Delay(5000, ct);
        L("rajada de velocidades 1.00/1.04 a cada 250 ms por 5 s");
        for (int i = 0; i < 20; i++) { _vel = i % 2 == 0 ? 1.04 : 1.00; await p.Velocidade(_vel); await Task.Delay(250, ct); }
        await Task.Delay(2000, ct);
        L("velocidade 1.05"); _vel = 1.05; await p.Velocidade(1.05);
        await Task.Delay(3000, ct);
        L("velocidade 1.0"); _vel = 1.0; await p.Velocidade(1.0);
        var sw = Stopwatch.StartNew();
        L("ir 30"); await p.Ir(30); await p.EsperarIrAsync();
        L(FormattableString.Invariant($"ir 30 pronto em {sw.ElapsedMilliseconds} ms, pos={await p.PosicaoAsync():F2}"));
        L("pausar"); await p.Pausar(true);
        await Task.Delay(2000, ct);
        L(FormattableString.Invariant($"pausado pos={await p.PosicaoAsync():F2}"));
        L("retomar"); await p.Pausar(false);
    }

    protected override void OnDestroy()
    {
        _cts.Cancel();
        _player?.Fechar();
        _stream?.Fechar();
        base.OnDestroy();
    }
}
#endif
