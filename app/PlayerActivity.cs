using Android.Content;
using System.Text.Json.Nodes;
using Android.Content.PM;
using Android.Graphics;
using Android.Net;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Player;
using Streemu.Proto.Ui;

namespace Streemu.Proto;

/// <summary>
/// O player: toca de uma a quatro lives do grupo ao mesmo tempo (spec 003), em tela cheia. Cada live é uma <see cref="PecaDoPlayer"/> (pede para assistir
/// pela sala, como o app Windows, MainForm.Grupo.cs) com a sua <see cref="MolduraDaPeca"/>; aqui ficam só o ciclo de vida, a rede do celular e a tela.
/// Os controles aparecem com um toque e somem sozinhos em 3 s. Se a rede do celular muda (Wi-Fi para 4G), cada peça pede de novo com uma ligação nova.
/// No build Debug, a tela principal abre este player com o código do proto-envio, sem sala (o código do celular vai para o logcat como "MEU CÓDIGO").
/// </summary>
[Activity(Label = NomeApp.Nome, Exported = false, Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", ScreenOrientation = ScreenOrientation.User,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public partial class PlayerActivity : Android.App.Activity
{
    const int MaximoDePecas = 4;

    string? _codigoTeste;
    FrameLayout _raiz = null!, _area = null!;
    readonly Android.OS.Handler _ui = new(Android.OS.Looper.MainLooper!);
    readonly Java.Lang.Runnable _esconder;
    ConnectivityManager? _rede;
    Rede? _redeCallback;
    volatile bool _fechando;
    bool _playerIniciado;

    public PlayerActivity() { _esconder = new Java.Lang.Runnable(() => Controles(false)); }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Atualizacao.Iniciar(this);
        if (Atualizacao.BloqueiaSala) { Finish(); return; }
        _playerIniciado = true;
        Atualizacao.PlayerAbriu();
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        Telemetria.Ativo = true;
        var i = Intent!;
#if DEBUG
        // só no Debug: força a latência do SRT desta live, sem cálculo nem escalada (adb ... --ei latencia_srt 400)
        if (i.GetIntExtra("latencia_srt", 0) is > 0 and var forcada) PecaDoPlayer.LatenciaForcadaMs = forcada;
#endif
        // modo de teste sem sala: "codigo" (um proto-envio) ou "codigos" (vários, separados por vírgula: uma peça para cada, para testar a grade e o destaque)
        var codigos = (i.GetStringExtra("codigos") ?? i.GetStringExtra("codigo") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _codigoTeste = codigos.Length > 0 ? codigos[0] : null;
        Sessao.Iniciar(this);
        MontarTela();
        Sessao.Mensagem += AoReceber;
        Sessao.NaoEntregue += AoNaoEntregar;
        Sessao.Mudou += AoMudarSala;
        OuvirRede();
        Srt.srt_startup();
        if (codigos.Length == 0) { AbrirPeca(PecaDaSala(i)); return; }
        for (int k = 0; k < Math.Min(codigos.Length, MaximoDePecas); k++)
            AbrirPeca(new PecaDoPlayer(this, RunOnUiThread, $"teste{k + 1}", $"Teste {k + 1}", "", "1080p 60 FPS", 0, codigos[k]));
    }

    PecaDoPlayer PecaDaSala(Intent i) => new(this, RunOnUiThread, i.GetStringExtra("conta") ?? "", i.GetStringExtra("apelido") ?? "", i.GetStringExtra("jogo") ?? "",
                                             i.GetStringExtra("qualidade") ?? "", i.GetLongExtra("desde", 0), null);

    // ================================================================================================ tela

    void MontarTela()
    {
        _raiz = new FrameLayout(this);
        _raiz.SetBackgroundColor(Color.Black);
        _area = new FrameLayout(this);
        _area.LayoutChange += (_, e) =>
        {
            if (e.Right - e.Left != e.OldRight - e.OldLeft || e.Bottom - e.Top != e.OldBottom - e.OldTop) Organizar();
        };
        _raiz.AddView(_area, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio));
        MontarControles();
        // as margens do recorte da câmera só para os controles: o vídeo ocupa a tela toda
        _raiz.SetOnApplyWindowInsetsListener(new Recorte(this));
        SetContentView(_raiz);
        TelaCheia();
    }

    void TelaCheia()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35)) Window!.SetDecorFitsSystemWindows(false);   // no 15+ já é assim
            Window!.InsetsController?.Hide(WindowInsets.Type.SystemBars());
            Window.InsetsController!.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
        else
#pragma warning disable CA1422
            Window!.DecorView.SystemUiFlags = SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
#pragma warning restore CA1422
    }

    // ================================================================================================ a sala e a rede do celular

    // a mensagem vem do PC de outra pessoa; cada peça confere a conta e a sessão dela, então o "parar" de uma live não derruba as outras
    void AoReceber(string de, string apelido, JsonObject msg) => RunOnUiThread(() => { foreach (var m in _molduras.ToList()) m.Peca.AoReceber(de, apelido, msg); });

    void AoNaoEntregar(string para, JsonObject? msg) => RunOnUiThread(() => { foreach (var m in _molduras.ToList()) m.Peca.AoNaoEntregar(para, msg); });

    void AoMudarSala() => RunOnUiThread(() => { foreach (var m in _molduras.ToList()) m.Peca.AoMudarSala(); });

    void OuvirRede()
    {
        _rede = (ConnectivityManager?)GetSystemService(ConnectivityService);
        if (_rede == null) return;
        _redeCallback = new Rede(this);
        _rede.RegisterDefaultNetworkCallback(_redeCallback);
    }

    sealed class Rede(PlayerActivity a) : ConnectivityManager.NetworkCallback
    {
        Network? _atual;
        public override void OnAvailable(Network network)
        {
            var antes = _atual;
            _atual = network;
            if (antes == null || antes.Equals(network)) return;   // a primeira avisada é a rede de agora
            a.RunOnUiThread(() => { if (!a._fechando) foreach (var m in a._molduras.ToList()) m.Peca.AoMudarRede(); });
        }
    }

    // ================================================================================================ ciclo de vida

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) TelaCheia();
    }

    /// <summary>Saiu do app (tela inicial, outro app): para de assistir. Uma live tocando escondida gasta bateria e dados.</summary>
    protected override void OnStop()
    {
        base.OnStop();
        if (!IsChangingConfigurations) Finish();
    }

    protected override void OnDestroy()
    {
        if (!_playerIniciado) { base.OnDestroy(); return; }
        _fechando = true;
        _editorProporcao?.Dismiss();
        _avisoControle?.Cancel();
        _ui.RemoveCallbacks(_esconder);
        Sessao.Mensagem -= AoReceber;
        Sessao.NaoEntregue -= AoNaoEntregar;
        Sessao.Mudou -= AoMudarSala;
        Telemetria.Ativo = false;
        try { if (_redeCallback != null) _rede?.UnregisterNetworkCallback(_redeCallback); } catch { }
        foreach (var m in _molduras) m.Peca.Fechar();
        Atualizacao.PlayerFechou();
        base.OnDestroy();
    }
}
