using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Streemu.Util;

// As permissões FOREGROUND_SERVICE, FOREGROUND_SERVICE_MEDIA_PROJECTION, RECORD_AUDIO e POST_NOTIFICATIONS NÃO são declaradas aqui: o código da captura
// (Captura.cs, por atributo [assembly: UsesPermission]) declara. Sem elas no manifesto, o StartForeground falha no Android 14+.

namespace Streemu.Proto.Transmissao;

/// <summary>A qualidade da transmissão (v1: três escolhas simples). O maior lado cabe numa caixa 16:9 e a imagem mantém a proporção da tela, sem esticar.</summary>
sealed record Qualidade(string Nome, int LadoMaior, int Fps, int Kbps)
{
    public static readonly Qualidade[] Todas =
    {
        // Só 60 fps: medido em 21/11 (S23 transmitindo, S26 assistindo, c2.exynos.h264.decoder), a 30 fps o decodificador segura cada quadro
        // 75 a 92 ms (576p ou 880p, igual); a 60 fps, 11 a 12 ms. O celular que assiste já publicado não muda, então quem transmite manda 60.
        new("720p", 1280, 60, 5000),
        new("1080p", 1920, 60, 8000),
        new("1080p alta", 1920, 60, 12000),
    };
    public const int Padrao = 1;   // 1080p60 a 8000 kbps

    public static Qualidade Escolhida(Context c)
    {
        int i = c.GetSharedPreferences("streemu", FileCreationMode.Private)!.GetInt("qualidade_tx", Padrao);
        return Todas[Math.Clamp(i, 0, Todas.Length - 1)];
    }

    public static int IndiceEscolhido(Context c) => Array.IndexOf(Todas, Escolhida(c));

    public static void Escolher(Context c, int indice) =>
        c.GetSharedPreferences("streemu", FileCreationMode.Private)!.Edit()!.PutInt("qualidade_tx", Math.Clamp(indice, 0, Todas.Length - 1))!.Apply();

    /// <summary>Largura e altura (múltiplas de 16, que o encoder gosta) para uma tela de <paramref name="telaL"/> x <paramref name="telaA"/>: cabe na caixa, na orientação da tela, e nunca amplia.</summary>
    public (int L, int A) Dimensoes(int telaL, int telaA)
    {
        int longo = LadoMaior, curto = LadoMaior * 9 / 16;
        bool emPe = telaA > telaL;
        int caixaL = emPe ? curto : longo, caixaA = emPe ? longo : curto;
        double s = Math.Min(1.0, Math.Min((double)caixaL / telaL, (double)caixaA / telaA));
        return (Math.Max(16, (int)(telaL * s) / 16 * 16), Math.Max(16, (int)(telaA * s) / 16 * 16));
    }

    /// <summary>O tamanho real da tela em pixels (a captura pega a tela toda, com as barras do sistema).</summary>
    public static (int L, int A) TelaReal(Context c)
    {
        // DisplayManager, não WindowManager: quem chama é o serviço, e o WindowManager de um contexto sem janela é frágil no Android 11+.
        // JavaCast, não cast C#: o objeto vem do Java (o cast direto dava InvalidCastException, medido no S23).
        var dm = c.GetSystemService(Context.DisplayService)!.JavaCast<Android.Hardware.Display.DisplayManager>();
        var tela = dm.GetDisplay(Display.DefaultDisplay)!;
        var p = new Point();
#pragma warning disable CA1422
        tela.GetRealSize(p);
#pragma warning restore CA1422
        return (p.X, p.Y);
    }
}

/// <summary>
/// A transmissão do celular, em primeiro plano (sem isso o Android corta a captura quando o app sai da tela, que é o uso normal: a pessoa abre o jogo).
/// Dono da <see cref="MediaProjection"/>, da <see cref="Captura"/> e do <see cref="Anfitriao"/>. A tela só conversa com o estado estático (<see cref="AoVivo"/>,
/// <see cref="Assistindo"/>, <see cref="Erro"/>, <see cref="Mudou"/>), que pode mudar em qualquer thread.
///
/// Ordem do Android 14+: StartForeground (tipo mediaProjection) ANTES de GetMediaProjection, e o MediaProjection.Callback registrado ANTES de a captura criar o VirtualDisplay.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class ServicoTransmissao : Service
{
    public const string AcaoParar = "br.com.streemu.proto.PARAR_TRANSMISSAO";
    const string Canal = "transmissao", ExtraCodigo = "resultCode", ExtraDados = "dados", ExtraQualidade = "qualidade", ExtraAudio = "audio";
    const int IdNotificacao = 4201;

    static ServicoTransmissao? _atual;
    static volatile bool _aoVivo, _preparando;
    static volatile int _assistindo;
    static volatile string? _erro;

    /// <summary>Entrou no ar (a live está anunciada na sala).</summary>
    public static bool AoVivo => _aoVivo;
    /// <summary>A pessoa autorizou a captura e o serviço está abrindo tudo.</summary>
    public static bool Preparando => _preparando;
    public static int Assistindo => _assistindo;
    /// <summary>Por que a última transmissão parou sozinha (nulo se a pessoa parou ou nunca houve). Some quando começa outra.</summary>
    public static string? Erro => _erro;
    /// <summary>Qualquer parte do estado mudou. Vem de qualquer thread: quem usa posta na tela.</summary>
    public static event Action? Mudou;

    /// <summary>Chamado pela tela com o resultado do pedido de captura (CreateScreenCaptureIntent do MediaProjectionManager). Ainda na thread da tela.</summary>
    public static void Iniciar(Context c, int resultCode, Intent dados, int qualidade, bool audio)
    {
        if (_preparando || _aoVivo) return;
        _erro = null;
        _preparando = true;
        Mudou?.Invoke();
        try
        {
            var i = new Intent(c, typeof(ServicoTransmissao)).PutExtra(ExtraCodigo, resultCode).PutExtra(ExtraDados, dados)
                .PutExtra(ExtraQualidade, qualidade).PutExtra(ExtraAudio, audio);
            c.StartForegroundService(i);
        }
        catch (Exception e)
        {
            Log.Info("Transmissão: não deu para iniciar o serviço: " + e.Message);
            _preparando = false; _erro = "Não deu para iniciar a transmissão: " + e.Message;
            Mudou?.Invoke();
        }
    }

    /// <summary>A pessoa tocou em "Parar transmissão". Avisa os espectadores e fecha tudo.</summary>
    public static void Parar() => _atual?.Encerrar(null);

    // ------------------------------------------------------------------ o serviço

    MediaProjection? _projecao;
    Captura? _captura;
    Anfitriao? _anfitriao;
    ProjecaoCb? _cb;
    string _grupo = "";
    int _encerrando;
    long _desdeMs;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == AcaoParar) { Encerrar(null); return StartCommandResult.NotSticky; }
        if (intent == null || _atual != null) { if (_atual == null) StopSelf(); return StartCommandResult.NotSticky; }
        _atual = this;
        _grupo = Sessao.NomeDoGrupo;
        _desdeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            CriarCanal();
            var n = Notificacao("Preparando a transmissão...");
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(IdNotificacao, n, ForegroundService.TypeMediaProjection);
            else StartForeground(IdNotificacao, n);
            var dados = Dados(intent);
            int codigo = intent.GetIntExtra(ExtraCodigo, 0);
            if (dados == null) throw new InvalidOperationException("faltou a autorização da captura");
            var gerente = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            _projecao = gerente.GetMediaProjection(codigo, dados) ?? throw new InvalidOperationException("o Android não deu a captura");
            _cb = new ProjecaoCb(this);
            _projecao.RegisterCallback(_cb, new Handler(Looper.MainLooper!));
            var q = Qualidade.Todas[Math.Clamp(intent.GetIntExtra(ExtraQualidade, Qualidade.Padrao), 0, Qualidade.Todas.Length - 1)];
            bool audio = intent.GetBooleanExtra(ExtraAudio, false);
            _ = Task.Run(() => Abrir(q, audio));
        }
        catch (Exception e) { Falhou(e); }
        return StartCommandResult.NotSticky;
    }

    static Intent? Dados(Intent intent)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) return (Intent?)intent.GetParcelableExtra(ExtraDados, Java.Lang.Class.FromType(typeof(Intent)));
#pragma warning disable CA1422
        return intent.GetParcelableExtra(ExtraDados) as Intent;
#pragma warning restore CA1422
    }

    /// <summary>Fora da thread da tela: a captura abre o encoder, e o anfitrião fala com a sala.</summary>
    void Abrir(Qualidade q, bool audio)
    {
        try
        {
            var (tl, ta) = Qualidade.TelaReal(this);
            var (l, a) = q.Dimensoes(tl, ta);
            Log.Info($"Transmissão: tela {tl}x{ta}, pedido {q.Nome} ({l}x{a}, {q.Kbps} kbps), áudio {(audio ? "pedido" : "sem permissão")}.");
            audio &= OperatingSystem.IsAndroidVersionAtLeast(29);   // o som dos apps (AudioPlaybackCapture) é do Android 10
            var captura = _captura = new Captura(this, _projecao!, new ConfigCaptura(l, a, q.Fps, q.Kbps, audio));
            captura.Falhou += texto => Encerrar(texto);
            // se o Android não deu o áudio, a captura diz 0 canais: o mux não anuncia uma faixa que nunca virá
            var mux = new TsMux(comAudio: audio && captura.CanaisAudio > 0, canaisAudio: captura.CanaisAudio is 1 or 2 ? captura.CanaisAudio : 2);
            var anfitriao = _anfitriao = new Anfitriao(captura, mux, q.Kbps);
            anfitriao.Encerrou += texto => Encerrar(texto);
            anfitriao.Mudou += AoMudarEspectadores;
            captura.Iniciar();   // antes do anfitrião: a qualidade que vai para a sala é o tamanho que a captura aceitou
            anfitriao.Iniciar();
            if (_encerrando != 0) return;   // pararam durante a abertura: o Encerrar já cuida
            _aoVivo = true; _preparando = false;
            AoMudarEspectadores();
        }
        catch (Exception e) { Falhou(e); }
    }

    void AoMudarEspectadores()
    {
        if (_encerrando != 0) return;
        int n = _anfitriao?.Assistindo ?? 0;
        _assistindo = n;
        try { ((NotificationManager)GetSystemService(NotificationService)!).Notify(IdNotificacao, Notificacao(n == 0 ? "Esperando amigos" : n == 1 ? "1 pessoa assistindo" : $"{n} pessoas assistindo")); }
        catch (Exception e) { Log.Info("Transmissão: notificação: " + e.Message); }
        Mudou?.Invoke();
    }

    void Falhou(Exception e)
    {
        Log.Info("Transmissão: falhou ao abrir: " + e);
        Encerrar("Não deu para transmitir: " + e.Message);
    }

    /// <summary>
    /// Fecha tudo, uma vez: avisa os espectadores (parar), tira a live da sala, solta a captura e a projeção e sai do primeiro plano.
    /// <paramref name="erro"/> nulo = a pessoa parou. Pode vir de qualquer thread (botão, notificação, projeção revogada, captura que caiu, sala que fechou).
    /// </summary>
    void Encerrar(string? erro)
    {
        if (Interlocked.Exchange(ref _encerrando, 1) != 0) return;
        Log.Info(erro == null ? "Transmissão: parada pela pessoa." : "Transmissão: encerrada. " + erro);
        _ = Task.Run(() =>
        {
            try { _anfitriao?.Parar(); } catch (Exception e) { Log.Info("Transmissão: parar anfitrião: " + e.Message); }
            try { _captura?.Dispose(); } catch (Exception e) { Log.Info("Transmissão: parar captura: " + e.Message); }
            try
            {
                if (_cb != null) _projecao?.UnregisterCallback(_cb);   // antes do Stop: o Stop chamaria o nosso OnStop
                _projecao?.Stop();
            }
            catch (Exception e) { Log.Info("Transmissão: soltar a projeção: " + e.Message); }
            _aoVivo = false; _preparando = false; _assistindo = 0; _erro = erro;
            _atual = null;
            Mudou?.Invoke();
            new Handler(Looper.MainLooper!).Post(() =>
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            });
        });
    }

    public override void OnDestroy()
    {
        // o sistema matou o serviço sem passar pelo Encerrar
        if (_encerrando == 0) Encerrar("A transmissão foi encerrada pelo sistema.");
        base.OnDestroy();
    }

    /// <summary>A pessoa revogou a captura (barra de status do Android 14+, "parar de compartilhar") ou o sistema a tirou: a live acaba.</summary>
    sealed class ProjecaoCb(ServicoTransmissao s) : MediaProjection.Callback
    {
        public override void OnStop() => s.Encerrar("O Android parou a captura da tela.");
    }

    // ------------------------------------------------------------------ notificação

    void CriarCanal()
    {
        var canal = new NotificationChannel(Canal, "Transmissão ao vivo", NotificationImportance.Low) { Description = "Aparece enquanto você transmite a tela do celular." };
        canal.SetShowBadge(false);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(canal);
    }

    Notification Notificacao(string texto)
    {
        var abrir = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var parar = PendingIntent.GetService(this, 1, new Intent(this, typeof(ServicoTransmissao)).SetAction(AcaoParar), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var icone = Icon.CreateWithResource(this, Resource.Drawable.ic_ao_vivo)!;
        var acao = new Notification.Action.Builder(icone, "Parar", parar).Build();
        return new Notification.Builder(this, Canal)
            .SetSmallIcon(icone)!
            .SetContentTitle(_grupo.Length > 0 ? $"Você está ao vivo no {_grupo}" : "Você está ao vivo")!
            .SetContentText(texto)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetWhen(_desdeMs)!
            .SetShowWhen(true)!
            .SetUsesChronometer(true)!
            .SetContentIntent(abrir)!
            .AddAction(acao)!
            .Build()!;
    }
}
