using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using Streemu.Proto.EnvioArquivos;

namespace Streemu.Proto.Ui;

/// <summary>
/// A lista flutuante de "Para quem" (spec 030): no lugar do menu padrão do Android, um painel com a cara do Streemu. Cantos arredondados, borda e sombra do tema,
/// o gatinho no cabeçalho, uma linha por amigo online (foto com o ponto verde, nome e "PRONTO PARA RECEBER") e um visto no escolhido. Aparece com uma entrada suave: o
/// painel desce e ganha corpo em ~200 ms e as linhas entram uma depois da outra; ao escolher, some rápido. Mais de cinco amigos rolam dentro do painel.
/// Ex.: <c>ListaDeAmigos.Mostrar(this, seletor, amigos, st.AmigoId, a =&gt; st.AmigoId = a.Id)</c>
/// </summary>
static class ListaDeAmigos
{
    const int Folga = 12, LinhasSemRolar = 5, AlturaDaLinha = 64;

    public static void Mostrar(Context c, View ancora, IReadOnlyList<AmigoDeArquivos> amigos, string? escolhidoId, Action<AmigoDeArquivos> aoEscolher)
    {
        var (raiz, painel, linhas) = Montar(c, amigos, escolhidoId);
        int folga = Tema.Dp(c, Folga);
        var janela = new PopupWindow(raiz, ancora.Width + 2 * folga, ViewGroup.LayoutParams.WrapContent, true) { OutsideTouchable = true };
        janela.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        for (int i = 0; i < linhas.Count; i++)
        {
            var amigo = amigos[i];
            linhas[i].Click += (_, _) => Sair(painel, janela, () => aoEscolher(amigo));
        }
        janela.ShowAsDropDown(ancora, -folga, Tema.Dp(c, 4) - folga);
        Entrar(c, painel, linhas);
    }

    /// <summary>A raiz tem folga em volta para a sombra do painel caber dentro da janela (uma janela transparente não projeta sombra por conta própria).</summary>
    static (FrameLayout Raiz, LinearLayout Painel, List<View> Linhas) Montar(Context c, IReadOnlyList<AmigoDeArquivos> amigos, string? escolhidoId)
    {
        var painel = Tema.Coluna(c);
        painel.Background = Tema.Forma(c, Tema.Cartao, 18, Tema.DestaqueSuave);
        painel.Elevation = Tema.Dp(c, 10);
        painel.ClipToOutline = true;
        painel.AddView(Cabecalho(c));
        var linhas = new List<View>();
        var lista = Tema.Coluna(c);
        foreach (var a in amigos) { var l = Linha(c, a, a.Id == escolhidoId); linhas.Add(l); lista.AddView(l, Tema.Lp(Tema.Cheio, Tema.Dp(c, AlturaDaLinha))); }
        if (amigos.Count > LinhasSemRolar)
        {
            var rolagem = new ScrollView(c) { VerticalScrollBarEnabled = false };
            rolagem.AddView(lista);
            painel.AddView(rolagem, Tema.Lp(Tema.Cheio, Tema.Dp(c, LinhasSemRolar * AlturaDaLinha)));
        }
        else painel.AddView(lista);
        var raiz = new FrameLayout(c) { };
        raiz.SetPadding(Tema.Dp(c, Folga), Tema.Dp(c, Folga), Tema.Dp(c, Folga), Tema.Dp(c, Folga));
        raiz.SetClipToPadding(false);
        raiz.AddView(painel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return (raiz, painel, linhas);
    }

    /// <summary>O gatinho do Streemu e "Quem vai receber?", numa faixa um tom acima do painel.</summary>
    static View Cabecalho(Context c)
    {
        var faixa = Tema.Linha(c);
        faixa.SetBackgroundColor(Tema.Selecionado);
        faixa.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 10), Tema.Dp(c, 16), Tema.Dp(c, 10));
        var gato = new ImageView(c);
        gato.SetImageResource(Resource.Drawable.gato);
        gato.SetScaleType(ImageView.ScaleType.FitCenter);
        faixa.AddView(gato, Tema.Lp(Tema.Dp(c, 28), Tema.Dp(c, 28)));
        var t = Tema.Texto(c, "Quem vai receber?", 14, Tema.DestaqueSuave, negrito: true);
        t.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
        faixa.AddView(t);
        return faixa;
    }

    static View Linha(Context c, AmigoDeArquivos amigo, bool escolhido)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 16), 0);
        linha.Background = Tema.Tocavel(c, escolhido ? Tema.Selecionado : Color.Transparent, 0);
        linha.Clickable = true;
        linha.ContentDescription = escolhido ? $"{amigo.Nick}, escolhido" : $"Enviar para {amigo.Nick}";
        var foto = new FrameLayout(c);
        var av = new Avatar(c);
        av.Mostrar(amigo.Id, amigo.Nick, Sessao.Avatar(amigo.Id));
        foto.AddView(av, new FrameLayout.LayoutParams(Tema.Dp(c, 40), Tema.Dp(c, 40)));
        foto.AddView(new View(c) { Background = Tema.Forma(c, Tema.Ok, 7, Tema.Cartao) }, new FrameLayout.LayoutParams(Tema.Dp(c, 13), Tema.Dp(c, 13), GravityFlags.Bottom | GravityFlags.Right));
        linha.AddView(foto, Tema.Lp(Tema.Dp(c, 40), Tema.Dp(c, 40)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, 0, 0);
        var nome = Tema.Texto(c, amigo.Nick, 16, negrito: true);
        nome.SetMaxLines(1); nome.Ellipsize = TextUtils.TruncateAt.End;
        textos.AddView(nome);
        textos.AddView(Tema.Texto(c, "PRONTO PARA RECEBER", 11, Tema.Ok, negrito: true));
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        if (escolhido) linha.AddView(Tema.Icone(c, Resource.Drawable.ic_check, Tema.Ok, 22), Tema.Lp(Tema.Dp(c, 22), Tema.Dp(c, 22)));
        return linha;
    }

    /// <summary>O painel desce uns pontos e ganha corpo; cada linha sobe e aparece 35 ms depois da anterior. Curta e discreta: o gesto é de escolher, não de assistir a uma animação.</summary>
    static void Entrar(Context c, View painel, List<View> linhas)
    {
        painel.PivotX = painel.Width / 2f; painel.PivotY = 0;
        painel.Alpha = 0; painel.ScaleX = 0.96f; painel.ScaleY = 0.88f; painel.TranslationY = -Tema.Dp(c, 8);
        painel.Animate()!.Alpha(1).ScaleX(1).ScaleY(1).TranslationY(0).SetDuration(200).SetInterpolator(new DecelerateInterpolator(1.6f))!.Start();
        for (int i = 0; i < linhas.Count; i++)
        {
            var l = linhas[i];
            l.Alpha = 0; l.TranslationY = Tema.Dp(c, 8);
            l.Animate()!.Alpha(1).TranslationY(0).SetStartDelay(60 + i * 35L).SetDuration(180).SetInterpolator(new DecelerateInterpolator())!.Start();
        }
    }

    /// <summary>Some rápido (110 ms) e só então fecha a janela e entrega a escolha, para o painel não piscar.</summary>
    static void Sair(View painel, PopupWindow janela, Action depois)
    {
        painel.Animate()!.Alpha(0).ScaleY(0.94f).SetDuration(110)!.WithEndAction(new Java.Lang.Runnable(() => { janela.Dismiss(); depois(); }))!.Start();
    }
}
