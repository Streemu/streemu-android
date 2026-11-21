using Stopwatch = System.Diagnostics.Stopwatch;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Ui;
using LogApp = Streemu.Util.Log;
using Logcat = Android.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// O player: pede para assistir pela sala do grupo (o mesmo pedido do app Windows, MainForm.Grupo.cs) e toca vídeo e som em tela cheia, deitado.
/// Os controles aparecem com um toque e somem sozinhos em 3 s. Se a rede do celular muda (Wi-Fi para 4G), pede de novo com uma ligação nova.
/// No build Debug, a tela principal abre este player com o código do proto-envio, sem sala (o código do celular vai para
/// o logcat como "MEU CÓDIGO").
/// </summary>
[Activity(Label = "Streemu", Exported = false, Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", ScreenOrientation = ScreenOrientation.SensorLandscape,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class PlayerActivity : Android.App.Activity, ISurfaceHolderCallback
{
    const int LatenciaMs = 80;   // a mesma do app no PC

    // quem assiste (vem do Início)
    string _conta = "", _apelido = "", _sala = "", _jogo = "", _qualidade = "";
    long _desde;
    string? _codigoTeste;

    FrameLayout _raiz = null!;
    SurfaceView _video = null!;
    LinearLayout _espera = null!, _barra = null!, _botoes = null!, _acoesErro = null!;
    ProgressBar _giro = null!;
    TextView _estado = null!, _detalhe = null!, _numeros = null!;
    ImageView _icSom = null!;
    readonly Android.OS.Handler _ui = new(Android.OS.Looper.MainLooper!);
    readonly Java.Lang.Runnable _esconder;
    bool _controlesVisiveis, _mostrarNumeros, _mudo;

    // a ligação
    PeerLink? _link;
    bool _pedindo;
    int _tentativa;   // cada pedido novo invalida as esperas do anterior
    Tunnel? _tunel;
    LocalRelay? _relay;
    Decodificador? _dec;
    Som? _som;
    /// <summary>Cria e solta o decodificador e o som: sem isto, a thread do SRT podia criar um depois do OnDestroy, e ele tocava para sempre.</summary>
    readonly object _midia = new();
    readonly TsDemux _demux = new();
    Surface? _surface;
    volatile bool _fechando, _tocando;
    int _srtLigado;
    int _videoW, _videoH;
    long? _utcMenosQpcPc;
    readonly Amostras _atrasoChegada = new(), _atrasoTela = new();
    // o que o decodificador do celular promete para a qualidade desta live (Aparelho.AguentaH264)
    Aparelho.Aguenta _aguenta = Aparelho.Aguenta.NaoSei;
    string _capacidade = "";
    TextView _aviso = null!, _avisoVideo = null!;
    ConnectivityManager? _rede;
    // a ligação de agora para a telemetria: live (ao pedir), midia_tentativa (o decodificador abriu), midia_video_ok (primeiro quadro), live_fim
    string? _ligacaoTel, _transmissaoTel;
    long _ligacaoDesde, _aceitoEm;
    bool _videoOkTel;
    int _tentativaMidia;
    Rede? _redeCallback;
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
        _codigoTeste = i.GetStringExtra("codigo");
        _conta = i.GetStringExtra("conta") ?? ""; _apelido = i.GetStringExtra("apelido") ?? "";
        _sala = i.GetStringExtra("sala") ?? ""; _jogo = i.GetStringExtra("jogo") ?? ""; _qualidade = i.GetStringExtra("qualidade") ?? "";
        _desde = i.GetLongExtra("desde", 0);
        Sessao.Iniciar(this);
        MontarTela();
        ConferirCapacidade();
        Sessao.Mensagem += AoReceber;
        Sessao.NaoEntregue += AoNaoEntregar;
        Sessao.Mudou += AoMudarSala;
        OuvirRede();
        Srt.srt_startup();
        _ = Task.Run(Painel);
        if (_codigoTeste != null) _ = Task.Run(ModoTeste);
        else _ = AssistirAsync();
    }

    // ================================================================================================ tela

    void MontarTela()
    {
        _raiz = new FrameLayout(this);
        _raiz.SetBackgroundColor(Color.Black);
        _raiz.Click += (_, _) => { if (_tocando) Controles(!_controlesVisiveis); };

        _video = new SurfaceView(this);
        _video.Holder!.AddCallback(this);
        _raiz.AddView(_video, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio, GravityFlags.Center));

        // esperando: o Mumu com pipoca, quem vai assistir e o passo atual
        _espera = Tema.Coluna(this);
        _espera.SetGravity(GravityFlags.Center);
        _espera.Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Tema.Fundo, Tema.Barra });
        var mumu = new ImageView(this);
        mumu.SetImageResource(Resource.Drawable.mumu_pipoca);
        mumu.SetScaleType(ImageView.ScaleType.FitCenter);
        // deitado, a altura é pouca (~400 dp num celular): o Mumu encolhe para o aviso e os botões caberem
        int ladoMumu = Math.Clamp((int)(Math.Min(Resources!.Configuration!.ScreenHeightDp, Resources.Configuration.ScreenWidthDp) * 0.26), 72, 150);
        _espera.AddView(mumu, Tema.Lp(Tema.Dp(this, ladoMumu), Tema.Dp(this, ladoMumu)));
        var quem = Tema.Texto(this, _codigoTeste != null ? "Modo de teste" : _apelido, 22, negrito: true);
        quem.Gravity = GravityFlags.Center;
        quem.SetPadding(0, Tema.Dp(this, 12), 0, 0);
        _espera.AddView(quem);
        if (_jogo.Length > 0) { var j = Tema.Texto(this, _jogo, 15, Tema.TextoApagado); j.Gravity = GravityFlags.Center; _espera.AddView(j); }
        var linhaEstado = Tema.Linha(this, GravityFlags.Center);
        linhaEstado.SetPadding(0, Tema.Dp(this, 20), 0, 0);
        _giro = new ProgressBar(this) { Indeterminate = true };
        _giro.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.DestaqueSuave);
        linhaEstado.AddView(_giro, Tema.Lp(Tema.Dp(this, 20), Tema.Dp(this, 20)));
        _estado = Tema.Texto(this, "Preparando a conexão...", 15);
        _estado.SetPadding(Tema.Dp(this, 10), 0, 0, 0);
        linhaEstado.AddView(_estado);
        _espera.AddView(linhaEstado);
        _detalhe = Tema.Texto(this, "", 13, Tema.TextoApagado);
        _detalhe.Gravity = GravityFlags.Center;
        _detalhe.SetMaxWidth(Tema.Dp(this, 420));
        _detalhe.SetPadding(Tema.Dp(this, 24), Tema.Dp(this, 6), Tema.Dp(this, 24), 0);
        _espera.AddView(_detalhe);
        _aviso = Tema.Texto(this, "", 13, Tema.Aviso);
        _aviso.Gravity = GravityFlags.Center;
        _aviso.SetMaxWidth(Tema.Dp(this, 440));
        _aviso.SetPadding(Tema.Dp(this, 24), Tema.Dp(this, 10), Tema.Dp(this, 24), 0);
        _aviso.Visibility = ViewStates.Gone;
        _espera.AddView(_aviso);
        _acoesErro = Tema.Linha(this, GravityFlags.Center);
        _acoesErro.SetPadding(0, Tema.Dp(this, 20), 0, 0);
        _acoesErro.Visibility = ViewStates.Gone;
        var voltar = Tema.Botao(this, "Voltar", principal: false);
        voltar.Click += (_, _) => Finish();
        _acoesErro.AddView(voltar, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        var deNovo = Tema.Botao(this, "Tentar de novo");
        deNovo.Click += (_, _) => { if (_codigoTeste == null) _ = AssistirAsync(); };
        var lpDeNovo = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
        lpDeNovo.LeftMargin = Tema.Dp(this, 12);
        _acoesErro.AddView(deNovo, lpDeNovo);
        _espera.AddView(_acoesErro);
        _raiz.AddView(_espera, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio));

        // por cima do vídeo: a barra de cima (voltar, quem, o jogo) num degradê, e os botões embaixo à direita
        _barra = Tema.Linha(this);
        _barra.Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Color.Argb(190, 0, 0, 0), Color.Transparent });
        _barra.SetPadding(Tema.Dp(this, 8), Tema.Dp(this, 8), Tema.Dp(this, 20), Tema.Dp(this, 28));
        _barra.AddView(BotaoRedondo(Resource.Drawable.ic_voltar, "Voltar", Finish));
        var av = new Avatar(this);
        av.Mostrar(_conta, _apelido, Sessao.Avatar(_conta), aoVivo: true);
        var lpAv = Tema.Lp(Tema.Dp(this, 36), Tema.Dp(this, 36));
        lpAv.LeftMargin = Tema.Dp(this, 4);
        _barra.AddView(av, lpAv);
        var textos = Tema.Coluna(this);
        textos.SetPadding(Tema.Dp(this, 10), 0, 0, 0);
        textos.AddView(Tema.Texto(this, _codigoTeste != null ? "Modo de teste" : _apelido, 16, Color.White, negrito: true));
        var sub = string.Join(" · ", new[] { _jogo, _qualidade, Tema.AoVivoHa(_desde) }.Where(s => s.Length > 0));
        if (sub.Length > 0) { var t = Tema.Texto(this, sub, 13, Color.Argb(220, 255, 255, 255)); t.SetMaxLines(1); t.Ellipsize = Android.Text.TextUtils.TruncateAt.End; textos.AddView(t); }
        _barra.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        _barra.AddView(Tema.Selo(this, "AO VIVO", Tema.AoVivo));
        _barra.Visibility = ViewStates.Gone;
        _raiz.AddView(_barra, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Ajusta, GravityFlags.Top));

        _botoes = Tema.Linha(this);
        _botoes.SetPadding(Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12));
        var som = BotaoRedondo(Resource.Drawable.ic_som, "Sem som", AlternarSom);
        _icSom = (ImageView)som;
        _botoes.AddView(som);
        _botoes.AddView(BotaoRedondo(Resource.Drawable.ic_info, "Números da conexão", () => { _mostrarNumeros = !_mostrarNumeros; _numeros.Visibility = _mostrarNumeros ? ViewStates.Visible : ViewStates.Gone; Controles(true); }));
        _botoes.Visibility = ViewStates.Gone;
        _raiz.AddView(_botoes, new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Bottom | GravityFlags.End));

        _numeros = Tema.Texto(this, "", 11, Color.White);
        _numeros.Typeface = Typeface.Monospace;
        _numeros.Background = Tema.Forma(this, Color.Argb(170, 0, 0, 0), 10);
        _numeros.SetPadding(Tema.Dp(this, 10), Tema.Dp(this, 8), Tema.Dp(this, 10), Tema.Dp(this, 8));
        _numeros.Visibility = ViewStates.Gone;
        var lpN = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Bottom | GravityFlags.Start);
        lpN.SetMargins(Tema.Dp(this, 16), 0, Tema.Dp(this, 16), Tema.Dp(this, 16));
        _raiz.AddView(_numeros, lpN);

        // o mesmo aviso por cima do vídeo, por alguns segundos quando a imagem abre
        _avisoVideo = Tema.Texto(this, "", 13, Color.White);
        _avisoVideo.Background = Tema.Forma(this, Color.Argb(200, 0, 0, 0), 10);
        _avisoVideo.SetPadding(Tema.Dp(this, 14), Tema.Dp(this, 8), Tema.Dp(this, 14), Tema.Dp(this, 8));
        _avisoVideo.Visibility = ViewStates.Gone;
        var lpAviso = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.CenterHorizontal);
        lpAviso.TopMargin = Tema.Dp(this, 72);
        _raiz.AddView(_avisoVideo, lpAviso);

        // as margens do recorte da câmera só para os controles: o vídeo ocupa a tela toda
        _raiz.SetOnApplyWindowInsetsListener(new Recorte(this));
        SetContentView(_raiz);
        TelaCheia();
    }

    ImageView BotaoRedondo(int icone, string descricao, Action tocar)
    {
        var b = Tema.Icone(this, icone, Color.White, 48);
        b.SetPadding(Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12));
        b.Background = new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.Argb(80, 255, 255, 255)), null, Tema.Forma(this, Color.White, 24));
        b.ContentDescription = descricao;
        b.Clickable = true;
        b.Click += (_, _) => { tocar(); if (_controlesVisiveis) Controles(true); };
        return b;
    }

    sealed class Recorte(PlayerActivity a) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            int l = 0, r = 0, t = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout is { } c) { l = c.SafeInsetLeft; r = c.SafeInsetRight; t = c.SafeInsetTop; }
            foreach (var view in new View[] { a._barra, a._botoes, a._numeros })
                if (view.LayoutParameters is FrameLayout.LayoutParams p) { p.LeftMargin = Math.Max(p.LeftMargin, l); p.RightMargin = Math.Max(p.RightMargin, r); p.TopMargin = t; view.LayoutParameters = p; }
            return insets;
        }
    }

    /// <summary>Mostra (e agenda para esconder em 3 s) ou esconde os controles, com um esmaecer curto.</summary>
    void Controles(bool mostrar)
    {
        _ui.RemoveCallbacks(_esconder);
        _controlesVisiveis = mostrar;
        foreach (var v in new View[] { _barra, _botoes })
        {
            v.Animate()!.Cancel();
            if (mostrar)
            {
                if (v.Visibility != ViewStates.Visible) { v.Alpha = 0; v.Visibility = ViewStates.Visible; }
                v.Animate()!.Alpha(1).SetDuration(150).Start();
            }
            else v.Animate()!.Alpha(0).SetDuration(200).WithEndAction(new Java.Lang.Runnable(() => { if (!_controlesVisiveis) v.Visibility = ViewStates.Gone; })).Start();
        }
        if (mostrar) _ui.PostDelayed(_esconder, 3000);
        else TelaCheia();
    }

    void AlternarSom()
    {
        _mudo = !_mudo;
        if (_som != null) _som.Mudo = _mudo;
        _icSom.SetImageResource(_mudo ? Resource.Drawable.ic_sem_som : Resource.Drawable.ic_som);
        _icSom.ContentDescription = _mudo ? "Ligar o som" : "Sem som";
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

    void Ui(Action a) => RunOnUiThread(a);

    /// <summary>A tela de espera com o passo atual (ou o erro, com "Tentar de novo").</summary>
    void Esperando(string estado, string detalhe = "", bool erro = false)
    {
        Streemu.Util.Log.Info(estado + (detalhe.Length > 0 ? " " + detalhe : ""));
        if (erro) Telemetria.Evento("falha", new JsonObject { ["linha"] = estado + (detalhe.Length > 0 ? " " + detalhe : ""), ["ligacao"] = _ligacaoTel, ["lado"] = "viewer" });
        Ui(() =>
        {
            if (_fechando) return;
            _tocando = false;
            Controles(false);
            _espera.Visibility = ViewStates.Visible;
            _espera.Alpha = 1;
            _estado.Text = estado;
            _estado.SetTextColor(erro ? Tema.Erro : Tema.CorTexto);
            _giro.Visibility = erro ? ViewStates.Gone : ViewStates.Visible;
            _detalhe.Text = detalhe;
            _acoesErro.Visibility = erro ? ViewStates.Visible : ViewStates.Gone;
            if (erro && _codigoTeste != null) _acoesErro.GetChildAt(1)!.Visibility = ViewStates.Gone;
        });
    }

    /// <summary>O primeiro quadro chegou à tela: some a espera e os controles aparecem por um instante (mostra que existem).</summary>
    void Tocando()
    {
        Ui(() =>
        {
            if (_tocando || _fechando) return;
            _tocando = true;
            if (!_videoOkTel && _ligacaoTel != null)
            {
                _videoOkTel = true;
                Telemetria.Evento("midia_video_ok", new JsonObject
                {
                    ["lado"] = "viewer", ["ligacao"] = _ligacaoTel, ["tentativa"] = $"{_ligacaoTel}-{_tentativaMidia}", ["numero"] = _tentativaMidia,
                    ["ms"] = _aceitoEm > 0 ? Environment.TickCount64 - _aceitoEm : null, ["decodificador"] = _dec?.Nome, ["resolucao"] = $"{_videoW}x{_videoH}",
                });
            }
            if (_aguenta == Aparelho.Aguenta.Nao && _avisoVideo.Visibility != ViewStates.Visible)
            {
                _avisoVideo.Alpha = 1;
                _avisoVideo.Visibility = ViewStates.Visible;
                _avisoVideo.Animate()!.Alpha(0).SetStartDelay(6000).SetDuration(400)
                    .WithEndAction(new Java.Lang.Runnable(() => _avisoVideo.Visibility = ViewStates.Gone)).Start();
            }
            _espera.Animate()!.Alpha(0).SetDuration(250).WithEndAction(new Java.Lang.Runnable(() => { if (_tocando) _espera.Visibility = ViewStates.Gone; })).Start();
            Controles(true);
        });
    }

    /// <summary>
    /// Pergunta ao decodificador se ele aguenta a qualidade da live. Se o fabricante diz que não, avisa (a live abre assim mesmo: o decodificador
    /// descarta o que não dá tempo de mostrar, então a imagem trava mas o atraso não cresce). Vai para o logcat como "CAPACIDADE".
    /// </summary>
    void ConferirCapacidade()
    {
        if (Aparelho.LerQualidade(_qualidade) is not { } q) return;
        _ = Task.Run(() =>
        {
            _aguenta = Aparelho.AguentaH264(q.W, q.H, q.Fps, out var detalhe);
            _capacidade = $"{q.H}p{q.Fps}: {(_aguenta switch { Aparelho.Aguenta.Sim => "aguenta", Aparelho.Aguenta.Nao => "NÃO aguenta", _ => "não sei" })} ({detalhe})";
            Streemu.Util.Log.Info("CAPACIDADE " + _capacidade);
            Telemetria.Evento("capacidade", new JsonObject
            {
                ["qualidade"] = _qualidade, ["resultado"] = _aguenta switch { Aparelho.Aguenta.Sim => "aguenta", Aparelho.Aguenta.Nao => "nao_aguenta", _ => "nao_sei" }, ["detalhe"] = detalhe,
            });
            if (_aguenta != Aparelho.Aguenta.Nao) return;
            string texto = $"Este celular não promete {q.H}p a {q.Fps} quadros. A imagem pode travar; quem transmite pode baixar a qualidade no PC.";
            Ui(() =>
            {
                _aviso.Text = texto; _aviso.Visibility = ViewStates.Visible;
                _avisoVideo.Text = $"Este celular pode não dar conta de {q.H}p{q.Fps}";
            });
        });
    }

    // ================================================================================================ pedir para assistir

    /// <summary>O mesmo pedido do Windows (MainForm.Grupo.cs, WatchAsync): ligação nova e "quero_assistir" com o meu código; conecta no "aceito".</summary>
    async Task AssistirAsync()
    {
        if (_pedindo || _fechando) return;
        _pedindo = true;   // um toque a mais durante o await abriria uma segunda ligação e o "aceito" da primeira seria descartado
        int tentativa = ++_tentativa;
        EncerrarLigacao(avisar: true);
        // a sala pode estar voltando (depois de trocar de rede ela cai e reconecta em ~3 s): espera até 15 s antes de desistir
        var sala = Sessao.Sala;
        if (sala is not { Connected: true })
        {
            Esperando("Reconectando ao grupo...");
            for (int i = 0; i < 150 && !_fechando && (sala = Sessao.Sala) is not { Connected: true }; i++) await Task.Delay(100);
        }
        if (sala is not { Connected: true }) { _pedindo = false; Esperando("Sem conexão com o grupo.", sala?.Status ?? "", erro: true); return; }
        if (sala.Friends.FirstOrDefault(f => f.Id == _conta)?.Live is not { } live) { _pedindo = false; Esperando($"{_apelido} não está mais ao vivo.", "", erro: true); return; }
        Esperando("Preparando a conexão...");
        string sid = IdAleatorio(12);
        PeerLink link;
        try
        {
            var lan = IpLocal();
            link = await PeerLink.CreateAsync(sid, _conta, _apelido, iAmSender: false, localMode: false, Sessao.Apelido,
                lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), live.StreamId);
        }
        catch (Exception e) { Esperando("Não deu para preparar a conexão.", e.Message, erro: true); return; }
        finally { _pedindo = false; }
        if (_fechando || tentativa != _tentativa) { link.Dispose(); return; }
        _link = link;
        link.Tunnel.StateChanged += (s, msg) => Streemu.Util.Log.Info($"ligação {s}: {msg}");
        _ligacaoTel = sid; _transmissaoTel = live.StreamId; _ligacaoDesde = Environment.TickCount64; _aceitoEm = 0; _videoOkTel = false; _tentativaMidia = 0;
        Telemetria.Evento("live", new JsonObject
        {
            ["papel"] = "assiste", ["transmissao"] = live.StreamId, ["ligacao"] = sid, ["outro_apelido"] = _apelido, ["jogo"] = live.Game, ["qualidade"] = live.Quality,
            ["gatilho"] = tentativa == 1 ? "inicio" : "de_novo",
        });
        try { await sala.SendTo(_conta, new JsonObject { ["tipo"] = "quero_assistir", ["sessao"] = sid, ["sala"] = live.StreamId, ["codigo"] = link.MyCode }); }
        catch (Exception e)
        {
            // sem isto a exceção sumia no Task e a tela ficava em "Preparando a conexão..." para sempre
            EncerrarLigacao(avisar: false);
            Esperando("Não deu para pedir a live.", e.Message, erro: true);
            return;
        }
        Esperando($"Esperando {_apelido} aceitar...");
        _ = Task.Delay(20_000).ContinueWith(_ => Ui(() =>
        {
            if (tentativa == _tentativa && _link == link && link.Tunnel.State is TunnelState.Ready or TunnelState.Discovering && !_tocando)
                Esperando($"{_apelido} não respondeu.", "O app no PC pode estar ocupado ou desatualizado.", erro: true);
        }));
    }

    void AoReceber(string de, string apelido, JsonObject msg) => Ui(() =>
    {
        var link = _link;
        // a mensagem vem do PC de outra pessoa: campo com tipo errado vira nulo (GetValue lançaria aqui, na thread da tela)
        if (link == null || de != _conta || Streemu.Util.JsonSeguro.Texto(msg["sessao"]) != link.SessionId) return;
        switch (Streemu.Util.JsonSeguro.Texto(msg["tipo"]))
        {
            case "aceito":
                try { link.Connect(Streemu.Util.JsonSeguro.Texto(msg["codigo"]) ?? ""); }
                catch (Exception e) { Esperando("Não deu para conectar.", $"{apelido} mandou um código inválido ({e.Message}).", erro: true); return; }
                Esperando("Conectando...");
                _aceitoEm = Environment.TickCount64;
                _tunel = link.Tunnel; _relay = link.Relay;
                EsperarConexao(link.Tunnel, _tentativa);
                break;
            case "recusado":
                EncerrarLigacao(avisar: false);
                Esperando($"{apelido} não aceitou.", Streemu.Util.JsonSeguro.Texto(msg["motivo"]) ?? "", erro: true);
                break;
            case "parar":
                EncerrarLigacao(avisar: false);
                Esperando($"{apelido} parou de transmitir.", "", erro: true);
                break;
        }
    });

    void AoNaoEntregar(string para, JsonObject? msg) => Ui(() =>
    {
        if (_link != null && para == _conta && Streemu.Util.JsonSeguro.Texto(msg?["sessao"]) == _link.SessionId)
        { EncerrarLigacao(avisar: false); Esperando($"{_apelido} saiu do grupo.", "", erro: true); }
    });

    /// <summary>A live acabou sem "parar" (o PC caiu, por exemplo): a sala avisa que a pessoa não está mais ao vivo nesta sala.</summary>
    void AoMudarSala() => Ui(() =>
    {
        if (_codigoTeste != null || _link == null || Sessao.Sala is not { Connected: true } sala) return;
        if (sala.Friends.FirstOrDefault(f => f.Id == _conta)?.Live == null)
        { EncerrarLigacao(avisar: false); Esperando($"A live de {_apelido} terminou.", "", erro: true); }
    });

    void EsperarConexao(Tunnel t, int tentativa)
    {
        _ = Task.Run(async () =>
        {
            var fim = DateTime.UtcNow.AddSeconds(30);
            while (t.State != TunnelState.Connected)
            {
                if (_fechando || tentativa != _tentativa) return;
                if (DateTime.UtcNow > fim) { Esperando("Não conectou.", "Nenhum caminho de rede funcionou em 30 s. Tente de novo, ou troque de rede.", erro: true); return; }
                await Task.Delay(50);
            }
            Streemu.Util.Log.Info($"Conectado por {t.Target} em {t.ConnectMs} ms.");
            Ui(() => { if (!_tocando) _estado.Text = "Abrindo o vídeo..."; });
            if (Interlocked.Exchange(ref _srtLigado, 1) == 0) new Thread(LacoSrt) { IsBackground = true, Name = "srt" }.Start();
        });
    }

    /// <summary>Fecha a ligação atual; com <paramref name="avisar"/>, manda "parar" para quem transmite (como o Windows faz quando o player fecha).</summary>
    void EncerrarLigacao(bool avisar)
    {
        var link = Interlocked.Exchange(ref _link, null);
        if (link == null) return;
        FimDaLive(avisar ? "pedido_de_novo" : "fim");
        if (avisar && Sessao.Sala is { } sala) _ = sala.SendTo(link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = link.SessionId });
        if (_tunel == link.Tunnel) { _tunel = null; _relay = null; }
        // depois que o SRT largar o relay: fechar agora faria o laço tentar o relay morto, que é o que ele já faz ao perder a conexão
        _ = Task.Delay(300).ContinueWith(_ => link.Dispose());
    }

    /// <summary>O live_fim da ligação de agora (uma vez por ligação).</summary>
    void FimDaLive(string motivo)
    {
        if (Interlocked.Exchange(ref _ligacaoTel, null) is not { } lig) return;
        Telemetria.Evento("live_fim", new JsonObject
        {
            ["papel"] = "assiste", ["transmissao"] = _transmissaoTel, ["ligacao"] = lig, ["outro_apelido"] = _apelido,
            ["minutos"] = Math.Round((Environment.TickCount64 - _ligacaoDesde) / 60000.0, 1), ["teve_video"] = _videoOkTel, ["motivo"] = motivo,
        });
    }

    // ================================================================================================ rede do celular

    /// <summary>Wi-Fi para 4G (ou o contrário) muda o endereço: o túnel antigo morre. Pede de novo com uma ligação nova pela rede nova.</summary>
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
            a.Ui(() =>
            {
                if (a._fechando || a._codigoTeste != null || (a._link == null && !a._tocando)) return;
                Streemu.Util.Log.Info("A rede mudou: pedindo de novo pela rede nova.");
                Telemetria.Evento("rede_mudou", new JsonObject { ["ligacao"] = a._ligacaoTel });
                a.EncerrarLigacao(avisar: true);
                // a sala também reconecta pela rede nova; espera ela voltar antes de pedir
                _ = Task.Run(async () =>
                {
                    a.Esperando("A rede mudou. Reconectando...");
                    // a sala só nota a queda um instante depois (03:18:55.53 a rede mudou, 55.60 a sala caiu): sem esta folga, o pedido saía com a sala
                    // ainda "conectada" pela rede velha e falhava. O AssistirAsync espera a sala voltar.
                    await Task.Delay(1500);
                    a.Ui(() => _ = a.AssistirAsync());
                });
            });
        }
    }

    // ================================================================================================ modo de teste (código colado, sem sala)

    async Task ModoTeste()
    {
        try
        {
            var t = new Tunnel(localMode: false) { Nick = "celular" };
            t.StateChanged += (s, msg) => Streemu.Util.Log.Info($"{s}: {msg}");
            var pub = await t.DiscoverAsync();
            var lan = IpLocal();
            string codigo = new PeerCode(pub.Address, pub.Port, t.Secret, lan != null ? new IPEndPoint(lan, t.LocalPort) : null).Encode();
            // o proto-envio manda, a cada segundo, a hora do PC junto com o relógio do túnel dele (ver Atraso)
            t.Control += m =>
            {
                if (m["tipo"]?.GetValue<string>() == "relogio-envio" && m["utc_us"] is { } utc && m["qpc_us"] is { } qpc)
                    _utcMenosQpcPc = utc.GetValue<long>() - qpc.GetValue<long>();
            };
            var relay = new LocalRelay("player");
            t.ToViewer = d => relay.Deliver(d);
            relay.Outgoing = d => t.SendMedia(Tunnel.TToSender, d);
            _tunel = t; _relay = relay;
            Streemu.Util.Log.Info("MEU CÓDIGO: " + codigo);
            Esperando("Conectando ao código...", "Meu código está no logcat (MEU CÓDIGO).");
            t.ConnectTo(PeerCode.Parse(_codigoTeste!.Trim().ToUpperInvariant()));
            EsperarConexao(t, _tentativa);
        }
        catch (Exception e) { Esperando("Falhou ao preparar.", e.Message, erro: true); }
    }

    // ================================================================================================ SRT -> demux -> decodificador

    void LacoSrt()
    {
        var buf = new byte[1500];
        while (!_fechando)
        {
            int u = Srt.Erro;
            try
            {
                // espera a tela e uma ligação conectada (depois de trocar de rede, a nova)
                while (!_fechando && (_surface == null || _tunel is not { State: TunnelState.Connected } || _relay == null)) Thread.Sleep(50);
                if (_fechando) break;
                var (tunel, relay) = (_tunel!, _relay!);
                lock (_midia)
                {
                    if (_fechando) break;
                    _dec ??= CriarDecodificador();
                }
                u = Srt.Conectar(relay.Port, tunel.Passphrase, LatenciaMs);
                Streemu.Util.Log.Info("SRT conectado.");
                while (!_fechando && _tunel == tunel)
                {
                    int n = Srt.srt_recvmsg(u, buf, buf.Length);
                    if (n == Srt.Erro)
                    {
                        string e = Srt.UltimoErro;
                        // a libsrt escreve "transmission timed out" (não "timeout"): sem o "timed out", 3 s sem bytes derrubavam e reabriam o SRT
                        if (e.Contains("timeout", StringComparison.OrdinalIgnoreCase) || e.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                            || e.Contains("again", StringComparison.OrdinalIgnoreCase)) continue;
                        throw new InvalidOperationException("SRT recv: " + e);
                    }
                    _demux.Alimentar(buf.AsSpan(0, n));
                }
            }
            catch (Exception e) { Streemu.Util.Log.Info("AVISO: " + e.Message + " (reabrindo em 1 s)"); Thread.Sleep(1000); }
            finally { if (u != Srt.Erro) Srt.srt_close(u); }
        }
    }

    Decodificador CriarDecodificador()
    {
        var d = new Decodificador(_surface!);
        d.Tamanho += (w, h) =>
        {
            _videoW = w; _videoH = h; Ui(AjustarProporcao);
            _tentativaMidia++;
            Telemetria.Evento("midia_tentativa", new JsonObject
            {
                ["lado"] = "viewer", ["ligacao"] = _ligacaoTel, ["tentativa"] = $"{_ligacaoTel}-{_tentativaMidia}", ["numero"] = _tentativaMidia,
                ["gatilho"] = _tentativaMidia == 1 ? "inicio" : "outro", ["decodificador"] = "mediacodec", ["codec"] = "h264", ["nome"] = d.Nome,
                ["chaves_do_fabricante"] = d.ChavesDoFabricante, ["renderizador"] = "surface", ["resolucao"] = $"{w}x{h}",
            });
        };
        d.Chegou += (pts, us) => { if (Atraso(pts, us) is double ms) _atrasoChegada.Add(ms); };
        d.NaTela += (pts, us) => { Tocando(); if (Atraso(pts, us) is double ms) _atrasoTela.Add(ms); };
        _demux.Quadro = d.Entregar;
        // o som abre no primeiro pacote de áudio, quando a PMT já disse quantos canais são
        _demux.Audio = (pes, pts) => ObterSom()?.Entregar(pes, pts);
        return d;
    }

    Som? ObterSom()
    {
        if (_som is { } som) return som;
        lock (_midia) return _fechando ? null : _som ??= new Som(_demux.CanaisAudio) { Mudo = _mudo };
    }

    /// <summary>Encaixa o vídeo na tela sem esticar (faixas pretas onde sobrar).</summary>
    void AjustarProporcao()
    {
        if (_videoW <= 0 || _videoH <= 0) return;
        int tw = _raiz.Width, th = _raiz.Height;
        if (tw <= 0 || th <= 0) return;
        double s = Math.Min((double)tw / _videoW, (double)th / _videoH);
        _video.LayoutParameters = new FrameLayout.LayoutParams((int)(_videoW * s), (int)(_videoH * s), GravityFlags.Center);
    }

    /// <summary>
    /// Atraso de um quadro, em ms (só no modo de teste, que recebe a hora do PC): a hora do PC agora menos a hora em que o PC gerou o quadro (o PTS).
    /// </summary>
    double? Atraso(long pts90k, long meuUs)
    {
        if (_tunel?.PeerClockOffsetUs is not long off || _utcMenosQpcPc is not long m || pts90k < 0) return null;
        long horaPcUs = meuUs + off + m;
        const long Volta = 1L << 33;
        long agora90k = horaPcUs / 100 * 9 % Volta;
        long d = (agora90k - pts90k) & (Volta - 1);
        if (d > Volta / 2) d -= Volta;
        return d / 90.0;
    }

    // ================================================================================================ números (botão de info, e o logcat a cada 2 s)

    async Task Painel()
    {
        long bytesAntes = 0, mostradosAntes = 0;
        Tunnel? tunelAntes = null;
        var relogio = Stopwatch.StartNew();
        double antes = 0;
        while (!_fechando)
        {
            await Task.Delay(2000);
            var t = _tunel; var d = _dec;
            if (t == null) continue;
            if (t != tunelAntes) { bytesAntes = t.RxBytes; tunelAntes = t; }
            double agora = relogio.Elapsed.TotalSeconds, dt = agora - antes; antes = agora;
            long bytes = t.RxBytes; double mbps = (bytes - bytesAntes) * 8 / dt / 1e6; bytesAntes = bytes;
            long mostrados = d?.Mostrados ?? 0; double fps = (mostrados - mostradosAntes) / dt; mostradosAntes = mostrados;
            double perda = t.RxViewer.TakeWindowLossPct();
            string s = $"{t.Target}  ida e volta {t.RttMs:0} ms  {mbps:0.0} Mbit/s  perda {perda:0.00}%" +
                       (d == null ? "" : $"\n{_videoW}x{_videoH} {fps:0} quadros/s  {d.Nome}  decodificar {d.MsNoDecodificador:0.0} ms") +
                       (_som is { } som ? $"\nsom: fila {som.NaFilaMs:0} ms (mín. {som.MinimoNaFilaMs:0}), {som.Faltas} faltas, maior intervalo {som.MaiorIntervaloMs:0} ms" : "");
            if (_capacidade.Length > 0) s += $"\ndecodificador para {_capacidade}";
            if (d is { ChavesDoFabricante.Length: > 0 }) s += $"\nbaixa latência do fabricante: {d.ChavesDoFabricante}";
            var tela = _atrasoTela.Janela();
            if (tela != null) s += $"\natraso até a tela {tela}";
            Logcat.Info("Streemu", s.Replace('\n', ' ') + (d == null ? "" : $" | quadros {d.Entregues}/{d.Mostrados}, {d.DescartadosAteChave} antes da chave, {d.DescartadosFilaCheia} fila cheia; TS {_demux.Descontinuidades} buracos"));
            // térmico e bateria a cada 10 s (são chamadas ao sistema; a cada 2 s seria à toa)
            if (++_amostra % 5 == 1) { _termico = Aparelho.Termico(this) ?? ""; _bateria = Aparelho.Bateria(this); }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Telemetria.Serie("assistir.csv",
                "hora_utc,ligacao,caminho,mbit_s,perda_pct,ida_e_volta_ms,quadros_s,largura,altura,decodificar_ms,entregues,mostrados,descartados_chave,descartados_fila,ts_buracos,som_fila_ms,som_min_ms,som_faltas,termico,bateria_pct",
                string.Join(",", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), _ligacaoTel ?? "teste", (t.Target?.ToString() ?? "").Replace(',', ' '),
                    mbps.ToString("0.00", inv), perda.ToString("0.00", inv), t.RttMs.ToString("0", inv), fps.ToString("0.0", inv), _videoW, _videoH,
                    (d?.MsNoDecodificador ?? 0).ToString("0.0", inv), d?.Entregues ?? 0, d?.Mostrados ?? 0, d?.DescartadosAteChave ?? 0, d?.DescartadosFilaCheia ?? 0,
                    _demux.Descontinuidades, (_som?.NaFilaMs ?? 0).ToString("0", inv), (_som?.MinimoNaFilaMs ?? 0).ToString("0", inv), _som?.Faltas ?? 0, _termico, _bateria));
            Ui(() => _numeros.Text = s);
        }
    }
    int _amostra, _bateria = -1;
    string _termico = "";

    // ================================================================================================ Surface e ciclo de vida

    public void SurfaceCreated(ISurfaceHolder holder) { _surface = holder.Surface; }
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) { }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) { TelaCheia(); AjustarProporcao(); }
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _raiz.Post(AjustarProporcao);
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
        _ui.RemoveCallbacks(_esconder);
        Sessao.Mensagem -= AoReceber;
        Sessao.NaoEntregue -= AoNaoEntregar;
        Sessao.Mudou -= AoMudarSala;
        Telemetria.Ativo = false;
        FimDaLive("fechou o player");
        try { if (_redeCallback != null) _rede?.UnregisterNetworkCallback(_redeCallback); } catch { }
        var link = Interlocked.Exchange(ref _link, null);
        var sala = Sessao.Sala;
        Decodificador? dec; Som? som;
        lock (_midia) { dec = _dec; _dec = null; som = _som; _som = null; }
        var (tunel, relay) = _codigoTeste != null ? (_tunel, _relay) : (null, null);
        // tudo o que espera (o "parar" pela sala, o fim do TURN, as threads do som e do decodificador) sai da thread da tela:
        // somado, passava de 2 s com rede ruim, e o Android acusava "o app não responde"
        _ = Task.Run(async () =>
        {
            if (link != null && sala != null)
                try { await sala.SendTo(link.PeerId, new JsonObject { ["tipo"] = "parar", ["sessao"] = link.SessionId }).WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            try { dec?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar o decodificador: " + e.Message); }
            try { som?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar o som: " + e.Message); }
            try { link?.Dispose(); } catch (Exception e) { Streemu.Util.Log.Info("[player] fechar a ligação: " + e.Message); }
            try { tunel?.Dispose(); relay?.Dispose(); } catch { }
        });
        Atualizacao.PlayerFechou();
        base.OnDestroy();
    }

    internal static IPAddress? IpLocal()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 53);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch { return null; }
    }

    static string IdAleatorio(int n)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, n));
    }
}
