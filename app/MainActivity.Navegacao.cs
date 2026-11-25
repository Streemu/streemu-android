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
        logo.SetImageResource(ArteDoTema.Para(this, Resource.Drawable.logo));
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
        toque.Click += (_, _) => { EsquecerSubtela(); _aba = Aba.Mais; MostrarAba(); AoEntrarNaAba(Aba.Mais); };
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

    /// <summary>A folha "Trocar de grupo": um item por grupo da conta, com o visto no aberto. A âncora vem da tela e não é mais usada (a folha sobe de baixo).</summary>
    void EscolherGrupo(View ancora)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        var gs = Sessao.Conta.Grupos;
        if (gs.Count == 0) return;
        var acoes = gs.Select(g => new AcaoDaFolha(Resource.Drawable.ic_grupo, g.Nome, () => Sessao.AbrirSala(g.Id), Marcada: g.Id == Sessao.GrupoId)).ToList();
        FolhaDeAcoes.Mostrar(this, "Trocar de grupo", acoes);
    }

    static readonly (Aba Aba, int Icone, string Nome)[] ItensDaNavegacao =
    {
        (Aba.Inicio, Resource.Drawable.ic_inicio, "Início"), (Aba.Grupos, Resource.Drawable.ic_grupo, "Grupos"),
        (Aba.Cinema, Resource.Drawable.ic_cinema, "Cinema"), (Aba.Arquivos, Resource.Drawable.ic_arquivos, "Arquivos"),
        (Aba.Mais, Resource.Drawable.ic_mais, "Mais"),
    };

    const string ChaveDaAba = "aba_nome";

    /// <summary>"Não lançado" só aparece nas Novidades do build de teste: no publicado, quem entrega já renomeou a seção para a versão.</summary>
#if DEBUG
    const bool MostrarNaoLancado = true;
#else
    const bool MostrarNaoLancado = false;
#endif
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
                if (_aba == aba && FecharSeAberta()) return;                   // tocar na aba de novo fecha a subtela aberta nela
                if (_aba == aba) { _telaAtual?.SmoothScrollTo(0, 0); return; }   // tocar na aba atual volta ao topo
                EsquecerSubtela();
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
        OuvirArquivos();
        foreach (var (aba, item) in _botoesNav)
        {
            bool atual = aba == _aba;
            var pilula = (FrameLayout)item.GetChildAt(0)!;
            pilula.Background = atual ? Tema.Forma(this, Tema.Selecionado, 16) : null;
            ((ImageView)pilula.GetChildAt(0)!).ImageTintList = Android.Content.Res.ColorStateList.ValueOf(atual ? Tema.DestaqueSuave : Tema.TextoApagado);
            ((TextView)item.GetChildAt(1)!).SetTextColor(atual ? Tema.CorTexto : Tema.TextoApagado);
            item.Selected = atual;
        }
        if (_telaAtual != null && _abaDaTela != _aba) Streemu.Proto.Avatares.RelogioDeAvatar.ParaTudo();   // trocou de aba: o GIF da aba anterior não anima escondido
        // a rolagem só fica na mesma tela: mesma aba e mesma subtela (abrir Novidades com Mais rolada abria Novidades no meio)
        int rolagem = _telaAtual != null && _abaDaTela == _aba && _subtelaDaTela == _subtela ? _telaAtual.ScrollY : 0;
        _conteudo.RemoveAllViews();
        _telaAtual = _aba switch
        {
            Aba.Inicio when _subtela == Subtela.Transmitir => Telas.Transmitir(this, FecharSubtela, EscolherQualidade, EscolherSom, ComecarTransmissao),
            Aba.Mais when _subtela == Subtela.Novidades => Telas.Novidades(this, FecharSubtela, Novidades.Ler(), Atualizacao.VersaoInstalada, MostrarNaoLancado),
            Aba.Inicio => Telas.Inicio(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this),
                                       AbrirTelaTransmitir, ServicoTransmissao.Parar, ConvidarParaOGrupo, EscolherGrupo,
                                       EnvioArquivos.EnvioDeArquivos.Liberado ? EnviarArquivoPara : null),
            Aba.Grupos => Telas.Grupos(this, Assistir, () => Convites.PedirCriacao(this), () => Convites.PedirConvite(this),
                                      g => Convites.Convidar(this, g), g => Convites.ConfirmarSaidaDoGrupo(this, g)),
            Aba.Cinema => Telas.Cinema(this, TenhoOArquivo, BaixarOFilme, AssistirBaixado, a => Filme.BaixarFilme.Parar(Streemu.Util.JsonSeguro.Texto(a["filme"]) ?? ""), AssistirPelaTransmissao, CriarSessao),
            Aba.Arquivos when EnvioArquivos.EnvioDeArquivos.Liberado => Telas.Arquivos(this, _estadoArquivos, AcoesArquivos(), EnvioArquivos.EnvioDeArquivos.Fluxo, AmigosOnline()),
            Aba.Arquivos => Telas.EmBreve(this, Resource.Drawable.mumu_dormindo, "Arquivos em breve", "Enviar fotos, vídeos e documentos direto para um amigo."),
            _ => Telas.Mais(this, TrocarApelido, ConfirmarSaida, () => AbrirSubtela(Subtela.Novidades), MenuDoAvatar),
        };
        _abaDaTela = _aba;
        _subtelaDaTela = _subtela;
        _conteudo.AddView(_telaAtual);
        // o botão "Enviar arquivos" fica fixo no rodapé da aba Enviar, por cima do fim da rolagem (mockup do projeto)
        if (_aba == Aba.Arquivos && EnvioArquivos.EnvioDeArquivos.Liberado && EnvioArquivos.EnvioDeArquivos.Fluxo != null && !_estadoArquivos.NoHistorico)
            _conteudo.AddView(Telas.RodapeDeArquivos(this, _estadoArquivos, AcoesArquivos()), new FrameLayout.LayoutParams(Tema.Cheio, Tema.Ajusta, Android.Views.GravityFlags.Bottom));
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

    /// <summary>"Mudar nome da conta" (aba Mais): grava pelo servidor, e a troca vale em todos os aparelhos.</summary>
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
        CaixaDeTexto.Mostrar(this, titulo, texto, inicial, ContaLeve.NomeMaximo, SalvarNome, () => _dialogoNome = false);
    }

    /// <summary>
    /// O "Salvar" da caixa de nome. Devolve null se deu certo (a caixa fecha) ou a frase de erro: com erro ou sem rede a caixa fica aberta e o texto digitado
    /// não se perde.
    /// </summary>
    async Task<string?> SalvarNome(string digitado)
    {
        var nome = digitado.Trim();
        if (nome.Length == 0) return "Escreva um nome.";
        if (nome == Sessao.Conta.Nome) return null;
        var (ok, aviso) = await Sessao.Conta.MudarNomeAsync(nome);
        if (!ok) return aviso ?? "Não deu certo. Tente de novo.";
        Toast.MakeText(this, "Seu nome agora é " + (Sessao.Conta.Nome ?? nome) + ".", ToastLength.Short)?.Show();
        return null;
    }

    void ConfirmarSaida()
    {
        CaixaDoApp.Confirmar(this, "Sair da conta?", "Este celular deixa de ser um dispositivo da sua conta Streemu. Para voltar, é só entrar de novo.",
            "Sair", () => _ = Sessao.SairDaContaAsync(), perigosa: true);
    }
}
