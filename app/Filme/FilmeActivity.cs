using System.Text.Json.Nodes;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Win32.SafeHandles;
using Streemu.Net;
using Streemu.Proto.Transmissao;
using AnfitriaoCinema = Streemu.Filme.AnfitriaoCinema;
using Streemu.Proto.Ui;
using JsonSeguro = Streemu.Util.JsonSeguro;
using LogApp = Streemu.Util.Log;
using Logcat = Android.Util.Log;
using SessaoFilme = Streemu.Filme.Sessao;

namespace Streemu.Proto.Filme;

/// <summary>
/// O filme visto de um celular que já tem o arquivo (modo A, spec 028 F2a). Faz o mesmo que o MainForm.Filme.cs / FilmeTela.cs do Windows para quem assiste:
/// abre uma PeerLink "filme" com o anfitrião (filme_quero, e a sala responde filme_aceito), espera o relógio comum, abre o PlayerFilme e a Sessao como espectador
/// e avisa "filme_pronto" pelo túnel. Tocar, pausar e pular só pedem à Sessao (que pede ao anfitrião, se ele deixou).
/// Extras: anfitriao (id na sala), anfitriao_nome, filme (identidade), titulo; o arquivo vem em Intent.Data.
/// <para>
/// Com o extra <c>stream</c> (F3, spec 028 5.1) não há arquivo: depois da ligação manda <c>filme_stream_quero</c> (com a capacidade do aparelho), espera <c>filme_stream_info</c> e toca
/// pelo <see cref="PlayerStream"/>, que lê o SRT para um cache. A Sessao é a mesma; só o que ela manda muda de nome (<c>filme_carregado</c> vira <c>filme_stream_carregado</c>,
/// <c>filme_pedido</c> vira <c>filme_stream_cmd</c>), como a StreamTela do Windows.
/// </para>
/// <para>
/// Com o extra <c>sou_anfitriao</c> (F4, spec 028 7) o celular é o ANFITRIÃO: o arquivo (Intent.Data) toca aqui, a Sessao é de anfitrião (relógio local) e o <see cref="AnfitriaoCinema"/> faz o resto do protocolo
/// (anúncio na sala, filme_quero→aceito, túnel por pessoa, arquivo por Stream). Extras: filme (identidade), titulo, ext. O <see cref="ServicoCinema"/> segura o app vivo em segundo plano.
/// </para>
/// </summary>
[Activity(Label = "Cinema", Exported = false, Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", ScreenOrientation = ScreenOrientation.Landscape,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class FilmeActivity : Android.App.Activity, Ui.IFixoEscuro
{
    // anfitrião (F4)
    static FilmeActivity? _anfAtual;
    /// <summary>Há um anfitrião de Cinema aberto neste aparelho (só um por vez).</summary>
    public static bool AnfitriaoAberto => _anfAtual != null;
    bool _anf;
    string _ext = "";
    AnfitriaoCinema? _host;
    LinearLayout _painelAnf = null!;
    TextView _pessoas = null!, _todos = null!, _franquia = null!;
    string _ultimaNotificacao = "";

    string _anfitriao = "", _anfitriaoNome = "", _filme = "", _titulo = "";
    Android.Net.Uri? _arquivo;
    PeerLink? _link;
    PlayerFilme? _player;
    CopiaDoFilme? _copia;   // F5: o envio do filme em cópia direta (nulo se o arquivo pede recompressão)
    TextView _botaoFaixas = null!;
    PlayerStream? _pstream;
    bool _stream;
    TaskCompletionSource<JsonObject>? _streamInfo;
    SessaoFilme? _sessao;
    volatile bool _fechando;
    string _sid = "";
    readonly CancellationTokenSource _cts = new();
    readonly Handler _ui = new(Looper.MainLooper!);

    SurfaceView _video = null!;
    LinearLayout _espera = null!, _barra = null!;
    TextView _estado = null!, _detalhe = null!, _aviso = null!, _tempo = null!, _tocar = null!, _voltar = null!;
    SeekBar _linha = null!;
    ProgressBar _giro = null!;
    bool _arrastando;
    double _duracao = double.NaN;
    Java.Lang.Runnable? _sumirAviso;
    volatile bool _aceito;
    volatile string? _recusa;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        Atualizacao.Iniciar(this);
        if (Atualizacao.BloqueiaSala) { Finish(); return; }
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        var i = Intent!;
        _anfitriao = i.GetStringExtra("anfitriao") ?? ""; _anfitriaoNome = i.GetStringExtra("anfitriao_nome") ?? "o anfitrião";
        _filme = i.GetStringExtra("filme") ?? ""; _titulo = i.GetStringExtra("titulo") ?? "";
        _arquivo = i.Data;
        _stream = i.GetBooleanExtra("stream", false);
        _anf = i.GetBooleanExtra("sou_anfitriao", false); _ext = i.GetStringExtra("ext") ?? "";
        Sessao.Iniciar(this);
        MontarTela();
        TelaCheia();
        Sessao.Mensagem += AoReceberSala;
        Sessao.NaoEntregue += AoNaoEntregar;
        if (_anf) { IniciarAnfitriao(); return; }
        if ((_arquivo == null && !_stream) || _anfitriao.Length == 0 || _filme.Length == 0) { Falhou("Faltou o arquivo ou a sessão."); return; }
        _ = Task.Run(EntrarAsync);
    }

    // ------------------------------------------------------------------ tela

    void MontarTela()
    {
        var raiz = new FrameLayout(this);
        raiz.SetBackgroundColor(Color.Black);
        raiz.Click += (_, _) =>
        {
            _barra.Visibility = _barra.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
            if (_anf) _painelAnf.Visibility = _barra.Visibility;
        };
        _video = new SurfaceView(this);
        raiz.AddView(_video, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio, GravityFlags.Center));

        _aviso = Tema.Texto(this, "", 15, Color.White);
        _aviso.Gravity = GravityFlags.Center;
        _aviso.SetPadding(Tema.Dp(this, 16), Tema.Dp(this, 8), Tema.Dp(this, 16), Tema.Dp(this, 8));
        _aviso.Background = Tema.Forma(this, Color.Argb(190, 0, 0, 0), 12);
        _aviso.Visibility = ViewStates.Gone;
        var lpAviso = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.CenterHorizontal);
        lpAviso.TopMargin = Tema.Dp(this, 20);
        raiz.AddView(_aviso, lpAviso);

        _barra = Tema.Linha(this);
        _barra.SetPadding(Tema.Dp(this, 16), Tema.Dp(this, 8), Tema.Dp(this, 16), Tema.Dp(this, 12));
        _barra.Background = new GradientDrawable(GradientDrawable.Orientation.BottomTop!, new int[] { Color.Argb(200, 0, 0, 0), Color.Transparent });
        _barra.Visibility = ViewStates.Gone;
        _tocar = Tema.Botao(this, "Pausar");
        _tocar.SetMinWidth(Tema.Dp(this, 110));
        _tocar.Click += (_, _) => { if (_sessao is { } s) { if (s.Tocando) s.Pausar(); else s.Tocar(); } };
        _barra.AddView(_tocar, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        _linha = new SeekBar(this) { Max = 1000 };
        _linha.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Escuro.Destaque);
        _linha.ThumbTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Escuro.DestaqueSuave);
        _linha.StartTrackingTouch += (_, _) => _arrastando = true;
        _linha.StopTrackingTouch += (_, _) =>
        {
            _arrastando = false;
            if (_sessao is { } s && !double.IsNaN(_duracao)) s.Ir(_linha.Progress / 1000.0 * _duracao);
        };
        _barra.AddView(_linha, Tema.Lp(0, Tema.Ajusta, 1, Tema.Dp(this, 8)));
        _tempo = Tema.Texto(this, "0:00", 14, Color.White);
        _barra.AddView(_tempo);
        _botaoFaixas = Tema.Botao(this, "Áudio e legendas", principal: false);
        _botaoFaixas.Visibility = ViewStates.Gone;   // só com player de arquivo (anfitrião ou quem tem o arquivo): a transmissão leva uma faixa só
        _botaoFaixas.Click += (_, _) => { if (_player is { } p) _ = MenuDeFaixas.MostrarAsync(this, p, _copia, Avisar); };
        _barra.AddView(_botaoFaixas, Tema.Lp(Tema.Ajusta, Tema.Ajusta, 0, Tema.Dp(this, 8)));
        raiz.AddView(_barra, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Ajusta, GravityFlags.Bottom));

        // controles do anfitrião (F4): quem está junto, "Todos controlam" e o aviso de rede com franquia; só aparece para o anfitrião
        _painelAnf = Tema.Coluna(this);
        _painelAnf.SetPadding(Tema.Dp(this, 14), Tema.Dp(this, 10), Tema.Dp(this, 14), Tema.Dp(this, 12));
        _painelAnf.Background = Tema.Forma(this, Color.Argb(190, 0, 0, 0), 14);
        _painelAnf.Visibility = ViewStates.Gone;
        _todos = Tema.Botao(this, "Todos controlam: não", principal: false);
        _todos.Click += (_, _) => { if (_sessao is { Anfitriao: true } s) s.MudarPermissao(!s.TodosControlam); };
        _painelAnf.AddView(_todos, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        _pessoas = Tema.Texto(this, "", 13, Color.White);
        _pessoas.SetPadding(0, Tema.Dp(this, 8), 0, 0);
        _painelAnf.AddView(_pessoas);
        _franquia = Tema.Texto(this, "", 12, Tema.Escuro.Erro);
        _franquia.SetPadding(0, Tema.Dp(this, 6), 0, 0);
        _franquia.Visibility = ViewStates.Gone;
        _painelAnf.AddView(_franquia);
        var lpAnf = new FrameLayout.LayoutParams(Tema.Dp(this, 260), Tema.Ajusta, GravityFlags.Top | GravityFlags.End);
        lpAnf.TopMargin = Tema.Dp(this, 16); lpAnf.RightMargin = Tema.Dp(this, 16);
        raiz.AddView(_painelAnf, lpAnf);

        _espera = Tema.Coluna(this);
        _espera.SetGravity(GravityFlags.Center);
        _espera.Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Tema.Escuro.Fundo, Tema.Escuro.Barra });
        var mumu = new ImageView(this);
        mumu.SetImageResource(ArteDoTema.Para(this, Resource.Drawable.mumu_pipoca));
        mumu.SetScaleType(ImageView.ScaleType.FitCenter);
        _espera.AddView(mumu, Tema.Lp(Tema.Dp(this, 120), Tema.Dp(this, 90)));
        _estado = Tema.Texto(this, "Conectando...", 18, negrito: true);
        _estado.Gravity = GravityFlags.Center;
        _estado.SetPadding(0, Tema.Dp(this, 10), 0, 0);
        _espera.AddView(_estado);
        _giro = new ProgressBar(this) { Indeterminate = true };
        _giro.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Escuro.Destaque);
        _espera.AddView(_giro, Tema.Lp(Tema.Dp(this, 32), Tema.Dp(this, 32)));
        _detalhe = Tema.Texto(this, _titulo.Length > 0 ? $"{_anfitriaoNome} · {_titulo}" : "", 14, Tema.Escuro.TextoApagado);
        _detalhe.Gravity = GravityFlags.Center;
        _detalhe.SetPadding(Tema.Dp(this, 24), Tema.Dp(this, 6), Tema.Dp(this, 24), 0);
        _espera.AddView(_detalhe);
        _voltar = Tema.Botao(this, "Voltar", principal: false);
        _voltar.Visibility = ViewStates.Gone;
        _voltar.Click += (_, _) => Finish();
        _espera.AddView(_voltar, Tema.Lp(Tema.Dp(this, 160), Tema.Ajusta, 0, 0));
        raiz.AddView(_espera, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio));
        SetContentView(raiz);
    }

    void TelaCheia()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35)) Window!.SetDecorFitsSystemWindows(false);
            Window!.InsetsController?.Hide(WindowInsets.Type.SystemBars());
            Window.InsetsController!.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
        else
#pragma warning disable CA1422
            Window!.DecorView.SystemUiFlags = SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
#pragma warning restore CA1422
    }

    void Falhou(string motivo, string detalhe = "")
    {
        LogApp.Info("[filme] " + motivo + (detalhe.Length > 0 ? " " + detalhe : ""));
        RunOnUiThread(() =>
        {
            if (_fechando) return;
            _espera.Visibility = ViewStates.Visible;
            _estado.Text = motivo; _estado.SetTextColor(Tema.Escuro.Erro);
            _giro.Visibility = ViewStates.Gone;
            _detalhe.Text = detalhe;
            _voltar.Visibility = ViewStates.Visible;
        });
    }

    void Avisar(string texto)
    {
        RunOnUiThread(() =>
        {
            if (_fechando) return;
            _aviso.Text = texto;
            _aviso.Visibility = ViewStates.Visible;
            if (_sumirAviso != null) _ui.RemoveCallbacks(_sumirAviso);
            _sumirAviso = new Java.Lang.Runnable(() => _aviso.Visibility = ViewStates.Gone);
            _ui.PostDelayed(_sumirAviso, 4500);
        });
    }

    // ------------------------------------------------------------------ entrar na sessão (o protocolo do Windows)

    async Task EntrarAsync()
    {
        var ct = _cts.Token;
        try
        {
            var sala = Sessao.Sala;
            if (sala is not { Connected: true })
                for (int i = 0; i < 150 && !_fechando && (sala = Sessao.Sala) is not { Connected: true }; i++) await Task.Delay(100, ct);
            if (sala is not { Connected: true }) { Falhou("Sem conexão com o grupo.", sala?.Status ?? ""); return; }
            if (sala.Find(_anfitriao) is not { } anfitriao) { Falhou("O anfitrião saiu da sala do grupo."); return; }
            _sid = IdAleatorio(12);
            var lan = Streemu.Proto.Player.PecaDoPlayer.IpLocal();
            PeerLink link;
            try
            {
                // streamId "filme": é o que o Windows usa para separar as ligações do filme das da live
                link = await PeerLink.CreateAsync(_sid, _anfitriao, anfitriao.Nick, iAmSender: false, localMode: false, Sessao.Apelido,
                    lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), "filme");
            }
            catch (Exception e) { Falhou($"Não deu para preparar a conexão com {anfitriao.Nick}.", e.Message); return; }
            if (_fechando) { link.Dispose(); return; }
            _link = link;
            link.Tunnel.StateChanged += (s, msg) => LogApp.Info($"[filme] ligação {s}: {msg}");
            link.Control += AoReceberControle;
            var quero = new JsonObject { ["tipo"] = "filme_quero", ["sessao"] = _sid, ["codigo"] = link.MyCode, ["filme"] = _filme };
            if (_stream) quero["capacidade"] = CapacidadeAparelho.Json(this);
            await sala.SendTo(_anfitriao, quero);
            Estado($"Esperando {anfitriao.Nick} aceitar...");
            var ate = DateTime.Now.AddSeconds(25);
            while (DateTime.Now < ate && !_fechando)
            {
                if (_recusa != null) { Falhou($"{anfitriao.Nick} não aceitou.", _recusa); return; }
                if (link.Connected && link.Tunnel.PeerClockOffsetUs != null)
                {
                    LogApp.Info($"[filme] ligado a {anfitriao.Nick} em {link.Tunnel.ConnectMs} ms; relógio com ida e volta de {link.Tunnel.PeerClockRttMs:N1} ms.");
                    break;
                }
                await Task.Delay(100, ct);
            }
            if (_fechando) return;
            if (!(link.Connected && link.Tunnel.PeerClockOffsetUs != null))
            {
                Falhou(link.Connected ? $"{anfitriao.Nick} tem uma versão do app sem o modo filme (o relógio não respondeu)." : $"Não consegui conectar com {anfitriao.Nick} em 25 s.",
                       link.Connected ? "Ele precisa atualizar." : "Tente de novo.");
                return;
            }
            if (_stream) await EntrarNaTransmissaoAsync(link, ct);
            else RunOnUiThread(() => AbrirPlayer(link));
        }
        catch (System.OperationCanceledException) { }
        catch (Exception e) { Falhou("Não deu para entrar no filme.", e.Message); }
    }

    void Estado(string t) => RunOnUiThread(() => { if (!_fechando) _estado.Text = t; });

    void AoReceberSala(string de, string apelido, JsonObject msg) => RunOnUiThread(() =>
    {
        // só vale o que veio do anfitrião e é desta ligação (o "sessao" vem do app dele; "de" é carimbado pela sala)
        string? tipo = JsonSeguro.Texto(msg["tipo"]);
        if (_anf) { if (!_fechando && tipo is "filme_quero" or "filme_parar") _host?.AoReceberSala(de, apelido, msg); return; }   // anfitrião: quem pede para entrar e quem sai
        if (de != _anfitriao) return;
        if (tipo == "filme_fechado") { if (JsonSeguro.Texto(msg["filme"]) == _filme) Encerrado($"{_anfitriaoNome} fechou o filme."); return; }
        if (_link == null || JsonSeguro.Texto(msg["sessao"]) != _sid) return;
        switch (tipo)
        {
            case "filme_aceito" when !_aceito:
                _aceito = true;
                try { _link.Connect(JsonSeguro.Texto(msg["codigo"]) ?? ""); }
                catch (Exception e) { _recusa = $"{apelido} mandou um código inválido ({e.Message}). Vocês estão em versões diferentes?"; }
                Estado("Conectando...");
                break;
            case "filme_recusado":
                _recusa = JsonSeguro.Texto(msg["motivo"]) ?? $"{apelido} recusou.";
                if (_sessao != null) Encerrado(_recusa);
                break;
            case "filme_parar":
                Encerrado($"{apelido} encerrou a sessão.");
                break;
        }
    });

    void AoNaoEntregar(string para, JsonObject? msg)
    {
        if (para == _anfitriao && _sessao == null && _link != null) _recusa = $"{_anfitriaoNome} saiu do grupo.";
    }

    /// <summary>Mensagens filme_* pelo túnel (thread do túnel): vão para a Sessao na thread da tela, como o Windows faz.</summary>
    void AoReceberControle(JsonObject m)
    {
        string tipo = JsonSeguro.Texto(m["type"]) ?? "";
        if (!tipo.StartsWith("filme_")) return;
        if (tipo.StartsWith("filme_stream_"))
        {
            if (_stream) RunOnUiThread(() => AoReceberStream(tipo, m));
            return;
        }
        RunOnUiThread(() =>
        {
            if (_fechando || _link == null) return;
            m["id"] = _link.PeerId;   // quem fala é quem está na ligação: o "id" que o outro app mandou não vale
            if (JsonSeguro.Texto(m["filme"]) != _filme) return;
            if (tipo == "filme_fim") { Encerrado($"{_anfitriaoNome} fechou o filme."); return; }
            if (tipo == "filme_trocar") { Encerrado($"{_anfitriaoNome} passou para outro filme. Entre de novo pelo Cinema."); return; }   // a fila de episódios fica para depois
            try { _sessao?.Receber(m); }
            catch (Exception e) { LogApp.Info($"[filme] mensagem {tipo} com campo inválido: {e.Message}"); }   // o JSON vem do PC de outra pessoa
        });
    }

    void AbrirPlayer(PeerLink link)
    {
        try { _player = new PlayerFilme(this, _video, _arquivo!, comecarTocando: false); }
        catch (Exception e) { Falhou("O player não abriu.", e.Message); return; }
        _botaoFaixas.Visibility = ViewStates.Visible;
        var t = link.Tunnel;
        long? Relogio() => t.PeerClockOffsetUs is long o ? Tunnel.NowUs + o : null;
        string meuId = Sessao.Sala?.MyId ?? Sessao.Conta.ContaId ?? "";
        var s = _sessao = new SessaoFilme(false, _filme, _titulo, _anfitriaoNome, Sessao.Apelido, _player, Relogio, () => Math.Max(0, t.RttMs),
            m => { if (m.DeepClone() is JsonObject copia) t.SendControl(copia); }, meuId)
            { NomeNaSala = id => Sessao.Sala?.Find(id)?.Nick };
        s.Aviso += Avisar;
        s.Mudou += () => RunOnUiThread(AtualizarBotao);
        _espera.Visibility = ViewStates.Gone;
        _barra.Visibility = ViewStates.Visible;
        LogApp.Info($"[filme] espectador: {_titulo} ({_filme})");
        // o anfitrião só deixa o primeiro ▶ valer depois de todos "prontos" (4.3); o arquivo já bateu pela identidade
        t.SendControl(new JsonObject { ["type"] = "filme_pronto", ["filme"] = _filme, ["pronto"] = true, ["nome"] = Sessao.Apelido, ["id"] = meuId, ["primeiro"] = true });
        _ = Task.Run(() => Painel(_cts.Token));
    }

    // ------------------------------------------------------------------ pela transmissão (F3)

    /// <summary>filme_stream_quero (com a capacidade) e espera o filme_stream_info com a época, a posição e o estado da Sessão do anfitrião (15 s, como o Windows).</summary>
    async Task EntrarNaTransmissaoAsync(PeerLink link, CancellationToken ct)
    {
        _streamInfo = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Estado($"Pedindo a transmissão a {_anfitriaoNome}...");
        link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_quero", ["capacidade"] = CapacidadeAparelho.Json(this) });
        var pronto = await Task.WhenAny(_streamInfo.Task, Task.Delay(15000, ct));
        if (pronto != _streamInfo.Task) { Falhou($"{_anfitriaoNome} não respondeu ao pedido para assistir.", "A transmissão do filme precisa estar ligada no PC dele."); return; }
        if (_streamInfo.Task.IsFaulted) { Falhou(_streamInfo.Task.Exception!.GetBaseException().Message, "Use \"Baixar\" para assistir com o arquivo."); return; }   // o anfitrião recusou: o aparelho não toca o arquivo pela transmissão
        var info = _streamInfo.Task.Result;
        RunOnUiThread(() => AbrirPlayerStream(link, info));
    }

    void AbrirPlayerStream(PeerLink link, JsonObject info)
    {
        if (_fechando) return;
        PlayerStream ps;
        try { ps = _pstream = new PlayerStream(this, _video, comecarTocando: false); }
        catch (Exception e) { Falhou("O player não abriu.", e.Message); return; }
        ps.Duracao = JsonSeguro.Numero(info["duracao"]) ?? double.NaN;
        _duracao = ps.Duracao;
        ps.Recarregar += () => link.Tunnel.SendControl(new JsonObject { ["type"] = "filme_stream_recarregar" });
        ps.Falhou += m => Avisar("O player da transmissão falhou: " + m);
        var t = link.Tunnel;
        long? Relogio() => t.PeerClockOffsetUs is long o ? Tunnel.NowUs + o : null;
        string meuId = Sessao.Sala?.MyId ?? Sessao.Conta.ContaId ?? "";
        var s = _sessao = new SessaoFilme(false, _filme, _titulo, _anfitriaoNome, Sessao.Apelido, ps, Relogio, () => Math.Max(0, t.RttMs),
            m => EnviarDaSessao(t, m), meuId) { NomeNaSala = id => Sessao.Sala?.Find(id)?.Nick };
        s.Aviso += Avisar;
        s.Mudou += () => RunOnUiThread(AtualizarBotao);
        _espera.Visibility = ViewStates.Gone;
        _barra.Visibility = ViewStates.Visible;
        LogApp.Info($"[filme] espectador pela transmissão: {_titulo} ({_filme}) · {JsonSeguro.Texto(info["perfil"])}");
        // como a StreamTela: primeiro a linha do tempo do anfitrião, depois a época (a Sessao espera o dado do ponto chegar no cache)
        if (info["estado"] is JsonObject estado)
        {
            estado["filme"] ??= _filme;
            try { s.Receber(estado); } catch (Exception e) { LogApp.Info("[filme] estado da transmissão inválido: " + e.Message); }
        }
        AoReceberStream("filme_stream_epoca", info);
        _ = Task.Run(() => Painel(_cts.Token));
    }

    /// <summary>O que a Sessao manda, na língua da transmissão: quem assiste pela stream não fica "pronto" (não tem arquivo); o carregado e os pedidos têm mensagem própria.</summary>
    static void EnviarDaSessao(Tunnel t, JsonObject m)
    {
        switch (JsonSeguro.Texto(m["type"]))
        {
            case "filme_carregado": t.SendControl(new JsonObject { ["type"] = "filme_stream_carregado", ["seq"] = m["seq"]?.DeepClone(), ["filme"] = m["filme"]?.DeepClone() }); break;
            case "filme_pedido": t.SendControl(new JsonObject { ["type"] = "filme_stream_cmd", ["cmd"] = m["acao"]?.DeepClone(), ["pos"] = m["pos"]?.DeepClone() }); break;
        }
    }

    void AoReceberStream(string tipo, JsonObject m)
    {
        if (_fechando || _link == null) return;
        switch (tipo)
        {
            case "filme_stream_info": _streamInfo?.TrySetResult(m); break;
            case "filme_stream_epoca":
                if (_pstream is { } ps && JsonSeguro.Inteiro(m["epoca"]) is long n)
                    ps.Epoca((int)n, JsonSeguro.Numero(m["pos"]) ?? 0, _link.Relay.Port, _link.Tunnel.Passphrase);
                break;
            case "filme_stream_aviso":
                string aviso = JsonSeguro.Texto(m["texto"]) ?? "";
                Avisar(aviso);
                if (aviso == AnfitriaoCinema.AvisoNaoToca) _streamInfo?.TrySetException(new InvalidOperationException(aviso));   // acaba a espera do info na hora, em vez de 15 s
                break;
        }
    }

    // ------------------------------------------------------------------ anfitrião (F4)

    /// <summary>Abre o player no arquivo, a Sessao de anfitrião (relógio local) e o protocolo da sala; o serviço em primeiro plano segura o app.</summary>
    void IniciarAnfitriao()
    {
        var sala = Sessao.Sala;
        if (_arquivo == null || _filme.Length == 0) { Falhou("Faltou o arquivo."); return; }
        if (_anfAtual != null) { Falhou("Você já está numa sessão do Cinema."); return; }
        if (sala is not { Connected: true }) { Falhou("Sem conexão com o grupo.", sala?.Status ?? ""); return; }
        try { _player = new PlayerFilme(this, _video, _arquivo, comecarTocando: false); }
        catch (Exception e) { Falhou("O player não abriu.", e.Message); return; }
        _anfAtual = this;
        string meuId = sala.MyId ?? Sessao.Conta.ContaId ?? "";
        var s = _sessao = new SessaoFilme(true, _filme, _titulo, Sessao.Apelido, Sessao.Apelido, _player, () => Tunnel.NowUs, () => 0,
            m => _host?.EnviarATodos(m), meuId) { NomeNaSala = id => Sessao.Sala?.Find(id)?.Nick };
        s.Aviso += Avisar;
        s.Mudou += () => RunOnUiThread(AtualizarBotao);
        var arquivo = _arquivo;
        var h = _host = new AnfitriaoCinema(s, Sessao.Conta.ContaId ?? "", Sessao.Apelido, () => AbrirStream(arquivo), _ext, CriarLigacaoDeEnvio,
            (id, m) => Sessao.Sala is { } sl ? sl.SendTo(id, m) : Task.CompletedTask,
            () => Sessao.Sala?.Friends.Select(f => f.Id).ToList() ?? new List<string>(),
            recuar: () => ServicoTransmissao.AoVivo, naThread: a => RunOnUiThread(() => { if (!_fechando) a(); }));
        h.ServindoArquivo += AoServirArquivo;
        h.Mudou += () => RunOnUiThread(AtualizarBotao);
        _copia = CopiaDoFilme.Ligar(this, h, arquivo, _titulo);   // F5: h264/hevc com AAC, AC-3, E-AC-3 ou MP3 vão pela transmissão sem recomprimir; o resto só por download
        if (_copia?.IdentidadeDaEscolhida is { } escolhida) _player.ForcarAudio(escolhida);   // o anfitrião ouve a faixa que a transmissão leva
        _botaoFaixas.Visibility = ViewStates.Visible;
        h.Iniciar();
        ServicoCinema.Iniciar(this, _titulo);
        ServicoCinema.PediramEncerrar += AoPedirEncerrar;
        _espera.Visibility = ViewStates.Gone;
        _barra.Visibility = ViewStates.Visible;
        _painelAnf.Visibility = ViewStates.Visible;
        LogApp.Info($"[filme] anfitrião: {_titulo} ({_filme})");
        _ = Task.Run(() => Painel(_cts.Token));
    }

    void AoPedirEncerrar() => RunOnUiThread(Finish);

    /// <summary>O arquivo do seletor chega como descritor (sem caminho): um FileStream com seek por leitor.</summary>
    Stream AbrirStream(Android.Net.Uri uri)
    {
        using var pfd = ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new IOException("o arquivo não abriu");
        return new FileStream(new SafeFileHandle((IntPtr)pfd.DetachFd(), ownsHandle: true), FileAccess.Read, 1 << 16);
    }

    static Task<PeerLink> CriarLigacaoDeEnvio(string sid, string de, string apelido)
    {
        var lan = Streemu.Proto.Player.PecaDoPlayer.IpLocal();
        return PeerLink.CreateAsync(sid, de, apelido, iAmSender: true, localMode: false, Sessao.Apelido,
            lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), "filme");
    }

    /// <summary>Alguém pediu o arquivo. Arquivo grande em rede com franquia (dados móveis): avisa na tela, sem bloquear (a pessoa decide).</summary>
    void AoServirArquivo(long bytes)
    {
        bool franquia = false;
        try { franquia = (GetSystemService(ConnectivityService) as Android.Net.ConnectivityManager)?.IsActiveNetworkMetered == true; }
        catch (Exception e) { LogApp.Info("[filme] rede com franquia: " + e.Message); }
        LogApp.Info($"[filme] servindo o arquivo ({bytes / 1e6:N0} MB); rede com franquia: {franquia}.");
        if (!franquia || bytes < 100L << 20) return;
        string texto = $"Rede com franquia: mandar o filme ({bytes / 1e9:N1} GB) gasta seus dados móveis. Conecte no Wi-Fi ou encerre a sessão.";
        RunOnUiThread(() => { if (_fechando) return; _franquia.Text = texto; _franquia.Visibility = ViewStates.Visible; Avisar(texto); });
    }

    void AtualizarPainelAnfitriao()
    {
        if (!_anf || _sessao is not { } s) return;
        var gente = s.Amigos.Take(6).Select(kv => $"{s.NomeDe(kv.Key)} · {kv.Value}").ToList();
        string mais = s.Amigos.Count > 6 ? $"\n+{s.Amigos.Count - 6}" : "";
        _pessoas.Text = gente.Count == 0 ? "Ninguém entrou ainda." : "Junto:\n" + string.Join("\n", gente) + mais;
        _todos.Text = s.TodosControlam ? "Todos controlam: sim" : "Todos controlam: não";
        string nota = s.Aguardando ? "Esperando todos ficarem prontos" : s.Amigos.Count == 0 ? "Esperando amigos" : s.Amigos.Count == 1 ? "1 pessoa junto" : $"{s.Amigos.Count} pessoas juntas";
        var (envios, bytes) = _host?.Enviando() ?? (0, 0);
        if (envios > 0) nota += $" · enviando o arquivo ({bytes / 1e6:N0} MB)";
        if (nota != _ultimaNotificacao) { _ultimaNotificacao = nota; ServicoCinema.Atualizar(nota); }
    }

    void AtualizarBotao()
    {
        if (_sessao is not { } s) return;
        _tocar.Text = s.Aguardando ? "Começar agora" : s.Tocando ? "Pausar" : "Tocar";
        AtualizarPainelAnfitriao();
        float alfa = s.PodeControlar ? 1f : 0.45f;   // botões apagados para quem não controla; o toque ainda explica o porquê (aviso da Sessao)
        _tocar.Alpha = alfa; _linha.Alpha = alfa;
    }

    /// <summary>Linha do tempo na tela a cada 0,5 s e "FILME erro=… vel=…" no logcat a cada 2 s.</summary>
    async Task Painel(CancellationToken ct)
    {
        int n = 0;
        try
        {
            while (!ct.IsCancellationRequested && !_fechando && _sessao is { } s)
            {
                if (double.IsNaN(_duracao) && _player is { } p) _duracao = await p.DuracaoAsync();
                double pos = s.Posicao, dur = _duracao;
                RunOnUiThread(() =>
                {
                    if (_fechando) return;
                    _tempo.Text = double.IsNaN(dur) ? SessaoFilme.Tempo(pos) : $"{SessaoFilme.Tempo(pos)} / {SessaoFilme.Tempo(dur)}";
                    if (!_arrastando && !double.IsNaN(dur) && dur > 0) _linha.Progress = (int)Math.Clamp(pos / dur * 1000, 0, 1000);
                    AtualizarBotao();
                });
                if (n++ % 4 == 0)
                    Logcat.Info("Streemu", FormattableString.Invariant($"FILME erro={s.UltimoErroMs:F1} vel={s.Velocidade:F3}"));
                await Task.Delay(500, ct);
            }
        }
        catch (System.OperationCanceledException) { }
    }

    void Encerrado(string aviso)
    {
        if (_fechando) return;
        LogApp.Info("[filme] " + aviso);
        try { _sessao?.Dispose(); } catch (Exception e) { LogApp.Info("[filme] fechar a sessão: " + e.Message); }
        _sessao = null; _player = null; _pstream = null;   // a Sessao fecha o player
        Falhou(aviso);
        _barra.Visibility = ViewStates.Gone;
    }

    protected override void OnDestroy()
    {
        _fechando = true;
        _cts.Cancel();
        Sessao.Mensagem -= AoReceberSala;
        Sessao.NaoEntregue -= AoNaoEntregar;
        if (_anf)
        {
            ServicoCinema.PediramEncerrar -= AoPedirEncerrar;
            if (_anfAtual == this) _anfAtual = null;
            var h = _host; _host = null;
            var app = ApplicationContext!;
            if (h != null) _ = h.FecharAsync().ContinueWith(_ => ServicoCinema.Parar(app));   // filme_fim x3, filme_fechado e as ligações 1 s depois
            else ServicoCinema.Parar(app);
        }
        var link = _link; _link = null;
        if (link != null && Sessao.Sala is { } sala) _ = sala.SendTo(_anfitriao, new JsonObject { ["tipo"] = "filme_parar", ["sessao"] = _sid });
        try { _sessao?.Dispose(); } catch (Exception e) { LogApp.Info("[filme] fechar a sessão: " + e.Message); }
        if (link != null) Task.Run(link.Dispose);
        base.OnDestroy();
    }

    static string IdAleatorio(int n)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, n));
    }
}
