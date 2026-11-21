using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;

namespace Streemu.Proto.Ui;

/// <summary>
/// O conteúdo das três abas do Início, montado a partir do estado da <see cref="Sessao"/>. Cada aba é refeita inteira quando algo muda: são listas
/// curtas (um grupo tem no máximo 50 pessoas), e refazer é mais simples e tão rápido quanto atualizar peça por peça.
/// </summary>
static class Telas
{
    static int Margem(Context c) => Tema.Dp(c, Tema.LarguraDp(c) >= 600 ? 24 : 16);

    static ScrollView Rolagem(Context c, View conteudo)
    {
        var r = new ScrollView(c) { FillViewport = true, VerticalScrollBarEnabled = false };
        r.SetClipToPadding(false);
        r.AddView(conteudo);
        return r;
    }

    static TextView TituloDeSecao(Context c, string t, string? contagem = null)
    {
        var v = Tema.Texto(c, contagem == null ? t : $"{t} · {contagem}", 14, Tema.DestaqueSuave, negrito: true);
        v.LetterSpacing = 0.02f;
        v.SetPadding(0, Tema.Dp(c, 20), 0, Tema.Dp(c, 10));
        return v;
    }

    /// <summary>A sala ainda não conectou (ou caiu e está voltando): o motivo em uma linha, com um indicador de espera.</summary>
    static View EsperandoSala(Context c, GroupRoom? sala)
    {
        var col = Tema.Coluna(c);
        col.SetGravity(GravityFlags.Center);
        col.SetPadding(Tema.Dp(c, 32), Tema.Dp(c, 64), Tema.Dp(c, 32), 0);
        var giro = new ProgressBar(c) { Indeterminate = true };
        giro.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Destaque);
        col.AddView(giro, Tema.Lp(Tema.Dp(c, 40), Tema.Dp(c, 40)));
        var t = Tema.Texto(c, sala?.Status ?? (Sessao.AguardandoAtualizacao ? "Procurando atualização…" : "Escolha um grupo."), 15, Tema.TextoApagado);
        t.Gravity = GravityFlags.Center;
        t.SetPadding(0, Tema.Dp(c, 16), 0, 0);
        col.AddView(t);
        return col;
    }

    // ================================================================================================ Ao vivo

    public static ScrollView AoVivo(Context c, Action<Member> assistir)
    {
        var sala = Sessao.Sala;
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        if (Atualizacao.Disponivel is { } nova) col.AddView(AvisoDaAtualizacao(c, nova));
        if (Atualizacao.BloqueiaSala) return Rolagem(c, col);
        if (!Telemetria.Respondido) col.AddView(AvisoDoDiagnostico(c));
        if (sala is not { Connected: true } && sala?.Members.Count is null or 0) { col.AddView(EsperandoSala(c, sala)); return Rolagem(c, col); }

        var lives = sala!.Friends.Where(f => f.Live != null).OrderBy(f => f.Live!.SinceUnixMs).ToList();
        if (lives.Count == 0)
        {
            col.AddView(Vazio(c, Resource.Drawable.mumu_dormindo, "Ninguém ao vivo agora",
                sala.Friends.Count() is var n && n > 0 ? $"{n} {(n == 1 ? "amigo online" : "amigos online")}. Quando alguém transmitir pelo PC, aparece aqui."
                                                      : "Quando alguém do grupo transmitir pelo PC, aparece aqui."));
            return Rolagem(c, col);
        }
        col.AddView(TituloDeSecao(c, "Ao vivo agora", lives.Count.ToString()));
        // colunas pela largura: 1 no celular em pé, 2 deitado ou num dobrável, 3 ou mais em tablet (cartão de no mínimo ~300 dp)
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
        return Rolagem(c, col);
    }

    internal static View AvisoDaAtualizacao(Context c, VersaoDisponivel nova)
    {
        var cartao = Tema.Coluna(c);
        cartao.Background = Tema.Forma(c, Tema.Selecionado, 16);
        cartao.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 12));
        cartao.LayoutParameters = new LinearLayout.LayoutParams(Tema.Cheio, Tema.Ajusta) { TopMargin = Tema.Dp(c, 12) };
        cartao.AddView(Tema.Texto(c, $"Versão nova · {nova.Versao}", 16, negrito: true));
        if (nova.Obrigatoria)
            cartao.AddView(Tema.Texto(c, "Atualize para entrar na sala do grupo.", 14, Tema.DestaqueSuave));
        if (!string.IsNullOrWhiteSpace(nova.Notas))
        {
            var notas = Tema.Texto(c, nova.Notas, 14, Tema.TextoApagado);
            notas.SetPadding(0, Tema.Dp(c, 8), 0, Tema.Dp(c, 10));
            cartao.AddView(notas);
        }
        var botoes = Tema.Linha(c, GravityFlags.CenterVertical | GravityFlags.End);
        if (!nova.Obrigatoria)
        {
            var depois = Tema.Botao(c, "Depois", principal: false);
            depois.Enabled = Atualizacao.PodeAdiar;
            depois.Click += (_, _) => Atualizacao.Depois();
            botoes.AddView(depois);
        }
        var atualizar = Tema.Botao(c, Atualizacao.Rotulo);
        atualizar.Enabled = !Atualizacao.Ocupado;
        atualizar.Click += (_, _) => Atualizacao.Atualizar();
        botoes.AddView(atualizar, new LinearLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta) { LeftMargin = Tema.Dp(c, 8) });
        cartao.AddView(botoes);
        return cartao;
    }

    /// <summary>
    /// O aviso do diagnóstico, no topo da aba Ao vivo até a pessoa responder (como a faixa do app Windows): nada do diagnóstico sobe antes.
    /// "Entendi" liga; "Desligar" desliga (dá para mudar depois na aba Conta).
    /// </summary>
    static View AvisoDoDiagnostico(Context c)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Selecionado, 16);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 12));
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, 12);
        k.LayoutParameters = lp;
        var linha = Tema.Linha(c);
        linha.AddView(Tema.Icone(c, Resource.Drawable.ic_info, Tema.DestaqueSuave, 20));
        var t = Tema.Texto(c, "Sobre o diagnóstico", 15, negrito: true);
        t.SetPadding(Tema.Dp(c, 8), 0, 0, 0);
        linha.AddView(t);
        k.AddView(linha);
        var d = Tema.Texto(c, Telemetria.TextoDoAviso, 14, Tema.TextoApagado);
        d.SetPadding(0, Tema.Dp(c, 8), 0, Tema.Dp(c, 10));
        k.AddView(d);
        var botoes = Tema.Linha(c, GravityFlags.CenterVertical | GravityFlags.End);
        void Responder(bool ligar)
        {
            Telemetria.Ligado = ligar;
            Telemetria.Respondido = true;
            (k.Parent as ViewGroup)?.RemoveView(k);
        }
        var desligar = Tema.Botao(c, "Desligar", principal: false);
        desligar.SetMinHeight(Tema.Dp(c, 44));
        desligar.Background = Tema.Tocavel(c, Tema.Selecionado, 12);
        desligar.SetTextColor(Tema.DestaqueSuave);
        desligar.Click += (_, _) => Responder(false);
        botoes.AddView(desligar);
        var ok = Tema.Botao(c, "Entendi");
        ok.SetMinHeight(Tema.Dp(c, 44));
        ok.Click += (_, _) => Responder(true);
        var lpOk = Tema.Lp(Tema.Ajusta, Tema.Ajusta);
        lpOk.LeftMargin = Tema.Dp(c, 8);
        botoes.AddView(ok, lpOk);
        k.AddView(botoes, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        return k;
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

    static FrameLayout.LayoutParams Canto(GravityFlags g, int margem)
    {
        var p = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, g);
        p.SetMargins(margem, margem, margem, margem);
        return p;
    }

    static View Vazio(Context c, int imagem, string titulo, string texto)
    {
        var col = Tema.Coluna(c);
        col.SetGravity(GravityFlags.CenterHorizontal);
        // deitado sobra pouca altura: a imagem encolhe para o texto caber sem rolar
        int alturaDp = c.Resources!.Configuration!.ScreenHeightDp;
        int lado = Math.Clamp((int)(alturaDp * 0.3), 96, 180);
        col.SetPadding(Tema.Dp(c, 24), Tema.Dp(c, alturaDp < 500 ? 8 : 40), Tema.Dp(c, 24), Tema.Dp(c, 24));
        var img = new ImageView(c);
        img.SetImageResource(imagem);
        img.SetScaleType(ImageView.ScaleType.FitCenter);
        col.AddView(img, Tema.Lp(Tema.Dp(c, lado), Tema.Dp(c, lado)));
        var t = Tema.Texto(c, titulo, 20, negrito: true);
        t.Gravity = GravityFlags.Center;
        t.SetPadding(0, Tema.Dp(c, 16), 0, 0);
        col.AddView(t);
        var d = Tema.Texto(c, texto, 15, Tema.TextoApagado);
        d.Gravity = GravityFlags.Center;
        d.SetPadding(0, Tema.Dp(c, 8), 0, 0);
        d.SetMaxWidth(Tema.Dp(c, 360));
        col.AddView(d);
        return col;
    }

    // ================================================================================================ Grupo

    public static ScrollView Grupo(Context c, Action<Member> assistir)
    {
        var sala = Sessao.Sala;
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        if (Atualizacao.BloqueiaSala && Atualizacao.Disponivel is { } nova)
        {
            col.AddView(AvisoDaAtualizacao(c, nova));
            return Rolagem(c, col);
        }
        if (sala is not { Connected: true } && sala?.Members.Count is null or 0) { col.AddView(EsperandoSala(c, sala)); return Rolagem(c, col); }

        var online = sala!.Friends.OrderByDescending(f => f.Live != null).ThenBy(f => f.Nick, StringComparer.CurrentCultureIgnoreCase).ToList();
        col.AddView(TituloDeSecao(c, "Online", online.Count.ToString()));
        var cartao = Cartao(c);
        if (online.Count == 0) cartao.AddView(LinhaSimples(c, "Ninguém online agora além de você."));
        foreach (var f in online)
            cartao.AddView(Pessoa(c, f.Id, f.Nick, f.Live is { } l ? $"Ao vivo{(l.Game.Length > 0 ? ": " + l.Game : "")}" : "Online",
                f.Live != null ? Tema.AoVivo : Tema.Ok, f.Live != null ? () => assistir(f) : null));
        col.AddView(cartao);

        if (sala.Ausentes.Count > 0)
        {
            col.AddView(TituloDeSecao(c, "Offline", sala.Ausentes.Count.ToString()));
            var off = Cartao(c);
            foreach (var a in sala.Ausentes.OrderBy(a => a.Nick, StringComparer.CurrentCultureIgnoreCase))
            {
                var p = Pessoa(c, a.Id, a.Nick, "Offline", null, null);
                p.Alpha = 0.6f;
                off.AddView(p);
            }
            col.AddView(off);
        }

        var frases = FrasesDeAtividade.Montar(sala.Atividades, (_, apelido) => apelido, DateTimeOffset.Now, meuId: sala.MyId);
        col.AddView(TituloDeSecao(c, "Atividade recente"));
        var ativ = Cartao(c);
        if (frases.Count == 0) ativ.AddView(LinhaSimples(c, FrasesDeAtividade.Vazio));
        foreach (var f in frases)
        {
            var linha = Tema.Linha(c, GravityFlags.Top);
            linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
            var ponto = new View(c) { Background = Tema.Forma(c, f.Tipo is "live" ? Tema.AoVivo : f.Tipo is "live_fim" ? Tema.DestaqueSuave : Tema.Neutro, 4) };
            var lpP = Tema.Lp(Tema.Dp(c, 8), Tema.Dp(c, 8));
            lpP.TopMargin = Tema.Dp(c, 7);
            linha.AddView(ponto, lpP);
            var t = Tema.Texto(c, f.Texto, 15);
            t.SetPadding(Tema.Dp(c, 12), 0, Tema.Dp(c, 8), 0);
            linha.AddView(t, Tema.Lp(0, Tema.Ajusta, 1));
            linha.AddView(Tema.Texto(c, f.Quando, 13, Tema.TextoApagado));
            ativ.AddView(linha);
        }
        col.AddView(ativ);
        return Rolagem(c, col);
    }

    static LinearLayout Cartao(Context c)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16);
        k.ClipToOutline = true;
        k.SetPadding(0, Tema.Dp(c, 4), 0, Tema.Dp(c, 4));
        return k;
    }

    static View LinhaSimples(Context c, string texto)
    {
        var t = Tema.Texto(c, texto, 15, Tema.TextoApagado);
        t.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 14));
        return t;
    }

    static View Pessoa(Context c, string conta, string nick, string estado, Color? corEstado, Action? tocar)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 10), Tema.Dp(c, 12), Tema.Dp(c, 10));
        linha.SetMinimumHeight(Tema.Dp(c, 64));
        var av = new Avatar(c);
        av.Mostrar(conta, nick, Sessao.Avatar(conta), aoVivo: corEstado == Tema.AoVivo);
        linha.AddView(av, Tema.Lp(Tema.Dp(c, 44), Tema.Dp(c, 44)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, 0, 0);
        var n = Tema.Texto(c, nick, 16, negrito: true);
        n.SetMaxLines(1); n.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        textos.AddView(n);
        var e = Tema.Texto(c, estado, 14, corEstado == Tema.AoVivo ? Tema.AoVivo : Tema.TextoApagado);
        e.SetMaxLines(1); e.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        textos.AddView(e);
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        if (tocar != null)
        {
            var b = Tema.Selo(c, "Assistir", Tema.Destaque);
            b.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
            b.SetPadding(Tema.Dp(c, 14), Tema.Dp(c, 8), Tema.Dp(c, 14), Tema.Dp(c, 8));
            b.Background = Tema.Forma(c, Tema.Destaque, 18);
            linha.AddView(b);
            linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
            linha.Clickable = true;
            linha.Click += (_, _) => tocar();
        }
        return linha;
    }

    // ================================================================================================ Conta

    public static ScrollView Conta(Context c, Action trocarApelido, Action sair)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        // em tela larga a coluna fica com largura de leitura, centralizada
        var envelope = Tema.Coluna(c);
        envelope.SetGravity(GravityFlags.CenterHorizontal);
        envelope.AddView(col, new LinearLayout.LayoutParams(Math.Min(Tema.Dp(c, 640), c.Resources!.DisplayMetrics!.WidthPixels), Tema.Ajusta));

        col.AddView(TituloDeSecao(c, "Você"));
        var eu = Cartao(c);
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 14));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.Click += (_, _) => trocarApelido();
        var av = new Avatar(c);
        av.Mostrar(Sessao.Conta.ContaId ?? "eu", Sessao.Apelido, null);
        linha.AddView(av, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, 0, 0);
        textos.AddView(Tema.Texto(c, Sessao.Apelido, 18, negrito: true));
        textos.AddView(Tema.Texto(c, "Seu nome no grupo · toque para trocar", 14, Tema.TextoApagado));
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        eu.AddView(linha);
        col.AddView(eu);

        col.AddView(TituloDeSecao(c, "Grupos", Sessao.Conta.Grupos.Count.ToString()));
        var grupos = Cartao(c);
        if (Sessao.Conta.Grupos.Count == 0) grupos.AddView(LinhaSimples(c, "A conta não está em nenhum grupo. Entre num grupo pelo app no PC."));
        foreach (var g in Sessao.Conta.Grupos)
        {
            var l = Tema.Linha(c);
            l.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 16), 0);
            l.SetMinimumHeight(Tema.Dp(c, 56));
            l.Background = Tema.Tocavel(c, Tema.Cartao, 0);
            l.Clickable = true;
            l.Click += (_, _) => Sessao.AbrirSala(g.Id);
            l.AddView(Tema.Texto(c, g.Nome, 16), Tema.Lp(0, Tema.Ajusta, 1));
            if (g.Id == Sessao.GrupoId) l.AddView(Tema.Icone(c, Resource.Drawable.ic_check, Tema.DestaqueSuave));
            grupos.AddView(l);
        }
        col.AddView(grupos);

        col.AddView(TituloDeSecao(c, "Diagnóstico"));
        var diag = Cartao(c);
        var linhaDiag = Tema.Linha(c);
        linhaDiag.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        linhaDiag.SetMinimumHeight(Tema.Dp(c, 64));
        linhaDiag.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linhaDiag.Clickable = true;
        var textosDiag = Tema.Coluna(c);
        textosDiag.AddView(Tema.Texto(c, "Enviar diagnóstico", 16));
        var estadoDiag = Tema.Texto(c, "", 13, Tema.TextoApagado);
        textosDiag.AddView(estadoDiag);
        linhaDiag.AddView(textosDiag, Tema.Lp(0, Tema.Ajusta, 1));
        var chave = new Chave(c);
        linhaDiag.AddView(chave, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 32)));
        void Mostrar()
        {
            chave.Ligada = Telemetria.Ligado;
            estadoDiag.Text = !Telemetria.Ligado ? "Desligado: nada sai do celular"
                            : !Telemetria.Respondido ? "Esperando você responder ao aviso no Ao vivo"
                            : Telemetria.UltimoErro is { } e ? "Último envio falhou: " + e
                            : Telemetria.UltimoOk is { } ok ? $"Ligado · último envio às {ok:HH:mm}" : "Ligado";
        }
        linhaDiag.Click += (_, _) => { Telemetria.Ligado = !Telemetria.Ligado; Telemetria.Respondido = true; Mostrar(); };
        Mostrar();
        diag.AddView(linhaDiag);
        var sobreDiag = Tema.Texto(c, Telemetria.TextoDoAviso, 13, Tema.TextoApagado);
        sobreDiag.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 16), Tema.Dp(c, 14));
        diag.AddView(sobreDiag);
        col.AddView(diag);

        col.AddView(TituloDeSecao(c, "Sobre"));
        var sobre = Cartao(c);
        sobre.AddView(LinhaSimples(c, $"Streemu para Android · {Atualizacao.VersaoInstalada}"));
        var procurar = Tema.Botao(c, "Procurar atualização", principal: false);
        procurar.Enabled = !Atualizacao.Ocupado;
        procurar.Click += (_, _) => Atualizacao.Verificar(manual: true);
        sobre.AddView(procurar);
        col.AddView(sobre);

        var sairB = Tema.Botao(c, "Sair da conta", principal: false);
        sairB.SetTextColor(Tema.Erro);
        sairB.Background = Tema.Tocavel(c, Tema.Cartao, 14);
        sairB.Click += (_, _) => sair();
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, 24);
        col.AddView(sairB, lp);
        return Rolagem(c, envelope);
    }
}

/// <summary>Moldura com proporção fixa (a miniatura em 16:9): a altura sai da largura que o layout der.</summary>
sealed class Proporcao : FrameLayout
{
    readonly int _w, _h;
    public Proporcao(Context c, int w, int h) : base(c) { _w = w; _h = h; }
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int largura = MeasureSpec.GetSize(widthMeasureSpec);
        base.OnMeasure(widthMeasureSpec, MeasureSpec.MakeMeasureSpec(largura * _h / _w, MeasureSpecMode.Exactly));
    }
}

/// <summary>Chave liga/desliga desenhada (o Switch do Android sem tema Material fica com cara de outro app): trilho redondo e bolinha, nas cores do Streemu.</summary>
sealed class Chave : View
{
    readonly Paint _p = new(PaintFlags.AntiAlias);
    bool _ligada;
    public Chave(Context c) : base(c) { ImportantForAccessibility = ImportantForAccessibility.No; }
    public bool Ligada { get => _ligada; set { _ligada = value; Invalidate(); } }
    protected override void OnDraw(Canvas canvas)
    {
        float w = Width, h = Height, r = h / 2f;
        _p.Color = _ligada ? Tema.Destaque : Tema.Neutro;
        canvas.DrawRoundRect(0, 0, w, h, r, r, _p);
        _p.Color = Color.White;
        float folga = h * 0.14f;
        canvas.DrawCircle(_ligada ? w - r : r, r, r - folga, _p);
    }
}
