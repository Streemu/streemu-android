using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Transmissao;
using Streemu.Proto.Ui;
using Logcat = Android.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// A tela principal. Sem conta: a entrada (navegador + código de 6 dígitos). Com conta: cinco abas (spec 002): Início (as lives do grupo, com
/// miniatura), Grupos, Cinema e Arquivos ("em breve" até existirem) e Mais. A navegação fica embaixo no celular em pé e vira uma barra lateral em
/// tela larga (600 dp ou mais: celular deitado, dobrável aberto, tablet), onde a lista de lives também ganha colunas.
/// No build Debug, abrir com "--es codigo X" (testar-no-celular.ps1) vai direto para o player no modo de código, sem sala.
/// </summary>
[Activity(Label = NomeApp.Nome, MainLauncher = true, Exported = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
          WindowSoftInputMode = SoftInput.AdjustResize, LaunchMode = LaunchMode.SingleTask)]
// convite de grupo: streemu://grupo/<código> e https://www.streemu.com.br/g/<código> (esse com verificação de link, para abrir direto no app)
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "streemu", DataHost = "grupo")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, AutoVerify = true,
              DataScheme = "https", DataHost = "www.streemu.com.br", DataPathPrefix = "/g/")]
public partial class MainActivity : Android.App.Activity
{
    enum Aba { Inicio, Grupos, Cinema, Arquivos, Mais }

    FrameLayout _raiz = null!, _conteudo = null!;
    Aba _aba = Aba.Inicio;
    bool _redesenhoMarcado, _visivel;
    readonly List<(Aba Aba, LinearLayout Item)> _botoesNav = new();
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
        if (!Atualizacao.BloqueiaSala && (Intent?.GetStringExtra("codigos") ?? Intent?.GetStringExtra("codigo")) is { Length: > 0 } codigo)
        {
            StartActivity(new Intent(this, typeof(PlayerActivity)).PutExtra("codigos", codigo).PutExtra("limite_lives", Intent!.GetIntExtra("limite_lives", 0)).PutExtra("latencia_srt", Intent!.GetIntExtra("latencia_srt", 0)));
            Finish();
            return;
        }
#endif
        Sessao.Iniciar(this, aguardarAtualizacao: Atualizacao.DeveVerificarAoAbrir);
        // o link do convite vem só na primeira criação: girar a tela ou voltar pelo Recentes reentrega o mesmo Intent
        if (savedInstanceState == null && (Intent == null || (Intent.Flags & ActivityFlags.LaunchedFromHistory) == 0)) Convites.Receber(this, Intent);
        _aba = AbaSalva(savedInstanceState);
        if (savedInstanceState == null) ReceberCompartilhamento(Intent);   // Compartilhar → Streemu (spec 030)
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
        ReceberCompartilhamento(intent);
        if (_raiz == null) return;
        Convites.Receber(this, intent);
        if (!_mostrandoInicio && Convites.Pendente(this) != null) Montar();   // sem conta: a entrada passa a mostrar o aviso do convite
        Convites.ProcessarPendente(this);
    }

    /// <summary>Do convite aceito: a aba Ao vivo, onde o grupo novo aparece.</summary>
    internal void IrParaAoVivo()
    {
        _aba = Aba.Inicio;
        if (!_mostrandoInicio) return;
        MostrarAba();
        Sessao.PedirMiniaturas(true);
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutString(ChaveDaAba, _aba.ToString());
        outState.PutBoolean("esperando", _esperandoCodigo);
    }

    protected override void OnStart()
    {
        base.OnStart();
        if (_raiz == null) return;
        _visivel = true;
        Sessao.Mudou += MarcarRedesenho;
        ServicoTransmissao.Mudou += MarcarRedesenho;
        Atualizacao.Mudou += RedesenharAtualizacao;
        // a miniatura de cada live: o PC manda a cada ~8 s enquanto alguém pede; pedir de novo a cada 20 s mantém o pedido vivo
        _miniaturas = new Timer(_ => { if (_aba == Aba.Inicio) Sessao.PedirMiniaturas(true); }, null, 500, 20_000);
        MarcarRedesenho();
    }

    protected override void OnStop()
    {
        _visivel = false;
        Sessao.Mudou -= MarcarRedesenho;
        ServicoTransmissao.Mudou -= MarcarRedesenho;
        _miniaturas?.Dispose(); _miniaturas = null;
        Atualizacao.Mudou -= RedesenharAtualizacao;
        if (_raiz != null) Sessao.PedirMiniaturas(false);
        base.OnStop();
    }

    protected override void OnResume()
    {
        base.OnResume();
        EnvioArquivos.EnvioDeArquivos.AoVoltarParaFrente(this);   // religa o serviço de arquivos se o Android o pausou (spec 030)
        Sessao.ReconectarSala();   // voltou do segundo plano: a conexão pode ter morrido sem aviso
        Sessao.TelaVisivel = true;
        Avisos.AvisosDeSegundoPlano.IniciarSeLigado(this);   // liga o serviço dos avisos (com o app na frente o Android deixa)
        if (_raiz == null) return;
        Atualizacao.Mostrar(this);
        // voltando do navegador: o campo do código já pronto para digitar
        if (_codigo != null) { _codigo.RequestFocus(); MostrarTeclado(_codigo); }
    }

    protected override void OnPause()
    {
        Sessao.TelaVisivel = false;
        Atualizacao.Ocultar(this);
        base.OnPause();
    }

    internal void MostrarAtualizacao()
    {
        _aba = Aba.Inicio;
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
        _raiz.SetBackgroundColor(Tema.Fundo);   // o Início troca pela cor das barras
        _botoesNav.Clear();
        _codigo = null; _erroEntrada = null; _telaAtual = null;
        _atualizacaoNaEntrada = null;
        _mostrandoInicio = Sessao.Conta.TemConta;
        if (_mostrandoInicio) MontarInicio(); else MontarEntrada();
    }

    void Atualizar()
    {
        if (Sessao.Conta.TemConta != _mostrandoInicio) { Montar(); return; }
        if (_mostrandoInicio) { AtualizarBarra(); MostrarAba(); OferecerNomeSeFaltar(); }
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

    /// <summary>Saindo do app pelo voltar (não é rotação nem o Android pausando): fecha a sessão do diagnóstico.</summary>
    protected override void OnDestroy()
    {
        if (_raiz != null && IsFinishing) Telemetria.Fechar();
        Convites.Fechar();
        base.OnDestroy();
    }
}
