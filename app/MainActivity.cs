using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Ui;
using Logcat = Android.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// A tela principal. Sem conta: a entrada (navegador + código de 6 dígitos). Com conta: o Início, com três abas: Ao vivo (as lives do grupo, com
/// miniatura), Grupo (quem está online, offline e a atividade recente) e Conta. A navegação fica embaixo no celular em pé e vira uma barra lateral em
/// tela larga (600 dp ou mais: celular deitado, dobrável aberto, tablet), onde a lista de lives também ganha colunas.
/// No build Debug, abrir com "--es codigo X" (testar-no-celular.ps1) vai direto para o player no modo de código, sem sala.
/// </summary>
[Activity(Label = "Streemu", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
          WindowSoftInputMode = SoftInput.AdjustResize, LaunchMode = LaunchMode.SingleTask)]
// convite de grupo: streemu://grupo/<código> e https://www.streemu.com.br/g/<código> (esse com verificação de link, para abrir direto no app)
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "streemu", DataHost = "grupo")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, AutoVerify = true,
              DataScheme = "https", DataHost = "www.streemu.com.br", DataPathPrefix = "/g/")]
public class MainActivity : Android.App.Activity
{
    enum Aba { AoVivo, Grupo, Conta }

    FrameLayout _raiz = null!, _conteudo = null!;
    Aba _aba = Aba.AoVivo;
    bool _redesenhoMarcado, _visivel;
    readonly List<(Aba Aba, LinearLayout Item)> _botoesNav = new();
    TextView _grupoNome = null!, _salaEstado = null!;
    View _salaPonto = null!;
    ScrollView? _telaAtual;
    Aba _abaDaTela;
    Timer? _miniaturas;
    // entrada na conta
    EditText? _codigo;
    TextView? _erroEntrada;
    LinearLayout? _atualizacaoNaEntrada;
    bool _esperandoCodigo;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Atualizacao.Iniciar(this);
#if DEBUG
        if (!Atualizacao.BloqueiaSala && Intent?.GetStringExtra("codigo") is { Length: > 0 } codigo)
        {
            StartActivity(new Intent(this, typeof(PlayerActivity)).PutExtra("codigo", codigo));
            Finish();
            return;
        }
#endif
        Sessao.Iniciar(this, aguardarAtualizacao: Atualizacao.DeveVerificarAoAbrir);
        // o link do convite vem só na primeira criação: girar a tela ou voltar pelo Recentes reentrega o mesmo Intent
        if (savedInstanceState == null && (Intent == null || (Intent.Flags & ActivityFlags.LaunchedFromHistory) == 0)) Convites.Receber(this, Intent);
        _aba = (Aba)(savedInstanceState?.GetInt("aba") ?? 0);
        _esperandoCodigo = savedInstanceState?.GetBoolean("esperando") ?? false;
        Janela.BordaABorda(this);
        _raiz = new FrameLayout(this);
        _raiz.SetBackgroundColor(Tema.Fundo);
        Janela.AplicarMargens(_raiz);
        SetContentView(_raiz);
        Montar();
        _raiz.Post(() => Convites.ProcessarPendente(this));
    }

    /// <summary>Com a tela já aberta (SingleTask), um link de convite chega aqui.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent == null) return;
        Intent = intent;   // o equivalente a SetIntent: getIntent() passa a ser o link novo
        if (_raiz == null) return;
        Convites.Receber(this, intent);
        if (!_mostrandoInicio && Convites.Pendente(this) != null) Montar();   // sem conta: a entrada passa a mostrar o aviso do convite
        Convites.ProcessarPendente(this);
    }

    /// <summary>Do convite aceito: a aba Ao vivo, onde o grupo novo aparece.</summary>
    internal void IrParaAoVivo()
    {
        _aba = Aba.AoVivo;
        if (!_mostrandoInicio) return;
        MostrarAba();
        Sessao.PedirMiniaturas(true);
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutInt("aba", (int)_aba);
        outState.PutBoolean("esperando", _esperandoCodigo);
    }

    protected override void OnStart()
    {
        base.OnStart();
        if (_raiz == null) return;
        _visivel = true;
        Sessao.Mudou += MarcarRedesenho;
        Atualizacao.Mudou += RedesenharAtualizacao;
        // a miniatura de cada live: o PC manda a cada ~8 s enquanto alguém pede; pedir de novo a cada 20 s mantém o pedido vivo
        _miniaturas = new Timer(_ => { if (_aba == Aba.AoVivo) Sessao.PedirMiniaturas(true); }, null, 500, 20_000);
        MarcarRedesenho();
    }

    protected override void OnStop()
    {
        _visivel = false;
        Sessao.Mudou -= MarcarRedesenho;
        _miniaturas?.Dispose(); _miniaturas = null;
        Atualizacao.Mudou -= RedesenharAtualizacao;
        if (_raiz != null) Sessao.PedirMiniaturas(false);
        base.OnStop();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_raiz == null) return;
        Atualizacao.Mostrar(this);
        // voltando do navegador: o campo do código já pronto para digitar
        if (_codigo != null) { _codigo.RequestFocus(); MostrarTeclado(_codigo); }
    }

    protected override void OnPause()
    {
        Atualizacao.Ocultar(this);
        base.OnPause();
    }

    internal void MostrarAtualizacao()
    {
        _aba = Aba.AoVivo;
        RedesenharAtualizacao();
        _telaAtual?.ScrollTo(0, 0);
    }

    void RedesenharAtualizacao()
    {
        if (!_visivel) return;
        if (_mostrandoInicio) { Atualizar(); return; }
        _atualizacaoNaEntrada?.RemoveAllViews();
        if (Atualizacao.Disponivel is { } nova)
            _atualizacaoNaEntrada?.AddView(Telas.AvisoDaAtualizacao(this, nova));
    }

    /// <summary>Junta várias mudanças seguidas (lista de amigos, miniatura, avatar) num redesenho só.</summary>
    void MarcarRedesenho()
    {
        if (_redesenhoMarcado) return;
        _redesenhoMarcado = true;
        _raiz.PostDelayed(() => { _redesenhoMarcado = false; if (_visivel) Atualizar(); }, 150);
    }

    bool _mostrandoInicio;
    void Montar()
    {
        _raiz.RemoveAllViews();
        _botoesNav.Clear();
        _codigo = null; _erroEntrada = null; _telaAtual = null;
        _atualizacaoNaEntrada = null;
        _mostrandoInicio = Sessao.Conta.TemConta;
        if (_mostrandoInicio) MontarInicio(); else MontarEntrada();
    }

    void Atualizar()
    {
        if (Sessao.Conta.TemConta != _mostrandoInicio) { Montar(); return; }
        if (_mostrandoInicio) { AtualizarBarra(); MostrarAba(); }
    }

    // ================================================================================================ entrada na conta

    void MontarEntrada()
    {
        bool larga = Tema.LarguraDp(this) >= 600;
        var rolagem = new ScrollView(this) { FillViewport = true };
        var corpo = larga ? Tema.Linha(this, GravityFlags.Center) : Tema.Coluna(this);
        corpo.SetGravity(GravityFlags.Center);
        int m = Tema.Dp(this, 24);
        corpo.SetPadding(m, m, m, m);

        var mumu = new ImageView(this);
        mumu.SetImageResource(Resource.Drawable.mumu_controle);
        mumu.SetScaleType(ImageView.ScaleType.FitCenter);
        corpo.AddView(mumu, larga ? Tema.Lp(0, Tema.Dp(this, 280), 1) : Tema.Lp(Tema.Cheio, Tema.Dp(this, 230)));

        var form = Tema.Coluna(this);
        form.SetGravity(GravityFlags.CenterHorizontal);
        form.AddView(Tema.Texto(this, "Streemu", 34, Tema.DestaqueSuave, negrito: true), Centro(Tema.Dp(this, 12)));
        form.AddView(Centralizado(Tema.Texto(this, "Assista às lives do seu grupo no celular.", 16, Tema.TextoApagado)), Centro(Tema.Dp(this, 4)));
        if (Convites.Pendente(this) != null)
        {
            var convite = Centralizado(Tema.Texto(this, "Você recebeu um convite. Entre na sua conta para aceitar.", 14, Tema.CorTexto));
            convite.Background = Tema.Forma(this, Tema.Selecionado, 12);
            convite.SetPadding(Tema.Dp(this, 14), Tema.Dp(this, 10), Tema.Dp(this, 14), Tema.Dp(this, 10));
            form.AddView(convite, Formulario(Tema.Dp(this, 16)));
        }

        if (!_esperandoCodigo)
        {
            var entrar = Tema.Botao(this, "Entrar com a conta Streemu");
            entrar.Click += (_, _) => _ = EntrarAsync(entrar);
            form.AddView(entrar, Formulario(Tema.Dp(this, 32)));
            form.AddView(Centralizado(Tema.Texto(this, "O navegador abre a página de entrada. Depois é só voltar aqui com o código.", 13, Tema.TextoApagado)), Formulario(Tema.Dp(this, 12)));
        }
        else
        {
            form.AddView(Centralizado(Tema.Texto(this, "Digite o código de 6 dígitos que apareceu no fim da página.", 15)), Formulario(Tema.Dp(this, 28)));
            _codigo = new EditText(this)
            {
                InputType = Android.Text.InputTypes.ClassNumber, Gravity = GravityFlags.Center, Hint = "000000", LetterSpacing = 0.45f,
                Typeface = Typeface.Monospace, ImeOptions = ImeAction.Done,
            };
            _codigo.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(6) });
            _codigo.SetTextSize(Android.Util.ComplexUnitType.Sp, 30);
            _codigo.SetTextColor(Tema.CorTexto);
            _codigo.SetHintTextColor(Tema.Neutro);
            _codigo.Background = Tema.Forma(this, Tema.Campo, 14, Tema.Borda);
            _codigo.SetPadding(0, Tema.Dp(this, 14), 0, Tema.Dp(this, 14));
            // os 6 dígitos confirmam sozinhos: sem precisar achar o botão com o teclado aberto
            _codigo.AfterTextChanged += (_, _) => { if (_codigo?.Text?.Length == 6) _ = ConfirmarAsync(); };
            _codigo.EditorAction += (_, e) => { if (e.ActionId == ImeAction.Done) _ = ConfirmarAsync(); };
            form.AddView(_codigo, Formulario(Tema.Dp(this, 12)));
            _erroEntrada = Centralizado(Tema.Texto(this, "", 14, Tema.Erro));
            form.AddView(_erroEntrada, Formulario(Tema.Dp(this, 8)));
            var confirmar = Tema.Botao(this, "Confirmar");
            confirmar.Click += (_, _) => _ = ConfirmarAsync();
            form.AddView(confirmar, Formulario(Tema.Dp(this, 8)));
            var deNovo = Centralizado(Tema.Texto(this, "Abrir a página de novo", 15, Tema.DestaqueSuave, negrito: true));
            deNovo.SetMinHeight(Tema.Dp(this, 48));
            deNovo.Background = Tema.Tocavel(this, Color.Transparent, 12);
            deNovo.Click += (_, _) => _ = EntrarAsync(null);
            form.AddView(deNovo, Formulario(Tema.Dp(this, 4)));
        }
        corpo.AddView(form, larga ? Tema.Lp(0, Tema.Ajusta, 1) : Tema.Lp(Tema.Cheio, Tema.Ajusta));
        var entrada = Tema.Coluna(this);
        _atualizacaoNaEntrada = Tema.Coluna(this);
        _atualizacaoNaEntrada.SetPadding(m, 0, m, 0);
        entrada.AddView(_atualizacaoNaEntrada, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        entrada.AddView(corpo, Tema.Lp(Tema.Cheio, 0, 1));
        rolagem.AddView(entrada);
        _raiz.AddView(rolagem);
        if (Atualizacao.Disponivel is { } nova)
            _atualizacaoNaEntrada.AddView(Telas.AvisoDaAtualizacao(this, nova));
    }

    static TextView Centralizado(TextView t) { t.Gravity = GravityFlags.Center; return t; }

    static LinearLayout.LayoutParams Centro(int topo) => new(Tema.Ajusta, Tema.Ajusta) { Gravity = GravityFlags.CenterHorizontal, TopMargin = topo };

    /// <summary>Botões e campo com a largura toda, mas no máximo 420 dp (em tela larga não viram uma faixa de ponta a ponta).</summary>
    LinearLayout.LayoutParams Formulario(int topo)
    {
        float disponivel = Tema.LarguraDp(this) - 48;
        if (Tema.LarguraDp(this) >= 600) disponivel /= 2;
        return new LinearLayout.LayoutParams(Tema.Dp(this, Math.Min(420, disponivel)), Tema.Ajusta) { Gravity = GravityFlags.CenterHorizontal, TopMargin = topo };
    }

    async Task EntrarAsync(TextView? botao)
    {
        if (botao != null) { botao.Enabled = false; botao.Text = "Abrindo..."; }
        var (url, erro) = await Sessao.Conta.IniciarAsync(RelayConfig.UrlPadrao);
        RunOnUiThread(() =>
        {
            if (url == null)
            {
                if (botao != null) { botao.Enabled = true; botao.Text = "Entrar com a conta Streemu"; }
                Toast.MakeText(this, $"Não deu para começar a entrada ({erro}). Confira a internet e tente de novo.", ToastLength.Long)!.Show();
                return;
            }
            _esperandoCodigo = true;
            Montar();
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url))); }
            catch (ActivityNotFoundException) { Toast.MakeText(this, "Este celular não tem navegador para abrir a página de entrada.", ToastLength.Long)!.Show(); }
        });
    }

    bool _confirmando;
    async Task ConfirmarAsync()
    {
        if (_confirmando || _codigo == null || _erroEntrada == null) return;
        _confirmando = true;
        _erroEntrada.SetTextColor(Tema.TextoApagado);
        _erroEntrada.Text = "Conferindo...";
        bool ok; string? erro;
        // sem o catch, uma falha (rede, Keystore, disco) deixava "Conferindo..." para sempre e o código não podia ser tentado de novo
        try { (ok, erro) = await Sessao.Conta.RetirarAsync(_codigo.Text ?? ""); }
        catch (Exception e) { ok = false; erro = "não deu para guardar a sessão (" + e.GetType().Name + ")"; Streemu.Util.Log.Info("[conta] login falhou: " + e); }
        RunOnUiThread(() =>
        {
            _confirmando = false;
            if (!ok)
            {
                if (_erroEntrada == null) return;
                _erroEntrada.SetTextColor(Tema.Erro);
                _erroEntrada.Text = $"Não aceitou: {erro}. Confira o código na página.";
                _codigo?.SelectAll();
                return;
            }
            _esperandoCodigo = false;
            EsconderTeclado();
            Sessao.AbrirSala(null);
            Montar();
            Convites.ProcessarPendente(this);   // o convite que fez a pessoa entrar na conta
        });
    }

    void MostrarTeclado(View v) => v.PostDelayed(() => (GetSystemService(InputMethodService) as InputMethodManager)?.ShowSoftInput(v, ShowFlags.Implicit), 150);
    void EsconderTeclado() => (GetSystemService(InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(_raiz.WindowToken, HideSoftInputFlags.None);

    // ================================================================================================ Início

    void MontarInicio()
    {
        bool larga = Tema.LarguraDp(this) >= 600;
        _conteudo = new FrameLayout(this);
        var coluna = Tema.Coluna(this);
        coluna.AddView(BarraDeCima(), Tema.Lp(Tema.Cheio, Tema.Dp(this, 64)));
        coluna.AddView(_conteudo, Tema.Lp(Tema.Cheio, 0, 1));
        if (larga)
        {
            var linha = Tema.Linha(this, GravityFlags.Top);
            linha.AddView(Navegacao(vertical: true), Tema.Lp(Tema.Dp(this, 88), Tema.Cheio));
            linha.AddView(coluna, Tema.Lp(0, Tema.Cheio, 1));
            _raiz.AddView(linha);
        }
        else
        {
            coluna.AddView(Navegacao(vertical: false), Tema.Lp(Tema.Cheio, Tema.Dp(this, 72)));
            _raiz.AddView(coluna);
        }
        AtualizarBarra();
        MostrarAba();
    }

    View BarraDeCima()
    {
        var barra = Tema.Linha(this);
        barra.SetPadding(Tema.Dp(this, 16), 0, Tema.Dp(this, 16), 0);
        var gato = new ImageView(this);
        gato.SetImageResource(Resource.Drawable.gato);
        barra.AddView(gato, Tema.Lp(Tema.Dp(this, 36), Tema.Dp(this, 36)));

        // o grupo é o contexto de tudo: um toque troca (menu com os grupos da conta)
        var seletor = Tema.Linha(this);
        seletor.SetPadding(Tema.Dp(this, 14), 0, Tema.Dp(this, 8), 0);
        seletor.SetMinimumHeight(Tema.Dp(this, 44));
        seletor.Background = Tema.Tocavel(this, Tema.Cartao, 22);
        seletor.Clickable = true;
        seletor.ContentDescription = "Trocar de grupo";
        _grupoNome = Tema.Texto(this, "", 17, negrito: true);
        _grupoNome.SetMaxLines(1);
        _grupoNome.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        seletor.AddView(_grupoNome, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        seletor.AddView(Tema.Icone(this, Resource.Drawable.ic_abrir, Tema.TextoApagado, 22));
        seletor.Click += (_, _) => EscolherGrupo(seletor);
        var lpSel = Tema.Lp(0, Tema.Ajusta, 1);
        lpSel.LeftMargin = Tema.Dp(this, 10);
        lpSel.RightMargin = Tema.Dp(this, 12);
        // o seletor ocupa o que sobra, mas encolhe para caber o nome (não estica até a borda)
        var envelope = Tema.Linha(this);
        envelope.AddView(seletor, Tema.Lp(Tema.Ajusta, Tema.Ajusta));
        barra.AddView(envelope, lpSel);

        _salaPonto = new View(this);
        barra.AddView(_salaPonto, Tema.Lp(Tema.Dp(this, 8), Tema.Dp(this, 8)));
        _salaEstado = Tema.Texto(this, "", 13, Tema.TextoApagado);
        var lpEst = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
        lpEst.LeftMargin = Tema.Dp(this, 6);
        barra.AddView(_salaEstado, lpEst);
        return barra;
    }

    void AtualizarBarra()
    {
        _grupoNome.Text = Sessao.NomeDoGrupo is { Length: > 0 } n ? n : "Sem grupo";
        var sala = Sessao.Sala;
        bool ok = sala?.Connected == true;
        _salaPonto.Background = Tema.Forma(this, ok ? Tema.Ok : Tema.Aviso, 4);
        _salaPonto.Visibility = sala == null ? ViewStates.Gone : ViewStates.Visible;
        _salaEstado.Text = sala == null ? "" : ok ? $"{sala.Friends.Count()} online" : "Conectando";
    }

    void EscolherGrupo(View ancora)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        var gs = Sessao.Conta.Grupos;
        if (gs.Count == 0) return;
        var menu = new PopupMenu(this, ancora);
        for (int i = 0; i < gs.Count; i++) menu.Menu!.Add(0, i, i, gs[i].Nome)!.SetCheckable(true)!.SetChecked(gs[i].Id == Sessao.GrupoId);
        menu.Menu!.SetGroupCheckable(0, true, true);
        menu.MenuItemClick += (_, e) => Sessao.AbrirSala(gs[e.Item!.ItemId].Id);
        menu.Show();
    }

    View Navegacao(bool vertical)
    {
        var nav = vertical ? Tema.Coluna(this) : Tema.Linha(this);
        nav.SetBackgroundColor(Tema.Barra);
        nav.SetGravity(vertical ? GravityFlags.CenterHorizontal | GravityFlags.Top : GravityFlags.Center);
        if (vertical) nav.SetPadding(0, Tema.Dp(this, 12), 0, 0);
        foreach (var (aba, icone, nome) in new[] { (Aba.AoVivo, Resource.Drawable.ic_ao_vivo, "Ao vivo"), (Aba.Grupo, Resource.Drawable.ic_grupo, "Grupo"), (Aba.Conta, Resource.Drawable.ic_conta, "Conta") })
        {
            var item = Tema.Coluna(this);
            item.SetGravity(GravityFlags.Center);
            item.Clickable = true;
            item.ContentDescription = nome;
            // o ícone da aba atual fica dentro de uma pílula: a convenção de navegação do Android
            var pilula = new FrameLayout(this);
            pilula.AddView(Tema.Icone(this, icone, Tema.TextoApagado), new FrameLayout.LayoutParams(Tema.Dp(this, 24), Tema.Dp(this, 24), GravityFlags.Center));
            item.AddView(pilula, Tema.Lp(Tema.Dp(this, 60), Tema.Dp(this, 32)));
            var rotulo = Tema.Texto(this, nome, 12, Tema.TextoApagado, negrito: true);
            var lpR = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
            lpR.TopMargin = Tema.Dp(this, 4);
            item.AddView(rotulo, lpR);
            item.Background = Tema.Tocavel(this, Color.Transparent, 16);
            item.Click += (_, _) =>
            {
                if (_aba == aba) { _telaAtual?.SmoothScrollTo(0, 0); return; }   // tocar na aba atual volta ao topo
                _aba = aba;
                MostrarAba();
                Sessao.PedirMiniaturas(aba == Aba.AoVivo);
            };
            nav.AddView(item, vertical ? Tema.Lp(Tema.Cheio, Tema.Dp(this, 76)) : Tema.Lp(0, Tema.Cheio, 1));
            _botoesNav.Add((aba, item));
        }
        return nav;
    }

    void MostrarAba()
    {
        foreach (var (aba, item) in _botoesNav)
        {
            bool atual = aba == _aba;
            var pilula = (FrameLayout)item.GetChildAt(0)!;
            pilula.Background = atual ? Tema.Forma(this, Tema.Selecionado, 16) : null;
            ((ImageView)pilula.GetChildAt(0)!).ImageTintList = Android.Content.Res.ColorStateList.ValueOf(atual ? Tema.DestaqueSuave : Tema.TextoApagado);
            ((TextView)item.GetChildAt(1)!).SetTextColor(atual ? Tema.CorTexto : Tema.TextoApagado);
            item.Selected = atual;
        }
        int rolagem = _telaAtual != null && _abaDaTela == _aba ? _telaAtual.ScrollY : 0;
        _conteudo.RemoveAllViews();
        _telaAtual = _aba switch
        {
            Aba.AoVivo => Telas.AoVivo(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this)),
            Aba.Grupo => Telas.Grupo(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this),
                                     g => Convites.Convidar(this, g), g => Convites.ConfirmarSaidaDoGrupo(this, g)),
            _ => Telas.Conta(this, TrocarApelido, ConfirmarSaida),
        };
        _abaDaTela = _aba;
        _conteudo.AddView(_telaAtual);
        // redesenho na mesma aba (chegou uma miniatura, alguém entrou): a rolagem fica onde estava
        if (rolagem > 0) { var t = _telaAtual; t.Post(() => t.ScrollTo(0, rolagem)); }
    }

    /// <summary>Saindo do app pelo voltar (não é rotação nem o Android pausando): fecha a sessão do diagnóstico.</summary>
    protected override void OnDestroy()
    {
        if (_raiz != null && IsFinishing) Telemetria.Fechar();
        Convites.Fechar();
        base.OnDestroy();
    }

    void Assistir(Member m)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (m.Live == null) return;
        StartActivity(new Intent(this, typeof(PlayerActivity))
            .PutExtra("conta", m.Id).PutExtra("apelido", m.Nick).PutExtra("sala", m.Live.StreamId)
            .PutExtra("jogo", m.Live.Game).PutExtra("qualidade", m.Live.Quality).PutExtra("desde", m.Live.SinceUnixMs));
    }

    void TrocarApelido()
    {
        var campo = new EditText(this) { Text = Sessao.Apelido };
        campo.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(24) });
        campo.SetSelectAllOnFocus(true);
        campo.SetSingleLine(true);
        var moldura = new FrameLayout(this);
        moldura.SetPadding(Tema.Dp(this, 22), Tema.Dp(this, 8), Tema.Dp(this, 22), 0);
        moldura.AddView(campo);
        new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!
            .SetTitle("Seu nome no grupo")!
            .SetMessage("É como os amigos veem este celular na lista do grupo.")!
            .SetView(moldura)!
            .SetPositiveButton("Salvar", (_, _) => { if (campo.Text?.Trim() is { Length: > 0 } n) Sessao.Apelido = n; })!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Show();
        campo.RequestFocus();
        MostrarTeclado(campo);
    }

    void ConfirmarSaida()
    {
        new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!
            .SetTitle("Sair da conta?")!
            .SetMessage("Este celular deixa de ser um dispositivo da sua conta Streemu. Para voltar, é só entrar de novo.")!
            .SetPositiveButton("Sair", (_, _) => _ = Sessao.SairDaContaAsync())!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Show();
    }
}
