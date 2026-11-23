using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using Streemu.Arquivos;
using Streemu.Proto.EnvioArquivos;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Arquivos (spec 030), no desenho do mockup do projeto: título e subtítulo, as abas Enviar e Histórico, "Para quem" (um seletor com o amigo
/// escolhido), "Arquivos" (o cartão tracejado com o Mumu na gaveta), a linha de onde ficam os recebidos e "Última transferência". O botão
/// "Enviar arquivos" fica fixo no rodapé (<see cref="RodapeDeArquivos"/>), acima da barra de navegação. Os pedidos que chegam e os que andam
/// aparecem no alto, porque são o que a pessoa precisa ver primeiro. Os textos de cada estado: <see cref="TextoDeArquivos"/>.
/// Ex.: <c>Telas.Arquivos(this, _estadoArquivos, AcoesDeArquivos, fluxo, amigos)</c>, redesenhada a cada mudança do fluxo.
/// </summary>
static partial class Telas
{
    /// <summary>As partes de um cartão de pedido que mudam a cada atualização de progresso (título, linha de texto e barra), para atualizá-las no lugar.</summary>
    sealed record ViewsDoPedido(TextView Titulo, TextView Linha, ProgressBar? Barra);

    static readonly Dictionary<string, ViewsDoPedido> _vivos = new();

    /// <summary>Como estava a "forma" da tela quando foi desenhada: quais pedidos e em que fase, e o início do histórico. Se muda, é preciso refazer a tela.</summary>
    static string _assinaturaDesenhada = "";

    /// <summary>
    /// A "forma" da aba: os pedidos visíveis com a fase de cada um e o último item do histórico. Progresso e velocidade ficam de fora de propósito:
    /// eles mudam várias vezes por segundo e são atualizados no lugar por <see cref="AtualizarProgresso"/>. Refazer a tela inteira a cada
    /// atualização fazia a aba piscar durante todo o download (achado em teste de campo, 2026-11-23, 500 MB do PC para o celular).
    /// </summary>
    static string Assinatura(FluxoDeArquivos f)
    {
        var pedidos = string.Join("|", f.Pedidos.Where(PedidoVisivel).Select(p => $"{p.Pedido}:{p.Fase}"));
        var primeiro = f.Historico.Itens.FirstOrDefault();
        return $"{pedidos}#{f.Historico.Itens.Count}#{primeiro?.Pedido}:{primeiro?.Resultado}";
    }

    /// <summary>A tela precisa ser refeita? (apareceu ou acabou um pedido, mudou a fase ou o histórico). Se não, basta <see cref="AtualizarProgresso"/>.</summary>
    public static bool MudouDeForma(FluxoDeArquivos f) => Assinatura(f) != _assinaturaDesenhada;

    /// <summary>Atualiza, no lugar e sem refazer a tela, o texto e a barra dos pedidos que estão andando. Chamar na thread da interface.</summary>
    public static void AtualizarProgresso(FluxoDeArquivos f)
    {
        foreach (var p in f.Pedidos)
        {
            if (!_vivos.TryGetValue(p.Pedido, out var v)) continue;
            v.Titulo.Text = TextoDeArquivos.TituloDoPedido(p);
            v.Linha.Text = TextoDeArquivos.LinhaDoPedido(p);
            if (v.Barra != null) v.Barra.Progress = p.Progresso?.Pct ?? 0;
        }
    }

    public static ScrollView Arquivos(Context c, EstadoDeArquivos st, AcoesDeArquivos a, FluxoDeArquivos? fluxo, IReadOnlyList<AmigoDeArquivos> amigos)
    {
        _vivos.Clear();
        _assinaturaDesenhada = fluxo == null ? "" : Assinatura(fluxo);
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        col.AddView(TituloDaTela(c, "Enviar arquivo"));
        col.AddView(Tema.Texto(c, "Compartilhe algo direto com um amigo.", 15, Tema.TextoApagado));
        col.AddView(AbasDeArquivos(c, st, a.Redesenhar), Topo(c, 16));
        if (fluxo == null) return Rolagem(c, col);
        if (st.NoHistorico) MontarHistorico(c, col, fluxo, st, a);
        else MontarEnviar(c, col, fluxo, st, amigos, a);
        return Rolagem(c, col);
    }

    /// <summary>
    /// O botão "Enviar arquivos" fixo no rodapé da aba Enviar, com um fio em cima separando-o da barra de navegação. Apagado até haver amigo e arquivo.
    /// Quem monta a tela o coloca por cima do fim do conteúdo (a rolagem deixa espaço para ele: <see cref="AlturaDoRodape"/>).
    /// </summary>
    public static View RodapeDeArquivos(Context c, EstadoDeArquivos st, AcoesDeArquivos a)
    {
        var k = Tema.Coluna(c);
        k.SetBackgroundColor(Tema.Fundo);
        k.AddView(new View(c) { Background = new ColorDrawable(Tema.Fio) }, Tema.Lp(Tema.Cheio, Tema.Dp(c, 1)));
        bool pronto = st.AmigoId != null && st.Escolhidos.Count > 0;
        var botao = Tema.Linha(c, GravityFlags.Center);
        botao.Background = Tema.Tocavel(c, pronto ? Tema.Destaque : Tema.Selecionado, 14);
        botao.Clickable = pronto;
        botao.ContentDescription = pronto ? "Enviar os arquivos escolhidos" : "Enviar arquivos: escolha um amigo e pelo menos um arquivo";
        var cor = pronto ? Color.White : Tema.TextoApagado;
        botao.AddView(Tema.Icone(c, Resource.Drawable.ic_aviao, cor, 22), Tema.Lp(Tema.Dp(c, 22), Tema.Dp(c, 22)));
        var texto = Tema.Texto(c, "Enviar arquivos", 16, cor, negrito: true);
        texto.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
        botao.AddView(texto);
        botao.Click += (_, _) => { if (pronto) a.Enviar(); };
        var lp = Tema.Lp(Tema.Cheio, Tema.Dp(c, 56));
        lp.SetMargins(Margem(c), Tema.Dp(c, 12), Margem(c), Tema.Dp(c, 12));
        k.AddView(botao, lp);
        return k;
    }

    /// <summary>Quanto o conteúdo da aba Enviar deixa livre no fim, para o rodapé fixo não cobrir o último cartão.</summary>
    public static int AlturaDoRodape(Context c) => Tema.Dp(c, 96);

    // ------------------------------------------------------------------ peças comuns

    /// <summary>Largura cheia e só a margem de cima: o <c>Lp</c> com margem põe a mesma nos quatro lados e estreita o botão.</summary>
    static LinearLayout.LayoutParams Topo(Context c, int dp)
    {
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, dp);
        return lp;
    }

    /// <summary>Peso na linha, altura fixa e só a margem da esquerda (o espaço entre dois botões lado a lado).</summary>
    static LinearLayout.LayoutParams ComEsquerda(Context c, float peso, int dp, int alturaDp = 40)
    {
        var lp = Tema.Lp(0, Tema.Dp(c, alturaDp), peso);
        lp.LeftMargin = Tema.Dp(c, dp);
        return lp;
    }

    /// <summary>O título de uma seção ("Para quem", "Arquivos") à esquerda e, à direita, uma dica miúda (ou uma seta, se a seção é um toque).</summary>
    static View CabecalhoDeArquivos(Context c, string titulo, string? dica, Action? tocar = null, bool seta = false)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 24), 0, Tema.Dp(c, 12));
        linha.AddView(Tema.Texto(c, titulo, 20, negrito: true), Tema.Lp(0, Tema.Ajusta, 1));
        if (dica != null) linha.AddView(Tema.Texto(c, dica, 13, Tema.TextoApagado));
        if (seta) linha.AddView(Tema.Icone(c, Resource.Drawable.ic_seta_direita, Tema.TextoApagado, 24), Tema.Lp(Tema.Dp(c, 24), Tema.Dp(c, 24)));
        if (tocar != null) { linha.Clickable = true; linha.Click += (_, _) => tocar(); }
        return linha;
    }

    static View AbasDeArquivos(Context c, EstadoDeArquivos st, Action redesenhar)
    {
        var caixa = Tema.Linha(c);
        caixa.Background = Tema.Forma(c, Tema.Cartao, 18);
        caixa.SetPadding(Tema.Dp(c, 6), Tema.Dp(c, 6), Tema.Dp(c, 6), Tema.Dp(c, 6));
        caixa.AddView(Aba(c, "Enviar", !st.NoHistorico, () => { st.NoHistorico = false; redesenhar(); }), ComEsquerda(c, 1, 0, 48));
        caixa.AddView(Aba(c, "Histórico", st.NoHistorico, () => { st.NoHistorico = true; redesenhar(); }), ComEsquerda(c, 1, 8, 48));
        return caixa;
    }

    /// <summary>Uma aba é um botão de cantos redondos com borda: cheio de roxo quando é a atual, cinza quando não.</summary>
    static TextView Aba(Context c, string nome, bool ativa, Action tocar)
    {
        var t = Tema.Texto(c, nome, 16, ativa ? Color.White : Tema.TextoApagado, negrito: true);
        t.Gravity = GravityFlags.Center;
        var fundo = Tema.Forma(c, ativa ? Tema.Destaque : Tema.Neutro, 14, ativa ? Tema.DestaqueSuave : Tema.Borda);
        t.Background = new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.Argb(60, 255, 255, 255)), fundo, Tema.Forma(c, Color.White, 14));
        t.Clickable = true;
        t.Click += (_, _) => tocar();
        return t;
    }

    // ------------------------------------------------------------------ Enviar

    static void MontarEnviar(Context c, LinearLayout col, FluxoDeArquivos fluxo, EstadoDeArquivos st, IReadOnlyList<AmigoDeArquivos> amigos, AcoesDeArquivos a)
    {
        foreach (var p in fluxo.Pedidos.Where(PedidoVisivel)) col.AddView(CartaoDoPedido(c, p, a), Topo(c, 12));
        col.AddView(CabecalhoDeArquivos(c, "Para quem", st.AmigoId == null ? "Escolha um amigo online" : null));
        col.AddView(SeletorDeAmigo(c, st, amigos, a.Redesenhar));
        col.AddView(CabecalhoDeArquivos(c, "Arquivos", "Vários arquivos"));
        col.AddView(CartaoDeArquivos(c, st, a));
        col.AddView(OndeFicam(c, fluxo), Topo(c, 16));
        col.AddView(CabecalhoDeArquivos(c, "Última transferência", null, () => { st.NoHistorico = true; a.Redesenhar(); }, seta: true));
        if (fluxo.Historico.Itens.FirstOrDefault() is { } ultimo) col.AddView(LinhaDoHistorico(c, ultimo, a, comAcoes: false));
        else col.AddView(Tema.Texto(c, "Nada enviado ou recebido ainda.", 14, Tema.TextoApagado));
        col.AddView(MaisOpcoes(c, fluxo, st, a));
        col.AddView(new View(c), Tema.Lp(Tema.Cheio, AlturaDoRodape(c)));
    }

    /// <summary>Pedido que a pessoa precisa ver: chegando, esperando resposta ou andando. O que já acabou vai para o histórico.</summary>
    static bool PedidoVisivel(PedidoEmCurso p) => !p.Terminado && p.Fase != FaseArquivo.AguardandoDeNovo && p.Fase != FaseArquivo.PedidoDeNovo;

    /// <summary>
    /// "Para quem" como no mockup: uma linha só, com o amigo escolhido (a foto com o ponto verde, "PRONTO PARA RECEBER" e o nome) e uma seta; tocar abre
    /// a lista dos amigos online para trocar. Sem ninguém escolhido pede "Escolha um amigo"; sem ninguém online, avisa.
    /// </summary>
    static View SeletorDeAmigo(Context c, EstadoDeArquivos st, IReadOnlyList<AmigoDeArquivos> amigos, Action redesenhar)
    {
        if (st.AmigoId != null && amigos.All(f => f.Id != st.AmigoId)) st.AmigoId = null;
        var escolhido = amigos.FirstOrDefault(f => f.Id == st.AmigoId);
        var linha = Tema.Linha(c);
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 16, Tema.Borda);
        linha.SetPadding(Tema.Dp(c, 14), Tema.Dp(c, 12), Tema.Dp(c, 14), Tema.Dp(c, 12));
        linha.SetMinimumHeight(Tema.Dp(c, 76));
        linha.AddView(FotoDoSeletor(c, escolhido), Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        var textos = Tema.Coluna(c);
        textos.SetGravity(GravityFlags.Center);
        string estado = escolhido != null ? "PRONTO PARA RECEBER" : amigos.Count == 0 ? "NINGUÉM ONLINE" : $"{amigos.Count} ONLINE AGORA";
        var e = Tema.Texto(c, estado, 12, Tema.TextoApagado);
        e.Gravity = GravityFlags.Center;
        textos.AddView(e);
        var n = Tema.Texto(c, escolhido?.Nick ?? (amigos.Count == 0 ? "Nenhum amigo online" : "Escolha um amigo"), 17, negrito: true);
        n.Gravity = GravityFlags.Center; n.SetMaxLines(1); n.Ellipsize = TextUtils.TruncateAt.End;
        textos.AddView(n);
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        linha.AddView(Tema.Icone(c, Resource.Drawable.ic_seta_baixo, Tema.TextoApagado, 24), Tema.Lp(Tema.Dp(c, 24), Tema.Dp(c, 24)));
        linha.Clickable = amigos.Count > 0;
        linha.ContentDescription = escolhido != null ? $"Para {escolhido.Nick}. Toque para trocar de amigo" : "Escolher o amigo que vai receber";
        linha.Click += (_, _) => AbrirListaDeAmigos(c, linha, st, amigos, redesenhar);
        return linha;
    }

    /// <summary>Abre a lista flutuante de amigos (<see cref="ListaDeAmigos"/>), com a identidade do Streemu e uma entrada suave.</summary>
    static void AbrirListaDeAmigos(Context c, View ancora, EstadoDeArquivos st, IReadOnlyList<AmigoDeArquivos> amigos, Action redesenhar) =>
        ListaDeAmigos.Mostrar(c, ancora, amigos, st.AmigoId, a => { st.AmigoId = a.Id; redesenhar(); });

    /// <summary>A foto do amigo com o ponto verde de online no canto; sem amigo escolhido, um círculo neutro com o ícone de pessoas.</summary>
    static View FotoDoSeletor(Context c, AmigoDeArquivos? amigo)
    {
        var quadro = new FrameLayout(c);
        if (amigo != null)
        {
            var av = new Avatar(c);
            av.Mostrar(amigo.Id, amigo.Nick, Sessao.Avatar(amigo.Id));
            quadro.AddView(av, new FrameLayout.LayoutParams(Tema.Dp(c, 52), Tema.Dp(c, 52)));
            quadro.AddView(new View(c) { Background = Tema.Forma(c, Tema.Ok, 8, Tema.Cartao) },
                new FrameLayout.LayoutParams(Tema.Dp(c, 16), Tema.Dp(c, 16), GravityFlags.Bottom | GravityFlags.Right));
            return quadro;
        }
        var vazio = new FrameLayout(c) { Background = Tema.Forma(c, Tema.Selecionado, 26) };
        vazio.AddView(Tema.Icone(c, Resource.Drawable.ic_grupo, Tema.DestaqueSuave, 26), new FrameLayout.LayoutParams(Tema.Dp(c, 26), Tema.Dp(c, 26), GravityFlags.Center));
        quadro.AddView(vazio, new FrameLayout.LayoutParams(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        return quadro;
    }

    /// <summary>
    /// "Arquivos": o cartão de borda tracejada do mockup. Vazio: "Escolha o que enviar", a dica, o botão e o Mumu dormindo na gaveta. Com arquivos
    /// escolhidos: quantos e quanto, a lista com um X em cada, e o botão vira "Selecionar mais".
    /// </summary>
    static View CartaoDeArquivos(Context c, EstadoDeArquivos st, AcoesDeArquivos a)
    {
        var k = Tema.Coluna(c);
        k.Background = Tracejado(c);
        k.SetPadding(Tema.Dp(c, 20), Tema.Dp(c, 20), Tema.Dp(c, 20), Tema.Dp(c, 20));
        var topo = Tema.Linha(c);
        var esquerda = Tema.Coluna(c);
        bool vazio = st.Escolhidos.Count == 0;
        esquerda.AddView(Tema.Texto(c, vazio ? "Escolha o que enviar" : $"{st.Escolhidos.Count} escolhido(s) · {NotificacaoArquivos.Tamanho(st.TotalEscolhido)}", 18, negrito: true));
        esquerda.AddView(Tema.Texto(c, "Fotos, vídeos ou documentos.", 14, Tema.TextoApagado), Topo(c, 4));
        esquerda.AddView(BotaoSelecionar(c, vazio ? "Selecionar arquivos" : "Selecionar mais", a.Selecionar));
        topo.AddView(esquerda, Tema.Lp(0, Tema.Ajusta, 1));
        if (vazio) topo.AddView(MumuNaGaveta(c));
        k.AddView(topo);
        foreach (var x in st.Escolhidos) k.AddView(LinhaDeEscolhido(c, x, a.TirarEscolhido), Topo(c, 6));
        return k;
    }

    /// <summary>A borda tracejada do cartão: cantos de 18 dp, traço de 6 dp e vão de 5 dp, na cor suave do destaque do tema.</summary>
    static Drawable Tracejado(Context c)
    {
        var g = new GradientDrawable();
        g.SetColor(Tema.Cartao);
        g.SetCornerRadius(Tema.Dp(c, 18));
        g.SetStroke(Tema.Dp(c, 1.5f), Tema.DestaqueSuave, Tema.Dp(c, 6), Tema.Dp(c, 5));
        return g;
    }

    static View BotaoSelecionar(Context c, string texto, Action tocar)
    {
        var b = Tema.Linha(c, GravityFlags.Center);
        b.Background = Tema.Tocavel(c, Tema.Destaque, 14);
        b.SetPadding(Tema.Dp(c, 14), 0, Tema.Dp(c, 14), 0);
        b.SetMinimumHeight(Tema.Dp(c, 52));
        b.Clickable = true;
        b.ContentDescription = "Escolher os arquivos para enviar, pelo seletor do sistema";
        b.AddView(Tema.Icone(c, Resource.Drawable.ic_pasta_enviar, Color.White, 22), Tema.Lp(Tema.Dp(c, 22), Tema.Dp(c, 22)));
        var t = Tema.Texto(c, texto, 15, Color.White, negrito: true);
        t.SetPadding(Tema.Dp(c, 8), 0, 0, 0);
        t.SetMaxLines(1);   // no mockup o texto cabe numa linha só, ao lado do gato
        b.AddView(t);
        b.Click += (_, _) => tocar();
        var lp = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, 16);
        b.LayoutParameters = lp;
        return b;
    }

    /// <summary>O Mumu dormindo na gaveta (a arte <c>gato-arquivo</c> do app do PC), ao lado do texto do cartão.</summary>
    static View MumuNaGaveta(Context c)
    {
        var img = new ImageView(c) { ContentDescription = "O Mumu dormindo numa gaveta de arquivos" };
        img.SetImageResource(Resource.Drawable.gato_arquivo);
        img.SetScaleType(ImageView.ScaleType.FitCenter);
        img.LayoutParameters = Tema.Lp(Tema.Dp(c, 104), Tema.Dp(c, 104));
        return img;
    }

    static View LinhaDeEscolhido(Context c, ArquivoEscolhido x, Action<ArquivoEscolhido> tirar)
    {
        var linha = Tema.Linha(c);
        var nome = Tema.Texto(c, $"{x.Nome} · {NotificacaoArquivos.Tamanho(x.Tamanho)}", 14, Tema.TextoApagado);
        nome.SetMaxLines(1); nome.Ellipsize = TextUtils.TruncateAt.Middle;
        linha.AddView(nome, Tema.Lp(0, Tema.Ajusta, 1));
        var tirarB = Tema.Icone(c, Resource.Drawable.ic_fechar, Tema.TextoApagado, 20);
        tirarB.SetPadding(Tema.Dp(c, 8), Tema.Dp(c, 8), Tema.Dp(c, 8), Tema.Dp(c, 8));
        tirarB.Clickable = true;
        tirarB.ContentDescription = $"Tirar {x.Nome}";
        tirarB.Click += (_, _) => tirar(x);
        linha.AddView(tirarB, Tema.Lp(Tema.Dp(c, 36), Tema.Dp(c, 36)));
        return linha;
    }

    /// <summary>"Os recebidos ficam em <b>Downloads/Streemu</b>", com o ícone de pasta com seta para baixo.</summary>
    static View OndeFicam(Context c, FluxoDeArquivos fluxo)
    {
        string onde = TextoDeArquivos.OndeFicam(c, fluxo);
        var linha = Tema.Linha(c);
        linha.AddView(Tema.Icone(c, Resource.Drawable.ic_pasta_baixar, Tema.TextoApagado, 24), Tema.Lp(Tema.Dp(c, 24), Tema.Dp(c, 24)));
        var t = Tema.Texto(c, "", 14, Tema.TextoApagado);
        var frase = new SpannableString($"Os recebidos ficam em {onde}");
        int ini = frase.Length() - onde.Length;
        frase.SetSpan(new StyleSpan(TypefaceStyle.Bold), ini, frase.Length(), SpanTypes.ExclusiveExclusive);
        frase.SetSpan(new ForegroundColorSpan(Tema.CorTexto), ini, frase.Length(), SpanTypes.ExclusiveExclusive);
        t.SetText(frase, TextView.BufferType.Spannable);
        t.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
        linha.AddView(t, Tema.Lp(0, Tema.Ajusta, 1));
        return linha;
    }

    /// <summary>
    /// "Mais opções", fechado e discreto no fim: trocar a pasta de recebidos (o Android não deixa usar a pasta Download inteira: é preciso escolher ou criar
    /// uma dentro dela), voltar para Downloads/Streemu e voltar a perguntar antes de gastar dados móveis.
    /// </summary>
    static View MaisOpcoes(Context c, FluxoDeArquivos fluxo, EstadoDeArquivos st, AcoesDeArquivos a)
    {
        var k = Tema.Coluna(c);
        var abrir = Tema.Texto(c, st.MaisOpcoes ? "Menos opções" : "Mais opções", 14, Tema.DestaqueSuave, negrito: true);
        abrir.SetPadding(0, Tema.Dp(c, 20), 0, Tema.Dp(c, 8));
        abrir.Clickable = true;
        abrir.Click += (_, _) => { st.MaisOpcoes = !st.MaisOpcoes; a.Redesenhar(); };
        k.AddView(abrir);
        if (!st.MaisOpcoes) return k;
        k.AddView(BotaoDoCartao(c, "Trocar a pasta de recebidos", false, a.TrocarPasta), Topo(c, 4));
        if (Recebidos.EhPastaEscolhida(fluxo.Historico.PastaRecebidos)) k.AddView(BotaoDoCartao(c, "Usar Downloads/Streemu de novo", false, a.VoltarParaPastaPadrao), Topo(c, 8));
        if (RedeMovel.NaoPerguntar(c)) k.AddView(BotaoDoCartao(c, "Voltar a perguntar antes de gastar dados móveis", false, a.VoltarAPerguntarDados), Topo(c, 8));
        return k;
    }

    // ------------------------------------------------------------------ um pedido (chegando, esperando ou andando)

    static View CartaoDoPedido(Context c, PedidoEmCurso p, AcoesDeArquivos a)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16, p.Fase is FaseArquivo.Esperando && p.Sentido == Sentido.Recebido ? Tema.Destaque : Tema.Borda);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 16));
        var titulo = Tema.Texto(c, TextoDeArquivos.TituloDoPedido(p), 16, negrito: true);
        var linha = Tema.Texto(c, TextoDeArquivos.LinhaDoPedido(p), 13, Tema.TextoApagado);
        k.AddView(titulo);
        k.AddView(linha, Topo(c, 4));
        ProgressBar? barra = null;
        if (p.Fase == FaseArquivo.Transferindo) { barra = Progresso(c, p.Progresso?.Pct ?? 0); k.AddView(barra, AlturaComTopo(c, 8, 10)); }
        _vivos[p.Pedido] = new ViewsDoPedido(titulo, linha, barra);
        foreach (var (texto, principal, acao) in BotoesDoPedido(p, a)) k.AddView(BotaoDoCartao(c, texto, principal, acao), Topo(c, 8));
        return k;
    }

    static LinearLayout.LayoutParams AlturaComTopo(Context c, int alturaDp, int topoDp)
    {
        var lp = Tema.Lp(Tema.Cheio, Tema.Dp(c, alturaDp));
        lp.TopMargin = Tema.Dp(c, topoDp);
        return lp;
    }

    static ProgressBar Progresso(Context c, int pct)
    {
        var barra = new ProgressBar(c, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Progress = pct };
        barra.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Destaque);
        return barra;
    }

    static TextView BotaoDoCartao(Context c, string texto, bool principal, Action acao)
    {
        var b = Tema.Botao(c, texto, principal);
        b.Click += (_, _) => acao();
        return b;
    }

    /// <summary>Os botões que cada fase do pedido oferece: aceitar/recusar um pedido que chegou, confirmar relay ou dados, ou cancelar o que anda.</summary>
    static IEnumerable<(string Texto, bool Principal, Action Acao)> BotoesDoPedido(PedidoEmCurso p, AcoesDeArquivos a)
    {
        string id = p.Pedido;
        if (p.Sentido == Sentido.Recebido && p.Fase == FaseArquivo.Esperando) { yield return ("Aceitar", true, () => a.Aceitar(id)); yield return ("Recusar", false, () => a.Recusar(id)); yield break; }
        if (p.Fase == FaseArquivo.ConfirmandoRelay) { yield return ("Receber mesmo assim", true, () => a.ResponderRelay(id, true)); yield return ("Agora não", false, () => a.ResponderRelay(id, false)); yield break; }
        if (p.Fase == FaseArquivo.ConfirmandoDados) { yield return ("Baixar", true, () => a.ResponderDados(id, true)); yield return ("Agora não", false, () => a.ResponderDados(id, false)); yield break; }
        yield return ("Cancelar", false, () => a.Cancelar(id));
    }

    // ------------------------------------------------------------------ Histórico

    static void MontarHistorico(Context c, LinearLayout col, FluxoDeArquivos fluxo, EstadoDeArquivos st, AcoesDeArquivos a)
    {
        var filtro = Tema.Linha(c);
        filtro.AddView(Filtro(c, "Recebidos", st.SoRecebidos, () => { st.SoRecebidos = true; a.Redesenhar(); }), ComEsquerda(c, 1, 0));
        filtro.AddView(Filtro(c, "Enviados", !st.SoRecebidos, () => { st.SoRecebidos = false; a.Redesenhar(); }), ComEsquerda(c, 1, 8));
        col.AddView(filtro, Topo(c, 16));
        var itens = fluxo.Historico.Itens.Where(i => (i.Sentido == Sentido.Recebido) == st.SoRecebidos).ToList();
        if (itens.Count == 0) { col.AddView(Vazio(c, Resource.Drawable.mumu_dormindo, "Nada por aqui ainda", st.SoRecebidos ? "Os arquivos que você receber aparecem aqui." : "Os arquivos que você enviar aparecem aqui.")); return; }
        col.AddView(CabecalhoDeArquivos(c, st.SoRecebidos ? "Recebidos" : "Enviados", itens.Count.ToString()));
        foreach (var i in itens) col.AddView(LinhaDoHistorico(c, i, a, comAcoes: true), Topo(c, 8));
        var limpar = Tema.Botao(c, "Limpar histórico", principal: false);
        limpar.Click += (_, _) => a.LimparHistorico();
        col.AddView(limpar, Topo(c, 16));
    }

    static TextView Filtro(Context c, string nome, bool ativo, Action tocar)
    {
        var t = Tema.Texto(c, nome, 14, ativo ? Color.White : Tema.TextoApagado, negrito: true);
        t.Gravity = GravityFlags.Center;
        t.Background = Tema.Tocavel(c, ativo ? Tema.Selecionado : Tema.Cartao, 20, ativo ? Tema.Destaque : null);
        t.Clickable = true;
        t.Click += (_, _) => tocar();
        return t;
    }

    /// <summary>
    /// Um item do histórico (e a "Última transferência"): um quadrado com o ícone do tipo de arquivo, o nome, "De Bia · 1,6 MB" e o selo do
    /// resultado à direita. Com <paramref name="comAcoes"/>, os botões de baixo (Abrir, Mostrar na pasta, Pedir de novo).
    /// </summary>
    static View LinhaDoHistorico(Context c, ItemHistorico i, AcoesDeArquivos a, bool comAcoes)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16);
        k.SetPadding(Tema.Dp(c, 14), Tema.Dp(c, 12), Tema.Dp(c, 14), Tema.Dp(c, 12));
        var topo = Tema.Linha(c);
        topo.AddView(IconeDoArquivo(c, i.Nome), Tema.Lp(Tema.Dp(c, 48), Tema.Dp(c, 48)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, Tema.Dp(c, 8), 0);
        var nome = Tema.Texto(c, i.Nome, 15, negrito: true);
        nome.SetMaxLines(1); nome.Ellipsize = TextUtils.TruncateAt.Middle;
        textos.AddView(nome);
        textos.AddView(Tema.Texto(c, TextoDeArquivos.LinhaDoItem(i), 13, Tema.TextoApagado), Topo(c, 2));
        topo.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        topo.AddView(Selo(c, TextoDeArquivos.Resultado(i.Resultado), TextoDeArquivos.CorDoTexto(i.Resultado)));
        k.AddView(topo, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        if (comAcoes && i.Sentido == Sentido.Recebido) k.AddView(AcoesDoItem(c, i, a), Topo(c, 12));
        return k;
    }

    /// <summary>O quadrado roxo do mockup com o ícone do tipo do arquivo (imagem ou arquivo qualquer).</summary>
    static View IconeDoArquivo(Context c, string nome)
    {
        var quadro = new FrameLayout(c) { Background = Tema.Forma(c, Tema.Selecionado, 12) };
        int icone = TextoDeArquivos.EhImagem(nome) ? Resource.Drawable.ic_imagem : Resource.Drawable.ic_arquivo_generico;
        quadro.AddView(Tema.Icone(c, icone, Tema.DestaqueSuave, 24), new FrameLayout.LayoutParams(Tema.Dp(c, 24), Tema.Dp(c, 24), GravityFlags.Center));
        return quadro;
    }

    /// <summary>O selo de resultado: o texto na cor do estado sobre um fundo da mesma cor, bem transparente (âmbar para "Falhou", verde para "Conferido").</summary>
    static TextView Selo(Context c, string texto, Color cor)
    {
        var s = Tema.Texto(c, texto, 13, cor, negrito: true);
        s.SetPadding(Tema.Dp(c, 12), Tema.Dp(c, 6), Tema.Dp(c, 12), Tema.Dp(c, 6));
        s.Background = Tema.Forma(c, Color.Argb(48, cor.R, cor.G, cor.B), 14);
        return s;
    }

    /// <summary>Abrir e Mostrar na pasta só para o que chegou conferido; "Pedir de novo" para qualquer recebido (o que parou continua de onde parou).</summary>
    static View AcoesDoItem(Context c, ItemHistorico i, AcoesDeArquivos a)
    {
        var linha = Tema.Linha(c);
        bool sozinho = i.Resultado != Resultado.Conferido;   // sem outro botão à esquerda, ocupa a linha toda
        if (!sozinho)
        {
            linha.AddView(Mini(c, "Abrir", true, () => a.Abrir(i)), ComEsquerda(c, 1, 0));
            linha.AddView(Mini(c, "Mostrar na pasta", false, () => a.MostrarNaPasta(i)), ComEsquerda(c, 1.4f, 8));
        }
        linha.AddView(Mini(c, "Pedir de novo", false, () => a.PedirDeNovo(i)), ComEsquerda(c, sozinho ? 1 : 1.2f, sozinho ? 0 : 8));
        return linha;
    }

    static TextView Mini(Context c, string t, bool principal, Action tocar)
    {
        var b = Tema.Texto(c, t, 13, principal ? Color.White : Tema.CorTexto, negrito: true);
        b.Gravity = GravityFlags.Center;
        b.SetMaxLines(1);
        b.Background = Tema.Tocavel(c, principal ? Tema.Destaque : Tema.Neutro, 12);
        b.Clickable = true;
        b.Click += (_, _) => tocar();
        return b;
    }
}
