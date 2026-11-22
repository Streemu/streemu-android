using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Transmissao;

namespace Streemu.Proto.Ui;

/// <summary>
/// O conteúdo das abas do Início, montado a partir do estado da <see cref="Sessao"/>; este arquivo tem as peças comuns, e cada aba tem o seu
/// (<c>TelaInicio.cs</c>, <c>TelaGrupos.cs</c>, <c>TelaMais.cs</c>). Cada aba é refeita inteira quando algo muda: são listas curtas (um grupo tem no
/// máximo 50 pessoas), e refazer é mais simples e tão rápido quanto atualizar peça por peça.
/// </summary>
static partial class Telas
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
    static View EsperandoSala(Context c, GroupRoom? sala, Action? criar = null, Action? entrar = null)
    {
        // conta sem nenhum grupo: não há o que esperar. É a primeira tela de quem acabou de entrar, então já oferece criar ou entrar
        if (sala == null && !Sessao.AguardandoAtualizacao && Sessao.Conta.Grupos.Count == 0)
        {
            if (criar == null || entrar == null) return Vazio(c, Resource.Drawable.mumu_dormindo, "Você ainda não está em um grupo", "Crie um grupo ou entre com um convite na aba Grupos.");
            var vazio = Vazio(c, Resource.Drawable.mumu_dormindo, "Você ainda não está em um grupo", "Crie um grupo ou entre com um convite.");
            var botoes = BotoesDeGrupo(c, criar, entrar, vertical: true);
            var lpB = Tema.Lp(Tema.Dp(c, Math.Min(320, Tema.LarguraDp(c) - 48)), Tema.Ajusta);
            lpB.TopMargin = Tema.Dp(c, 20);
            ((LinearLayout)vazio).AddView(botoes, lpB);
            return vazio;
        }
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
    /// O aviso do diagnóstico, no topo da aba Ao vivo até a pessoa responder (como a faixa do app Windows). O diagnóstico vem ligado:
    /// "Entendi" mantém; "Desligar" desliga de vez (dá para mudar depois na aba Mais).
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

    /// <summary>O título grande do alto de uma aba ("Sua sala", "Grupos"), sem subtítulo (spec 002, decisão 3).</summary>
    static TextView TituloDaTela(Context c, string texto)
    {
        var t = Tema.Texto(c, texto, 24, negrito: true);
        t.SetPadding(0, Tema.Dp(c, 8), 0, Tema.Dp(c, 4));
        return t;
    }

    /// <summary>Um ícone num quadrado de cantos redondos (o do cartão de transmitir e o de cada grupo).</summary>
    static View IconeEmQuadro(Context c, int icone)
    {
        var fundo = new FrameLayout(c) { Background = Tema.Forma(c, Tema.Selecionado, 12) };
        fundo.AddView(Tema.Icone(c, icone, Tema.DestaqueSuave), new FrameLayout.LayoutParams(Tema.Dp(c, 24), Tema.Dp(c, 24), GravityFlags.Center));
        return fundo;
    }

    static View ComMargemDeCima(Context c, View v, int dp)
    {
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, dp);
        v.LayoutParameters = lp;
        return v;
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

    /// <summary>"Criar grupo" e "Entrar com convite": lado a lado (na aba Grupos) ou um sobre o outro (no Início, sem grupo).</summary>
    static LinearLayout BotoesDeGrupo(Context c, Action criar, Action entrar, bool vertical = false)
    {
        var caixa = vertical ? Tema.Coluna(c) : Tema.Linha(c);
        var b1 = BotaoComIcone(c, "Criar grupo", Resource.Drawable.ic_criar_grupo, principal: true);
        var b2 = BotaoComIcone(c, "Entrar com convite", Resource.Drawable.ic_chave, principal: false);
        b1.Click += (_, _) => criar();
        b2.Click += (_, _) => entrar();
        int vao = Tema.Dp(c, vertical ? 10 : 8);
        var lp1 = vertical ? Tema.Lp(Tema.Cheio, Tema.Dp(c, 48)) : Tema.Lp(0, Tema.Dp(c, 48), 1);
        var lp2 = vertical ? Tema.Lp(Tema.Cheio, Tema.Dp(c, 48)) : Tema.Lp(0, Tema.Dp(c, 48), 1);
        if (vertical) lp2.TopMargin = vao; else lp2.LeftMargin = vao;
        caixa.AddView(b1, lp1);
        caixa.AddView(b2, lp2);
        return caixa;
    }

    /// <summary>
    /// Botão de 48 dp com ícone e texto, como os do mockup de Grupos: o principal é cheio (destaque), o outro só tem contorno.
    /// Ex.: <c>BotaoComIcone(c, "Criar grupo", Resource.Drawable.ic_criar_grupo, principal: true)</c>
    /// </summary>
    static LinearLayout BotaoComIcone(Context c, string texto, int icone, bool principal)
    {
        var b = Tema.Linha(c, GravityFlags.Center);
        b.SetPadding(Tema.Dp(c, 10), 0, Tema.Dp(c, 10), 0);
        b.Background = principal ? Tema.Tocavel(c, Tema.Destaque, 12) : Tema.Tocavel(c, Tema.Cartao, 12, Tema.Borda);
        b.Clickable = true;
        b.Focusable = true;
        b.ContentDescription = texto;
        b.AddView(Tema.Icone(c, icone, principal ? Color.White : Tema.CorTexto, 18));
        var t = Tema.Texto(c, texto, 14, principal ? Color.White : Tema.CorTexto, negrito: true);
        t.SetMaxLines(1);
        t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        t.SetPadding(Tema.Dp(c, 8), 0, 0, 0);
        b.AddView(t);
        return b;
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
