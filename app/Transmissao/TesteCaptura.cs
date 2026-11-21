using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;

#if DEBUG
namespace Streemu.Proto.Transmissao;

/// <summary>
/// Só no Debug: testa a <see cref="Captura"/> sem sala. Abre assim (sem launcher):
/// <c>adb shell am start -n br.com.streemu.proto.dev/br.com.streemu.proto.TesteCaptura --ei segundos 20 --ei w 1920 --ei h 1080 --ei fps 60 --ei kbps 12000 --ez audio true</c>
/// Pede o consentimento da captura (e o microfone, para o áudio dos apps), roda o <see cref="TesteCapturaServico"/> por N segundos e grava em files/:
/// captura.h264 (Annex-B cru), captura.opus (Ogg Opus) e captura.csv (tipo,pts_us,bytes,chave de cada quadro/pacote).
/// </summary>
[Activity(Name = "br.com.streemu.proto.TesteCaptura", Label = "Teste captura", Exported = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar")]
public class TesteCapturaActivity : Activity
{
    const int PedidoCaptura = 1, PedidoMicrofone = 2;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        SetContentView(new Android.Widget.TextView(this) { Text = "Teste da captura..." });
        if (Intent!.GetBooleanExtra("audio", true) && CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            RequestPermissions([Android.Manifest.Permission.RecordAudio, Android.Manifest.Permission.PostNotifications], PedidoMicrofone);
        else PedirCaptura();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) => PedirCaptura();

    void PedirCaptura()
    {
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), PedidoCaptura);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PedidoCaptura) return;
        if (resultCode != Result.Ok || data == null) { Android.Util.Log.Info("Streemu", "TESTE: consentimento negado."); Finish(); return; }
        var i = new Intent(this, typeof(TesteCapturaServico));
        i.PutExtras(Intent!);
        i.PutExtra("codigo", (int)resultCode);
        i.PutExtra("dados", data);
        StartForegroundService(i);
        Finish();
    }
}

[Service(Name = "br.com.streemu.proto.TesteCapturaServico", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class TesteCapturaServico : Service
{
    static bool _logLigado;
    Captura? _captura;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent == null) { StopSelf(); return StartCommandResult.NotSticky; }
        if (!_logLigado) { _logLigado = true; Streemu.Util.Log.Line += l => Android.Util.Log.Info("Streemu", l); }

        // Android 14+: startForeground (tipo mediaProjection) ANTES de GetMediaProjection
        var canal = new NotificationChannel("teste-captura", "Teste da captura", NotificationImportance.Low);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(canal);
        var nota = new Notification.Builder(this, "teste-captura").SetContentTitle("Streemu: teste da captura").SetSmallIcon(Android.Resource.Drawable.IcMediaPlay).Build();
        StartForeground(77, nota, ForegroundService.TypeMediaProjection);

        var dados = (Intent?)intent.GetParcelableExtra("dados");
        int codigo = intent.GetIntExtra("codigo", 0);
        int segundos = intent.GetIntExtra("segundos", 20);
        var cfg = new ConfigCaptura(intent.GetIntExtra("w", 1920), intent.GetIntExtra("h", 1080), intent.GetIntExtra("fps", 60), intent.GetIntExtra("kbps", 12000), intent.GetBooleanExtra("audio", true));
        new Thread(() => Rodar(codigo, dados, cfg, segundos)) { IsBackground = true, Name = "teste-captura" }.Start();
        return StartCommandResult.NotSticky;
    }

    void Rodar(int codigo, Intent? dados, ConfigCaptura cfg, int segundos)
    {
        MediaProjection? projecao = null;
        FileStream? h264 = null, csv = null;
        OggOpus? ogg = null;
        try
        {
            var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            projecao = mpm.GetMediaProjection(codigo, dados!);
            string dir = FilesDir!.AbsolutePath;
            h264 = File.Create(Path.Combine(dir, "captura.h264"));
            csv = File.Create(Path.Combine(dir, "captura.csv"));
            ogg = new OggOpus(Path.Combine(dir, "captura.opus"));
            var gate = new object();
            var texto = new System.Text.StringBuilder();
            void Linha(string s) { lock (gate) { texto.Append(s).Append('\n'); if (texto.Length > 4000) { var b = System.Text.Encoding.ASCII.GetBytes(texto.ToString()); csv!.Write(b); texto.Clear(); } } }

            _captura = new Captura(this, projecao, cfg);
            var done = new ManualResetEventSlim();
            _captura.Falhou += m => { Android.Util.Log.Info("Streemu", $"TESTE falhou: {m}"); done.Set(); };
            _captura.Video += (d, pts, chave) => { lock (gate) h264.Write(d); Linha($"v,{pts},{d.Length},{(chave ? 1 : 0)}"); };
            _captura.Audio += (d, pts) => { ogg.Escrever(d); Linha($"a,{pts},{d.Length},0"); };
            _captura.Iniciar();
            done.Wait(segundos * 1000);
        }
        catch (Exception e) { Android.Util.Log.Error("Streemu", $"TESTE erro: {e}"); }
        finally
        {
            try { _captura?.Dispose(); } catch { }
            try { projecao?.Stop(); } catch { }
            try { h264?.Dispose(); csv?.Dispose(); ogg?.Dispose(); } catch { }
            Android.Util.Log.Info("Streemu", "TESTE pronto");
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
    }

    /// <summary>Ogg Opus mínimo (um pacote por página), para o ffmpeg do PC abrir.</summary>
    sealed class OggOpus : IDisposable
    {
        readonly FileStream _f;
        readonly object _gate = new();
        uint _seq;
        long _granule;
        byte[]? _ultimo;   // o último pacote espera para sair com o sinal de fim
        static readonly uint[] Tabela = Crc();

        public OggOpus(string caminho)
        {
            _f = File.Create(caminho);
            var cab = new List<byte>("OpusHead"u8.ToArray()) { 1, 2 };
            cab.AddRange(BitConverter.GetBytes((ushort)312)); cab.AddRange(BitConverter.GetBytes(48000)); cab.AddRange(BitConverter.GetBytes((ushort)0)); cab.Add(0);
            Pagina(cab.ToArray(), 0, 0x02);
            var tags = new List<byte>("OpusTags"u8.ToArray());
            tags.AddRange(BitConverter.GetBytes(7)); tags.AddRange("streemu"u8.ToArray()); tags.AddRange(BitConverter.GetBytes(0));
            Pagina(tags.ToArray(), 0, 0);
        }

        public void Escrever(byte[] pacote)
        {
            lock (_gate)
            {
                if (_ultimo != null) { _granule += Amostras(_ultimo); Pagina(_ultimo, _granule, 0); }
                _ultimo = pacote;
            }
        }

        static int Amostras(byte[] p)
        {
            int cfg = p[0] >> 3, c = p[0] & 3;
            int por = cfg < 12 ? new[] { 480, 960, 1920, 2880 }[cfg & 3] : cfg < 16 ? new[] { 480, 960 }[cfg & 1] : new[] { 120, 240, 480, 960 }[cfg & 3];
            int quadros = c == 0 ? 1 : c == 3 ? (p.Length > 1 ? p[1] & 0x3F : 1) : 2;
            return por * quadros;
        }

        void Pagina(byte[] dados, long granule, byte tipo)
        {
            var seg = new List<byte>();
            int resto = dados.Length;
            while (resto >= 255) { seg.Add(255); resto -= 255; }
            seg.Add((byte)resto);
            var pag = new List<byte>("OggS"u8.ToArray()) { 0, tipo };
            pag.AddRange(BitConverter.GetBytes(granule)); pag.AddRange(BitConverter.GetBytes(0x53544D55u)); pag.AddRange(BitConverter.GetBytes(_seq++));
            pag.AddRange([0, 0, 0, 0]); pag.Add((byte)seg.Count); pag.AddRange(seg); pag.AddRange(dados);
            var a = pag.ToArray();
            uint crc = 0;
            foreach (var b in a) crc = (crc << 8) ^ Tabela[(byte)(crc >> 24) ^ b];
            BitConverter.GetBytes(crc).CopyTo(a, 22);
            _f.Write(a);
        }

        static uint[] Crc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint r = i << 24; for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1; t[i] = r; }
            return t;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_ultimo != null) { _granule += Amostras(_ultimo); Pagina(_ultimo, _granule, 0x04); _ultimo = null; }
                _f.Dispose();
            }
        }
    }
}
#endif
