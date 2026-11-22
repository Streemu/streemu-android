using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Ui;

namespace Streemu.Proto.Player;

enum Enquadramento { Original, Ampliar, Preencher, Widescreen, Classico, Ultrawide, Vertical, Personalizado }

/// <summary>
/// A tela de uma peça (spec 003): a Surface do vídeo, a espera com o passo atual (ou o erro com "Tentar de novo"), e, com mais de uma peça, o nome,
/// o som e o "×". O player só muda o tamanho e a posição das molduras; quem decide a camada é <see cref="TrocarVideo"/>: a SurfaceView da principal fica
/// atrás da janela, a das miniaturas é "media overlay" e fica por cima dela (a ordem entre duas SurfaceViews na mesma camada não é garantida, por isso a
/// camada é escolhida ao criar a view, e a peça que muda de papel ganha uma view nova e o decodificador troca a saída sem reabrir).
/// </summary>
sealed class MolduraDaPeca : FrameLayout, ISurfaceHolderCallback
{
    public PecaDoPlayer Peca { get; }
    public Enquadramento Modo { get; set; }
    public (int Largura, int Altura)? Personalizada { get; set; }
    public bool Sobreposta { get; private set; }
    public event Action<MolduraDaPeca>? Tocada, FecharPedido, SomPedido, DeNovoPedido;

    SurfaceView? _video, _velho;
    readonly LinearLayout _espera, _acoes, _rotulo;
    readonly ImageView _mumu, _icSom;
    readonly ProgressBar _giro;
    readonly TextView _estado, _detalhe, _aviso, _nome;
    readonly ImageView _fechar;
    TextView _botaoSair = null!;
    bool _compacta, _multi;

    public MolduraDaPeca(Context c, PecaDoPlayer peca) : base(c)
    {
        Peca = peca;
        SetBackgroundColor(Color.Black);
        Clickable = true;
        Click += (_, _) => Tocada?.Invoke(this);

        _espera = Tema.Coluna(c);
        _espera.SetGravity(GravityFlags.Center);
        _espera.Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Tema.Fundo, Tema.Barra });
        _mumu = new ImageView(c);
        _mumu.SetImageResource(Resource.Drawable.mumu_pipoca);
        _mumu.SetScaleType(ImageView.ScaleType.FitCenter);
        // deitado, a altura é pouca (~400 dp num celular): o Mumu encolhe para o aviso e os botões caberem
        int ladoMumu = Math.Clamp((int)(Math.Min(c.Resources!.Configuration!.ScreenHeightDp, c.Resources.Configuration.ScreenWidthDp) * 0.26), 72, 150);
        _espera.AddView(_mumu, Tema.Lp(Tema.Dp(c, ladoMumu), Tema.Dp(c, ladoMumu)));
        var quem = Tema.Texto(c, peca.CodigoTeste != null ? "Modo de teste" : peca.Apelido, 22, negrito: true);
        quem.Gravity = GravityFlags.Center;
        quem.SetPadding(0, Tema.Dp(c, 12), 0, 0);
        _quem = quem;
        _espera.AddView(quem);
        if (peca.Jogo.Length > 0) { _jogo = Tema.Texto(c, peca.Jogo, 15, Tema.TextoApagado); _jogo.Gravity = GravityFlags.Center; _espera.AddView(_jogo); }
        var linhaEstado = Tema.Linha(c, GravityFlags.Center);
        linhaEstado.SetPadding(0, Tema.Dp(c, 20), 0, 0);
        _linhaEstado = linhaEstado;
        _giro = new ProgressBar(c) { Indeterminate = true };
        _giro.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.DestaqueSuave);
        linhaEstado.AddView(_giro, Tema.Lp(Tema.Dp(c, 20), Tema.Dp(c, 20)));
        _estado = Tema.Texto(c, peca.Titulo, 15);
        _estado.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
        linhaEstado.AddView(_estado, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        _espera.AddView(linhaEstado);
        _detalhe = Tema.Texto(c, "", 13, Tema.TextoApagado);
        _detalhe.Gravity = GravityFlags.Center;
        _detalhe.SetMaxWidth(Tema.Dp(c, 420));
        _detalhe.SetPadding(Tema.Dp(c, 24), Tema.Dp(c, 6), Tema.Dp(c, 24), 0);
        _espera.AddView(_detalhe);
        _aviso = Tema.Texto(c, "", 13, Tema.Aviso);
        _aviso.Gravity = GravityFlags.Center;
        _aviso.SetMaxWidth(Tema.Dp(c, 440));
        _aviso.SetPadding(Tema.Dp(c, 24), Tema.Dp(c, 10), Tema.Dp(c, 24), 0);
        _aviso.Visibility = ViewStates.Gone;
        _espera.AddView(_aviso);
        _acoes = Tema.Linha(c, GravityFlags.Center);
        _acoes.SetPadding(0, Tema.Dp(c, 20), 0, 0);
        _acoes.Visibility = ViewStates.Gone;
        _botaoSair = Tema.Botao(c, "Voltar", principal: false);
        _botaoSair.Click += (_, _) => FecharPedido?.Invoke(this);
        _acoes.AddView(_botaoSair, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        _botaoDeNovo = Tema.Botao(c, "Tentar de novo");
        _botaoDeNovo.Click += (_, _) => DeNovoPedido?.Invoke(this);
        var lpDeNovo = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
        lpDeNovo.LeftMargin = Tema.Dp(c, 12);
        _acoes.AddView(_botaoDeNovo, lpDeNovo);
        if (peca.CodigoTeste != null) _botaoDeNovo.Visibility = ViewStates.Gone;
        _espera.AddView(_acoes);
        AddView(_espera, new LayoutParams(Tema.Cheio, Tema.Cheio));

        // com mais de uma peça: o nome e o som no canto de cima à esquerda, e o "×" no de cima à direita
        _rotulo = Tema.Linha(c);
        _rotulo.Background = Tema.Forma(c, Color.Argb(150, 0, 0, 0), 12);
        _rotulo.SetPadding(Tema.Dp(c, 2), 0, Tema.Dp(c, 10), 0);
        _icSom = Tema.Icone(c, Resource.Drawable.ic_sem_som, Color.White, 28);
        _icSom.SetPadding(Tema.Dp(c, 4), Tema.Dp(c, 4), Tema.Dp(c, 4), Tema.Dp(c, 4));
        _icSom.Clickable = true;
        _icSom.ContentDescription = $"Ouvir {peca.Apelido}";
        _icSom.Click += (_, _) => SomPedido?.Invoke(this);
        _rotulo.AddView(_icSom, Tema.Lp(Tema.Dp(c, 32), Tema.Dp(c, 32)));
        _nome = Tema.Texto(c, peca.Apelido, 12, Color.White, negrito: true);
        _nome.SetMaxLines(1);
        _nome.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        _nome.SetMaxWidth(Tema.Dp(c, 110));
        _rotulo.AddView(_nome);
        _rotulo.Visibility = ViewStates.Gone;
        var lpRotulo = new LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.Start);
        lpRotulo.SetMargins(Tema.Dp(c, 6), Tema.Dp(c, 6), 0, 0);
        AddView(_rotulo, lpRotulo);

        _fechar = Tema.Icone(c, Resource.Drawable.ic_fechar, Color.White, 28);
        _fechar.SetPadding(Tema.Dp(c, 5), Tema.Dp(c, 5), Tema.Dp(c, 5), Tema.Dp(c, 5));
        _fechar.Background = Tema.Forma(c, Color.Argb(150, 0, 0, 0), 16);
        _fechar.Clickable = true;
        _fechar.ContentDescription = $"Fechar a live de {peca.Apelido}";
        _fechar.Click += (_, _) => FecharPedido?.Invoke(this);
        _fechar.Visibility = ViewStates.Gone;
        var lpFechar = new LayoutParams(Tema.Dp(c, 32), Tema.Dp(c, 32), GravityFlags.Top | GravityFlags.End);
        lpFechar.SetMargins(0, Tema.Dp(c, 6), Tema.Dp(c, 6), 0);
        AddView(_fechar, lpFechar);

        // o aviso de qualidade: por último, então fica por cima da SurfaceView (a da principal está atrás da janela e a da miniatura é media overlay)
        if (AvisoDeQualidade.Ligado(c))
        {
            _indicador = new IndicadorDeQualidade(c);
            AddView(_indicador, new LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.End));
            peca.QualidadeMudou += () => { _indicador.Mostrar(Peca.QualidadeAtual); PosicionarIndicador(); };
        }
    }

    readonly IndicadorDeQualidade? _indicador;
    readonly int[] _local = new int[2];

    readonly TextView _quem;
    readonly TextView? _jogo;
    readonly LinearLayout _linhaEstado;
    readonly TextView _botaoDeNovo;

    // ================================================================================================ a Surface

    /// <summary>
    /// Põe uma SurfaceView nova nesta moldura, na camada pedida. A anterior só sai quando a nova tem Surface (e o decodificador já mostra nela),
    /// para a imagem não piscar.
    /// </summary>
    public void TrocarVideo(bool sobreposta)
    {
        if (_video != null && Sobreposta == sobreposta) return;
        var sv = new SurfaceView(Context);
        sv.SetZOrderMediaOverlay(sobreposta);
        Sobreposta = sobreposta;
        sv.Holder!.AddCallback(this);
        if (_velho != null && _video != null) { var intermediaria = _video; intermediaria.Holder?.RemoveCallback(this); RemoveView(intermediaria); }   // trocas seguidas: a que nunca ganhou Surface sai, a que está mostrando fica até a nova nascer
        else _velho = _video;
        _video = sv;
        AddView(sv, 0, _velho?.LayoutParameters ?? new LayoutParams(Tema.Cheio, Tema.Cheio, GravityFlags.Center));
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        if (_video?.Holder is not { } atual || (holder as Java.Lang.Object)?.Handle != (atual as Java.Lang.Object)?.Handle) return;
        Peca.DefinirSurface(holder.Surface!);
        if (_velho is not { } velho) return;
        _velho = null;
        velho.Holder?.RemoveCallback(this);
        RemoveView(velho);
    }
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) { }

    // ================================================================================================ o que aparece

    /// <summary>Com mais de uma peça aparecem o nome, o som e o "×"; o botão da espera passa a ser "Fechar" (com uma só, "Voltar" fecha o player).</summary>
    public void Multi(bool multi)
    {
        _multi = multi;
        _rotulo.Visibility = multi ? ViewStates.Visible : ViewStates.Gone;
        _fechar.Visibility = multi ? ViewStates.Visible : ViewStates.Gone;
        _botaoSair.Text = multi ? "Fechar" : "Voltar";
        PosicionarIndicador();
    }

    public void SomLigado(bool ligado)
    {
        _icSom.SetImageResource(ligado ? Resource.Drawable.ic_som : Resource.Drawable.ic_sem_som);
        _icSom.ContentDescription = ligado ? $"Som de {Peca.Apelido}" : $"Ouvir {Peca.Apelido}";
    }

    /// <summary>Redesenha a espera e o vídeo a partir do estado da peça (vem de <see cref="PecaDoPlayer.Mudou"/>, na thread da tela).</summary>
    public void Atualizar()
    {
        bool tocando = Peca.Fase == FaseDaPeca.Tocando, erro = Peca.Fase == FaseDaPeca.Erro;
        _estado.Text = Peca.Titulo;
        _estado.SetTextColor(erro ? Tema.Erro : Tema.CorTexto);
        _giro.Visibility = erro ? ViewStates.Gone : ViewStates.Visible;
        _detalhe.Text = Peca.Detalhe;
        _detalhe.Visibility = _compacta || Peca.Detalhe.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        _aviso.Text = Peca.Aviso;
        _aviso.Visibility = Peca.Aviso.Length > 0 && !_compacta ? ViewStates.Visible : ViewStates.Gone;
        _acoes.Visibility = erro ? ViewStates.Visible : ViewStates.Gone;
        if (tocando)
        {
            _espera.Animate()!.Alpha(0).SetDuration(250).WithEndAction(new Java.Lang.Runnable(() => { if (Peca.Fase == FaseDaPeca.Tocando) _espera.Visibility = ViewStates.Gone; })).Start();
        }
        else { _espera.Animate()!.Cancel(); _espera.Alpha = 1; _espera.Visibility = ViewStates.Visible; }
        Ajustar();
    }

    /// <summary>Peça pequena (miniatura ou célula da grade): sem o Mumu, o detalhe e o aviso, e com texto e botões menores.</summary>
    void AjustarCompacta()
    {
        float dp = Context!.Resources!.DisplayMetrics!.Density;
        bool compacta = Width / dp < 320 || Height / dp < 220;
        if (compacta == _compacta) return;
        _compacta = compacta;
        _mumu.Visibility = _quem.Visibility = compacta ? ViewStates.Gone : ViewStates.Visible;
        // miniatura: só o ícone do som e um "×" menor (o nome cobria quase todo o vídeo; quem é a live aparece ao pôr em destaque)
        _nome.Visibility = compacta ? ViewStates.Gone : ViewStates.Visible;
        _rotulo.SetPadding(Tema.Dp(Context!, 2), 0, Tema.Dp(Context!, compacta ? 2 : 10), 0);
        _rotulo.LayoutParameters = new LayoutParams(compacta ? Tema.Dp(Context!, 36) : Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.Start) { LeftMargin = Tema.Dp(Context!, 6), TopMargin = Tema.Dp(Context!, 6) };
        int lado = Tema.Dp(Context!, compacta ? 24 : 32);
        if (_fechar.LayoutParameters is LayoutParams lpf) { lpf.Width = lpf.Height = lado; _fechar.LayoutParameters = lpf; }
        if (_jogo != null) _jogo.Visibility = compacta ? ViewStates.Gone : ViewStates.Visible;
        _linhaEstado.SetPadding(0, compacta ? 0 : Tema.Dp(Context, 20), 0, 0);
        _estado.SetTextSize(Android.Util.ComplexUnitType.Sp, compacta ? 11 : 15);
        foreach (var b in new[] { _botaoSair, _botaoDeNovo })
        {
            b.SetTextSize(Android.Util.ComplexUnitType.Sp, compacta ? 11 : 16);
            b.SetMinHeight(Tema.Dp(Context, compacta ? 30 : 52));
            b.SetPadding(Tema.Dp(Context, compacta ? 8 : 20), 0, Tema.Dp(Context, compacta ? 8 : 20), 0);
        }
        _acoes.SetPadding(0, Tema.Dp(Context, compacta ? 4 : 20), 0, 0);
        Atualizar();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        AjustarCompacta();
        Ajustar();
        PosicionarIndicador();
    }

    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        base.OnLayout(changed, l, t, r, b);
        PosicionarIndicador();
    }

    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        var r = base.OnApplyWindowInsets(insets);
        PosicionarIndicador();
        return r;
    }

    /// <summary>
    /// Põe o aviso no canto de cima à direita da área visível do vídeo (dentro das barras pretas), a 8 dp da borda, somando as barras do sistema e o recorte
    /// da câmera onde a peça encosta na borda da tela. Usa só o deslocamento da view (sem novo layout), então pode rodar a cada layout.
    /// </summary>
    void PosicionarIndicador()
    {
        if (_indicador is not { Visibility: ViewStates.Visible } || Width <= 0 || Height <= 0) return;
        var c = Context!;
        float dp = c.Resources!.DisplayMetrics!.Density;
        bool pequena = Width / dp < 240;
        if (_indicadorPequeno != pequena) { _indicadorPequeno = pequena; _indicador.Tamanho(pequena); }
        _indicador.Limitar((int)(Width * 0.7f));
        // retângulo visível do vídeo, em coordenadas da moldura
        int vw = Width, vh = Height;
        if (_video?.LayoutParameters is { } lp && lp.Width > 0 && lp.Height > 0) (vw, vh) = (lp.Width, lp.Height);
        int esq = Math.Max(0, (Width - vw) / 2), topo = Math.Max(0, (Height - vh) / 2), dir = Math.Min(Width, (Width + vw) / 2);
        // barras do sistema e recorte: só pesam onde a peça encosta na borda da janela
        int insetTopo = 0, insetDir = 0;
        if (RootWindowInsets is { } wi && RootView is { } raiz)
        {
            int t, r;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var i = wi.GetInsetsIgnoringVisibility(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                (t, r) = (i.Top, i.Right);
            }
            else
            {
#pragma warning disable CA1422
                (t, r) = (wi.SystemWindowInsetTop, wi.SystemWindowInsetRight);
#pragma warning restore CA1422
            }
            GetLocationInWindow(_local);
            insetTopo = Math.Max(0, t - _local[1] - topo);
            insetDir = Math.Max(0, r - (raiz.Width - (_local[0] + Width)) - (Width - dir));
        }
        int margem = Tema.Dp(c, 8), folga = Tema.Dp(c, pequena ? 19 : 12);   // a área de toque tem folga em volta da pílula
        int xFora = (_multi ? Tema.Dp(c, (_compacta ? 24 : 32) + 12) : 0);   // com mais de uma peça o "×" ocupa esse canto
        _indicador.TranslationX = -(Width - dir + insetDir + margem - folga + xFora);
        _indicador.TranslationY = topo + insetTopo + margem - folga;
    }

    bool _indicadorPequeno;

    /// <summary>O vídeo cabe na moldura mantendo a proporção do enquadramento escolhido (ou a do vídeo), sem esticar.</summary>
    public void Ajustar()
    {
        int tw = Width, th = Height, vw = Peca.VideoW, vh = Peca.VideoH;
        if (_video == null || tw <= 0 || th <= 0 || vw <= 0 || vh <= 0) return;
        double proporcao = Modo switch
        {
            Enquadramento.Widescreen => 16d / 9,
            Enquadramento.Classico => 4d / 3,
            Enquadramento.Ultrawide => 21d / 9,
            Enquadramento.Vertical => 9d / 16,
            Enquadramento.Personalizado when Personalizada is { } p => (double)p.Largura / p.Altura,
            Enquadramento.Preencher => (double)tw / th,
            _ => (double)vw / vh,
        };
        // No modo Ampliar, a Surface mantém a proporção e as bordas passam dos limites da janela.
        bool cabePelaAltura = proporcao <= (double)tw / th;
        bool usarAltura = Modo == Enquadramento.Ampliar ? !cabePelaAltura : cabePelaAltura;
        int w = usarAltura ? Math.Max(1, (int)Math.Round(th * proporcao)) : tw;
        int h = usarAltura ? th : Math.Max(1, (int)Math.Round(tw / proporcao));
        if (_video.LayoutParameters is not LayoutParams p2 || p2.Width != w || p2.Height != h)
        {
            var lp = new LayoutParams(w, h, GravityFlags.Center);
            _video.LayoutParameters = lp;
            if (_velho != null) _velho.LayoutParameters = new LayoutParams(w, h, GravityFlags.Center);
        }
    }
}
