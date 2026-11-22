using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Transmissao;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Início (spec 002): "Sua sala", quem está ao vivo no grupo, os amigos online e o cartão "Transmitir a tela".
/// </summary>
static partial class Telas
{
    // ================================================================================================ Início

    /// <summary>Monta o Início inteiro. Ex.: <c>Telas.Inicio(this, Assistir, criar, entrar, Transmitir, ServicoTransmissao.Parar, ConvidarParaOGrupo, EscolherGrupo)</c></summary>
    public static ScrollView Inicio(Context c, Action<Member> assistir, Action criarGrupo, Action entrarComConvite, Action transmitir, Action parar, Action convidar, Action<View> escolherGrupo)
    {
        var sala = Sessao.Sala;
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        if (Atualizacao.Disponivel is { } nova) col.AddView(AvisoDaAtualizacao(c, nova));
        if (Atualizacao.BloqueiaSala) return Rolagem(c, col);
        if (!Telemetria.Respondido) col.AddView(AvisoDoDiagnostico(c));
        if (Sessao.Conta.Grupos.Count > 0) col.AddView(LinhaDoGrupo(c, sala, escolherGrupo));
        col.AddView(TituloDaTela(c, "Sua sala"));
        if (sala is not { Connected: true } && sala?.Members.Count is null or 0)
        {
            col.AddView(EsperandoSala(c, sala, criarGrupo, entrarComConvite));
            return Rolagem(c, col);
        }
        AdicionarLives(c, col, sala!, assistir);
        AdicionarAmigosOnline(c, col, sala!, assistir, convidar);
        col.AddView(CartaoDeTransmissao(c, sala, transmitir, parar));
        return Rolagem(c, col);
    }

    /// <summary>
    /// Em cima do "Sua sala", como no mockup: à direita, o grupo aberto (toque para trocar); à esquerda, "Conectando" só enquanto a sala não está
    /// pronta. É o lugar do "PC conectado" do mockup, que não existe no celular (spec 002).
    /// </summary>
    static View LinhaDoGrupo(Context c, GroupRoom? sala, Action<View> escolherGrupo)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 14), 0, 0);
        if (sala is { Connected: false }) linha.AddView(SeloConectando(c));
        linha.AddView(new View(c), Tema.Lp(0, 1, 1));
        linha.AddView(SeletorDeGrupo(c, escolherGrupo));
        return linha;
    }

    static View SeletorDeGrupo(Context c, Action<View> escolherGrupo)
    {
        var s = Tema.Linha(c);
        s.SetPadding(Tema.Dp(c, 12), 0, Tema.Dp(c, 8), 0);
        s.SetMinimumHeight(Tema.Dp(c, 44));
        s.Background = Tema.Tocavel(c, Tema.Cartao, 12, Tema.Borda);
        s.Clickable = true;
        s.ContentDescription = "Trocar de grupo";
        s.AddView(Tema.Icone(c, Resource.Drawable.ic_grupo, Tema.DestaqueSuave, 20));
        var nome = Tema.Texto(c, Sessao.NomeDoGrupo is { Length: > 0 } n ? n : "Sem grupo", 15, negrito: true);
        nome.SetMaxLines(1);
        nome.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        nome.SetMaxWidth(Tema.Dp(c, 200));
        nome.SetPadding(Tema.Dp(c, 8), 0, Tema.Dp(c, 4), 0);
        s.AddView(nome);
        s.AddView(Tema.Icone(c, Resource.Drawable.ic_abrir, Tema.TextoApagado, 20));
        s.Click += (_, _) => escolherGrupo(s);
        return s;
    }

    static View SeloConectando(Context c)
    {
        var s = Tema.Linha(c);
        s.SetPadding(Tema.Dp(c, 10), Tema.Dp(c, 6), Tema.Dp(c, 12), Tema.Dp(c, 6));
        s.Background = Tema.Forma(c, Tema.Cartao, 16, Tema.Aviso);
        s.AddView(new View(c) { Background = Tema.Forma(c, Tema.Aviso, 4) }, Tema.Lp(Tema.Dp(c, 8), Tema.Dp(c, 8)));
        var t = Tema.Texto(c, "Conectando", 13, Tema.Aviso, negrito: true);
        t.SetPadding(Tema.Dp(c, 6), 0, 0, 0);
        s.AddView(t);
        return s;
    }

    static void AdicionarLives(Context c, LinearLayout col, GroupRoom sala, Action<Member> assistir)
    {
        var lives = sala.Friends.Where(f => f.Live != null).OrderBy(f => f.Live!.SinceUnixMs).ToList();
        if (lives.Count == 0) { col.AddView(NinguemAoVivo(c)); return; }
        col.AddView(TituloDeSecao(c, "Ao vivo agora", lives.Count.ToString()));
        GradeDeLives(c, col, lives, assistir);
    }

    /// <summary>Colunas pela largura: 1 no celular em pé, 2 deitado ou num dobrável, 3 ou mais em tablet (cartão de no mínimo ~300 dp).</summary>
    static void GradeDeLives(Context c, LinearLayout col, List<Member> lives, Action<Member> assistir)
    {
        float larguraUtil = Tema.LarguraDp(c) - (Tema.LarguraDp(c) >= 600 ? 88 + 48 : 32);
        int colunas = Math.Clamp((int)(larguraUtil / 300), 1, 4);
        LinearLayout? linha = null;
        for (int i = 0; i < lives.Count; i++)
        {
            if (i % colunas == 0)
            {
                linha = Tema.Linha(c, GravityFlags.Top);
                linha.WeightSum = colunas;
                col.AddView(linha, Tema.Lp(Tema.Cheio, Tema.Ajusta));
            }
            var lp = Tema.Lp(0, Tema.Ajusta, 1);
            lp.SetMargins(i % colunas == 0 ? 0 : Tema.Dp(c, 6), 0, i % colunas == colunas - 1 ? 0 : Tema.Dp(c, 6), Tema.Dp(c, 14));
            linha!.AddView(CartaoDeLive(c, lives[i], assistir), lp);
        }
    }

    /// <summary>Ninguém ao vivo: o Mumu dormindo num cartão, sem texto de explicação nem botão (riscados no mockup, spec 002).</summary>
    static View NinguemAoVivo(Context c)
    {
        var k = Tema.Coluna(c);
        k.SetGravity(GravityFlags.CenterHorizontal);
        k.Background = Tema.Forma(c, Tema.Cartao, 16, Tema.Borda);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 16), Tema.Dp(c, 16), Tema.Dp(c, 20));
        var img = new ImageView(c);
        img.SetImageResource(Resource.Drawable.mumu_dormindo);
        img.SetScaleType(ImageView.ScaleType.FitCenter);
        k.AddView(img, Tema.Lp(Tema.Dp(c, 128), Tema.Dp(c, 96)));
        var rotulo = Tema.Texto(c, "AO VIVO NO GRUPO", 12, Tema.DestaqueSuave, negrito: true);
        rotulo.LetterSpacing = 0.08f;
        rotulo.SetPadding(0, Tema.Dp(c, 12), 0, Tema.Dp(c, 4));
        rotulo.Gravity = GravityFlags.Center;   // o texto ocupa a largura do cartão: a gravidade da coluna não o centraliza
        k.AddView(rotulo);
        var titulo = Tema.Texto(c, "Ninguém está transmitindo", 18, negrito: true);
        titulo.Gravity = GravityFlags.Center;
        k.AddView(titulo);
        return ComMargemDeCima(c, k, 12);
    }

    /// <summary>A fileira de amigos online (quem está ao vivo primeiro, com o anel vermelho) e, no fim, "Convidar". Rola de lado se não couber.</summary>
    static void AdicionarAmigosOnline(Context c, LinearLayout col, GroupRoom sala, Action<Member> assistir, Action convidar)
    {
        var amigos = sala.Friends.OrderByDescending(f => f.Live != null).ThenBy(f => f.Nick, StringComparer.CurrentCultureIgnoreCase).ToList();
        col.AddView(TituloDeSecao(c, "Amigos online", amigos.Count.ToString()));
        var fileira = Tema.Linha(c, GravityFlags.Top);
        foreach (var f in amigos) fileira.AddView(AmigoNaFileira(c, f, assistir));
        fileira.AddView(BotaoConvidar(c, convidar));
        var rolagem = new HorizontalScrollView(c) { HorizontalScrollBarEnabled = false };
        rolagem.AddView(fileira);
        col.AddView(rolagem, Tema.Lp(Tema.Cheio, Tema.Ajusta));
    }

    static View AmigoNaFileira(Context c, Member m, Action<Member> assistir)
    {
        bool aoVivo = m.Live != null;
        var av = new Avatar(c);
        av.Mostrar(m.Id, m.Nick, Sessao.Avatar(m.Id), aoVivo);
        var item = ItemDaFileira(c, av, m.Nick, aoVivo ? null : Tema.Ok);
        item.ContentDescription = aoVivo ? $"Assistir {m.Nick}" : $"{m.Nick}, online";
        if (!aoVivo) return item;
        item.Clickable = true;
        item.Click += (_, _) => assistir(m);
        return item;
    }

    static View BotaoConvidar(Context c, Action convidar)
    {
        var mais = Tema.Texto(c, "+", 24, Tema.DestaqueSuave, negrito: true);
        mais.Gravity = GravityFlags.Center;
        mais.Background = Tema.Forma(c, Tema.Selecionado, 26);
        var item = ItemDaFileira(c, mais, "Convidar", null);
        item.Clickable = true;
        item.ContentDescription = "Convidar para o grupo";
        item.Click += (_, _) => convidar();
        return item;
    }

    /// <summary>Um lugar da fileira: o círculo de 52 dp (com o ponto de online, se houver) e o nome embaixo.</summary>
    static LinearLayout ItemDaFileira(Context c, View circulo, string nome, Color? ponto)
    {
        var item = Tema.Coluna(c);
        item.SetGravity(GravityFlags.CenterHorizontal);
        item.SetPadding(0, Tema.Dp(c, 4), 0, Tema.Dp(c, 4));
        item.Background = Tema.Tocavel(c, Color.Transparent, 14);
        var quadro = new FrameLayout(c);
        quadro.AddView(circulo, new FrameLayout.LayoutParams(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        if (ponto is { } cor)
            quadro.AddView(new View(c) { Background = Tema.Forma(c, cor, 8, Tema.Fundo) },
                new FrameLayout.LayoutParams(Tema.Dp(c, 15), Tema.Dp(c, 15), GravityFlags.Bottom | GravityFlags.Right));
        item.AddView(quadro, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        var t = Tema.Texto(c, nome, 13, Tema.TextoApagado);
        t.SetMaxLines(1);
        t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        t.Gravity = GravityFlags.Center;
        t.SetPadding(Tema.Dp(c, 2), Tema.Dp(c, 6), Tema.Dp(c, 2), 0);
        item.AddView(t, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        item.LayoutParameters = Tema.Lp(Tema.Dp(c, 76), Tema.Ajusta);
        return item;
    }

    /// <summary>
    /// "Transmitir a tela", no fim do Início. Parado: uma linha (ícone, título, seta) que abre a tela Transmitir. Ao vivo: quantos assistem e
    /// "Parar transmissão".
    /// </summary>
    static View CartaoDeTransmissao(Context c, GroupRoom? sala, Action transmitir, Action parar)
    {
        if (ServicoTransmissao.AoVivo) return CartaoAoVivo(c, parar);
        bool preparando = ServicoTransmissao.Preparando;
        bool pode = sala is { Connected: true } && !preparando;
        var k = Tema.Linha(c);
        k.SetPadding(Tema.Dp(c, 14), Tema.Dp(c, 12), Tema.Dp(c, 12), Tema.Dp(c, 12));
        k.Background = Tema.Tocavel(c, Tema.Cartao, 16, Tema.Borda);
        k.Clickable = pode;
        k.Alpha = pode ? 1f : 0.5f;
        k.Click += (_, _) => { if (pode) transmitir(); };
        k.AddView(IconeEmQuadro(c, Resource.Drawable.ic_ao_vivo), Tema.Lp(Tema.Dp(c, 44), Tema.Dp(c, 44)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, Tema.Dp(c, 8), 0);
        textos.AddView(Tema.Texto(c, preparando ? "Abrindo a transmissão..." : "Transmitir a tela", 16, negrito: true));
        if (ServicoTransmissao.Erro is { } erro) textos.AddView(Tema.Texto(c, erro, 13, Tema.Erro));
        k.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        k.AddView(Tema.Icone(c, Resource.Drawable.ic_seta, Tema.TextoApagado));
        return ComMargemDeCima(c, k, 20);
    }

    static View CartaoAoVivo(Context c, Action parar)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16, Tema.AoVivo);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 14));
        var topo = Tema.Linha(c);
        topo.AddView(Tema.Selo(c, "AO VIVO", Tema.AoVivo));
        var titulo = Tema.Texto(c, "Você está transmitindo a tela", 16, negrito: true);
        titulo.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
        topo.AddView(titulo);
        k.AddView(topo);
        int n = ServicoTransmissao.Assistindo;
        k.AddView(Tema.Texto(c, (n == 0 ? "Esperando amigos" : n == 1 ? "1 pessoa assistindo" : $"{n} pessoas assistindo") + " · " + Qualidade.Escolhida(c).Nome,
            14, Tema.TextoApagado), Tema.Lp(Tema.Cheio, Tema.Ajusta));
        var botaoParar = Tema.Botao(c, "Parar transmissão", principal: false);
        botaoParar.Click += (_, _) => parar();
        var lpP = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lpP.TopMargin = Tema.Dp(c, 12);
        k.AddView(botaoParar, lpP);
        return ComMargemDeCima(c, k, 20);
    }

    static View CartaoDeLive(Context c, Member m, Action<Member> assistir)
    {
        var live = m.Live!;
        var cartao = Tema.Coluna(c);
        cartao.Background = Tema.Tocavel(c, Tema.Cartao, 16);
        cartao.ClipToOutline = true;
        cartao.Clickable = true;
        cartao.ContentDescription = $"Assistir {m.Nick}, {live.Game}";
        cartao.Click += (_, _) => assistir(m);

        // a miniatura em 16:9, com o selo LIVE, a qualidade e quantos assistem por cima
        var quadro = new Proporcao(c, 16, 9);
        quadro.Background = Tema.Forma(c, Tema.Campo, 0);
        if (Sessao.Miniatura(live.StreamId) is { } mini)
        {
            var img = new ImageView(c);
            img.SetImageBitmap(mini);
            img.SetScaleType(ImageView.ScaleType.CenterCrop);
            quadro.AddView(img, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio));
        }
        else
        {
            var gato = new ImageView(c) { Alpha = 0.35f };
            gato.SetImageResource(Resource.Drawable.gato);
            quadro.AddView(gato, new FrameLayout.LayoutParams(Tema.Dp(c, 56), Tema.Dp(c, 56), GravityFlags.Center));
        }
        int s = Tema.Dp(c, 10);
        quadro.AddView(Tema.Selo(c, "AO VIVO", Tema.AoVivo), Canto(GravityFlags.Top | GravityFlags.Left, s));
        if (live.Quality.Length > 0) quadro.AddView(Tema.Selo(c, live.Quality, Color.Argb(170, 0, 0, 0)), Canto(GravityFlags.Top | GravityFlags.Right, s));
        if (live.Watching > 0)
        {
            var olhos = Tema.Linha(c);
            olhos.SetPadding(Tema.Dp(c, 6), Tema.Dp(c, 2), Tema.Dp(c, 8), Tema.Dp(c, 2));
            olhos.Background = Tema.Forma(c, Color.Argb(170, 0, 0, 0), 6);
            olhos.AddView(Tema.Icone(c, Resource.Drawable.ic_olho, Color.White, 14));
            var n = Tema.Texto(c, live.Watching.ToString(), 12, Color.White, negrito: true);
            n.SetPadding(Tema.Dp(c, 4), 0, 0, 0);
            olhos.AddView(n);
            quadro.AddView(olhos, Canto(GravityFlags.Bottom | GravityFlags.Right, s));
        }
        cartao.AddView(quadro, Tema.Lp(Tema.Cheio, Tema.Ajusta));

        var info = Tema.Linha(c);
        info.SetPadding(Tema.Dp(c, 12), Tema.Dp(c, 12), Tema.Dp(c, 12), Tema.Dp(c, 14));
        var av = new Avatar(c);
        av.Mostrar(m.Id, m.Nick, Sessao.Avatar(m.Id), aoVivo: true);
        info.AddView(av, Tema.Lp(Tema.Dp(c, 44), Tema.Dp(c, 44)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 12), 0, 0, 0);
        var nome = Tema.Texto(c, m.Nick, 16, negrito: true);
        nome.SetMaxLines(1); nome.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        textos.AddView(nome);
        var jogo = Tema.Texto(c, live.Game.Length > 0 ? live.Game : "Transmitindo", 14, Tema.TextoApagado);
        jogo.SetMaxLines(1); jogo.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        textos.AddView(jogo);
        info.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        var tempo = Tema.Texto(c, Tema.AoVivoHa(live.SinceUnixMs), 12, Tema.TextoApagado);
        info.AddView(tempo);
        cartao.AddView(info);
        return cartao;
    }
}
