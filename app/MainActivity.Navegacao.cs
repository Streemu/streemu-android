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
/// O Início: a barra de cima, a navegação entre as abas e o que as abas pedem à tela (assistir, trocar o nome, sair).
/// </summary>
public partial class MainActivity
{
    // ================================================================================================ Início

    void MontarInicio()
    {
        bool larga = Tema.LarguraDp(this) >= 600;
        // as barras (a de cima, a de navegação e as do sistema por trás delas) são escuras; só o conteúdo tem o roxo do fundo
        _raiz.SetBackgroundColor(Tema.Barra);
        _conteudo = new FrameLayout(this);
        _conteudo.SetBackgroundColor(Tema.Fundo);
        var coluna = Tema.Coluna(this);
        coluna.AddView(BarraDeCima(), Tema.Lp(Tema.Cheio, Tema.Dp(this, 60)));
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
        AoEntrarNaAba(_aba);
    }

    /// <summary>
    /// O que cada aba pede ao entrar (não a cada redesenho, que acontece a cada mudança da sala): o Início, as miniaturas das lives; Grupos, o papel e
    /// o tamanho de cada grupo, que o servidor só sabe perguntando a cada sala.
    /// </summary>
    void AoEntrarNaAba(Aba aba)
    {
        Sessao.PedirMiniaturas(aba == Aba.Inicio);
        if (aba == Aba.Grupos) Sessao.PedirDetalhesDosGrupos();
    }

    /// <summary>
    /// A barra de cima (como no print do mockup): escura, o logo inteiro à esquerda, você à direita e um fio embaixo.
    /// O grupo aberto saiu daqui para a linha de cima do Início, onde o mockup o põe.
    /// </summary>
    View BarraDeCima()
    {
        var barra = Tema.Linha(this);
        barra.SetPadding(Tema.Dp(this, 16), 0, Tema.Dp(this, 8), 0);
        var logo = new ImageView(this) { ContentDescription = "Streemu" };
        logo.SetImageResource(Resource.Drawable.logo);
        logo.SetAdjustViewBounds(true);   // altura fixa, largura pela proporção do logo (800 × 161)
        logo.SetScaleType(ImageView.ScaleType.FitStart);
        barra.AddView(logo, Tema.Lp(Tema.Ajusta, Tema.Dp(this, 30)));
        barra.AddView(new View(this), Tema.Lp(0, 1, 1));
        barra.AddView(AvatarDaBarra(), Tema.Lp(Tema.Dp(this, 48), Tema.Dp(this, 48)));
        var comFio = Tema.Coluna(this);
        comFio.SetBackgroundColor(Tema.Barra);
        comFio.AddView(barra, Tema.Lp(Tema.Cheio, 0, 1));
        comFio.AddView(new View(this) { Background = new Android.Graphics.Drawables.ColorDrawable(Tema.Fio) }, Tema.Lp(Tema.Cheio, Tema.Dp(this, 1)));
        return comFio;
    }

    /// <summary>Você, no canto da barra (como no mockup): um toque abre a aba Mais, onde ficam o seu nome e a conta.</summary>
    View AvatarDaBarra()
    {
        var toque = new FrameLayout(this) { Clickable = true, ContentDescription = "Você e a conta" };
        toque.Background = Tema.Tocavel(this, Color.Transparent, 24);
        _eu = new Avatar(this);
        toque.AddView(_eu, new FrameLayout.LayoutParams(Tema.Dp(this, 36), Tema.Dp(this, 36), GravityFlags.Center));
        toque.Click += (_, _) => { _aba = Aba.Mais; MostrarAba(); AoEntrarNaAba(Aba.Mais); };
        return toque;
    }

    void AtualizarBarra()
    {
        string conta = Sessao.Conta.ContaId ?? "eu";
        _eu.Mostrar(conta, Sessao.Apelido, Sessao.Avatar(conta));
    }

    /// <summary>"Convidar" do Início: o link do grupo aberto agora (o mesmo de "Convidar" na aba Grupos).</summary>
    void ConvidarParaOGrupo()
    {
        if (Sessao.Conta.Grupos.FirstOrDefault(g => g.Id == Sessao.GrupoId) is { } grupo) Convites.Convidar(this, grupo);
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

    static readonly (Aba Aba, int Icone, string Nome)[] ItensDaNavegacao =
    {
        (Aba.Inicio, Resource.Drawable.ic_inicio, "Início"), (Aba.Grupos, Resource.Drawable.ic_grupo, "Grupos"),
        (Aba.Cinema, Resource.Drawable.ic_cinema, "Cinema"), (Aba.Arquivos, Resource.Drawable.ic_arquivos, "Arquivos"),
        (Aba.Mais, Resource.Drawable.ic_mais, "Mais"),
    };

    const string ChaveDaAba = "aba_nome";
    Avatar _eu = null!;

    /// <summary>
    /// A aba guardada ao girar a tela ou ao voltar pelo Recentes. Guardada pelo nome: o número do 0.1.6 ("aba", 2 = Conta) cairia em Cinema.
    /// Nome ausente ou desconhecido abre o Início. Ex.: <c>_aba = AbaSalva(savedInstanceState);</c>
    /// </summary>
    static Aba AbaSalva(Bundle? estado)
    {
        string? nome = estado?.GetString(ChaveDaAba);
        // Enum.TryParse aceitaria "2" como número; só nomes conhecidos valem
        return nome != null && Enum.GetNames<Aba>().Contains(nome) ? Enum.Parse<Aba>(nome) : Aba.Inicio;
    }

    View Navegacao(bool vertical)
    {
        var nav = vertical ? Tema.Coluna(this) : Tema.Linha(this);
        nav.SetBackgroundColor(Tema.Barra);
        nav.SetGravity(vertical ? GravityFlags.CenterHorizontal | GravityFlags.Top : GravityFlags.Center);
        if (vertical) nav.SetPadding(0, Tema.Dp(this, 12), 0, 0);
        foreach (var (aba, icone, nome) in ItensDaNavegacao)
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
                AoEntrarNaAba(aba);
            };
            nav.AddView(item, vertical ? Tema.Lp(Tema.Cheio, Tema.Dp(this, 64)) : Tema.Lp(0, Tema.Cheio, 1));
            _botoesNav.Add((aba, item));
        }
        if (!vertical) return nav;
        // deitado, um celular tem ~360 dp de altura útil: 5 itens de 64 dp cabem, e a rolagem segura telas ainda mais baixas (POCO X6 Pro, 2026-11-21)
        var rolagem = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
        rolagem.SetBackgroundColor(Tema.Barra);
        rolagem.AddView(nav);
        return rolagem;
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
            Aba.Inicio => Telas.Inicio(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this),
                                       Transmitir, ServicoTransmissao.Parar, ConvidarParaOGrupo, EscolherGrupo),
            Aba.Grupos => Telas.Grupos(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this),
                                      g => Convites.Convidar(this, g), g => Convites.ConfirmarSaidaDoGrupo(this, g)),
            Aba.Cinema => Telas.EmBreve(this, Resource.Drawable.mumu_pipoca, "Cinema em breve", "Assistir filmes junto com o grupo, no mesmo ritmo."),
            Aba.Arquivos => Telas.EmBreve(this, Resource.Drawable.mumu_dormindo, "Arquivos em breve", "Enviar fotos, vídeos e documentos direto para um amigo."),
            _ => Telas.Conta(this, TrocarApelido, ConfirmarSaida),
        };
        _abaDaTela = _aba;
        _conteudo.AddView(_telaAtual);
        // redesenho na mesma aba (chegou uma miniatura, alguém entrou): a rolagem fica onde estava
        if (rolagem > 0) { var t = _telaAtual; t.Post(() => t.ScrollTo(0, rolagem)); }
    }

    void Assistir(Member m)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (m.Live == null) return;
        StartActivity(new Intent(this, typeof(PlayerActivity))
            .PutExtra("conta", m.Id).PutExtra("apelido", m.Nick).PutExtra("sala", m.Live.StreamId)
            .PutExtra("jogo", m.Live.Game).PutExtra("qualidade", m.Live.Quality).PutExtra("desde", m.Live.SinceUnixMs));
    }

    /// <summary>"Mudar nome da conta" (aba Conta): grava pelo servidor, e a troca vale em todos os aparelhos.</summary>
    void TrocarApelido() => PedirNome("Mudar nome da conta", "É como os amigos veem você em todos os aparelhos.", Sessao.Apelido);

    /// <summary>Conta sem nome depois do renovar: o diálogo "Escolha seu nome", uma vez só.</summary>
    void OferecerNomeSeFaltar()
    {
        if (_dialogoNome || !Sessao.PrecisaEscolherNome) return;
        Sessao.NomeOferecido();
        PedirNome("Escolha seu nome", "É como os amigos veem você em todos os aparelhos.", Sessao.SugestaoDeNome);
    }

    bool _dialogoNome;

    void PedirNome(string titulo, string texto, string inicial)
    {
        _dialogoNome = true;
        var campo = new EditText(this) { Text = inicial };
        campo.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(ContaLeve.NomeMaximo) });
        campo.SetSelectAllOnFocus(true);
        campo.SetSingleLine(true);
        var erro = Tema.Texto(this, "", 14, Tema.Erro);
        erro.Visibility = ViewStates.Gone;
        var coluna = Tema.Coluna(this);
        coluna.SetPadding(Tema.Dp(this, 22), Tema.Dp(this, 8), Tema.Dp(this, 22), 0);
        coluna.AddView(campo);
        coluna.AddView(erro);
        var dialogo = new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!
            .SetTitle(titulo)!
            .SetMessage(texto)!
            .SetView(coluna)!
            .SetPositiveButton("Salvar", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Create()!;
        dialogo.DismissEvent += (_, _) => _dialogoNome = false;
        dialogo.Show();
        // o botão é religado à mão: com erro ou sem rede o diálogo fica aberto e o texto digitado não se perde
        var salvar = dialogo.GetButton((int)DialogButtonType.Positive)!;
        salvar.Click += async (_, _) =>
        {
            var nome = campo.Text?.Trim() ?? "";
            if (nome.Length == 0) { erro.Text = "Escreva um nome."; erro.Visibility = ViewStates.Visible; return; }
            if (nome == Sessao.Conta.Nome) { dialogo.Dismiss(); return; }
            salvar.Enabled = false;
            var (ok, aviso) = await Sessao.Conta.MudarNomeAsync(nome);
            if (ok) { dialogo.Dismiss(); Toast.MakeText(this, "Seu nome agora é " + (Sessao.Conta.Nome ?? nome) + ".", ToastLength.Short)?.Show(); return; }
            salvar.Enabled = true;
            erro.Text = aviso ?? "Não deu certo. Tente de novo.";
            erro.Visibility = ViewStates.Visible;
        };
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
