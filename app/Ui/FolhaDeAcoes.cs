using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>Uma linha da <see cref="FolhaDeAcoes"/>: ícone, texto e o que fazer. <see cref="Perigosa"/> pinta de vermelho (remover, sair).</summary>
sealed record AcaoDaFolha(int Icone, string Texto, Action Executar, bool Perigosa = false);

/// <summary>
/// O menu de ações do app: uma folha que sobe da borda de baixo, nas cores do tema (claro e escuro), no lugar do PopupMenu do Android, que sai com a cara do
/// sistema. Toque fora ou numa ação fecha. Tem de ser chamada da thread da tela.
/// Uso: <c>FolhaDeAcoes.Mostrar(this, "Sua foto de perfil", new[] { new AcaoDaFolha(Resource.Drawable.ic_imagem, "Escolher foto ou GIF", Escolher) });</c>
/// </summary>
static class FolhaDeAcoes
{
    const int LarguraMaximaDp = 560, RaioDp = 24;

    public static void Mostrar(Activity tela, string titulo, IReadOnlyList<AcaoDaFolha> acoes)
    {
        var dialogo = new Dialog(tela);
        dialogo.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialogo.SetContentView(Montar(tela, titulo, acoes, dialogo.Dismiss));
        dialogo.SetCanceledOnTouchOutside(true);
        var janela = dialogo.Window!;
        janela.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        janela.SetGravity(GravityFlags.Bottom);
        janela.SetLayout(Math.Min(Tema.Dp(tela, LarguraMaximaDp), tela.Resources!.DisplayMetrics!.WidthPixels), ViewGroup.LayoutParams.WrapContent);
        dialogo.Show();
    }

    static View Montar(Activity tela, string titulo, IReadOnlyList<AcaoDaFolha> acoes, Action fechar)
    {
        var folha = Tema.Coluna(tela);
        folha.Background = FundoDaFolha(tela);
        folha.SetPadding(0, Tema.Dp(tela, 10), 0, Tema.Dp(tela, 8));
        folha.SetOnApplyWindowInsetsListener(new MargemDeBaixo(Tema.Dp(tela, 8)));
        folha.AddView(Puxador(tela), new LinearLayout.LayoutParams(Tema.Dp(tela, 36), Tema.Dp(tela, 4)) { Gravity = GravityFlags.CenterHorizontal });
        var cabecalho = Tema.Texto(tela, titulo, 15, Tema.TextoApagado, negrito: true);
        cabecalho.SetPadding(Tema.Dp(tela, 20), Tema.Dp(tela, 14), Tema.Dp(tela, 20), Tema.Dp(tela, 6));
        folha.AddView(cabecalho);
        foreach (var acao in acoes) folha.AddView(Linha(tela, acao, fechar));
        return folha;
    }

    /// <summary>Cartão com os cantos de cima redondos e o fio da borda: a mesma pele dos cartões do app, colada embaixo.</summary>
    static Drawable FundoDaFolha(Context c)
    {
        float r = Tema.Dp(c, RaioDp);
        var fundo = new GradientDrawable();
        fundo.SetColor(Tema.Cartao);
        fundo.SetCornerRadii(new[] { r, r, r, r, 0f, 0f, 0f, 0f });
        fundo.SetStroke(Tema.Dp(c, 1), Tema.Borda);
        return fundo;
    }

    static View Puxador(Context c) => new(c) { Background = Tema.Forma(c, Tema.Borda, 2) };

    static View Linha(Activity tela, AcaoDaFolha acao, Action fechar)
    {
        var cor = acao.Perigosa ? Tema.Erro : Tema.CorTexto;
        var linha = Tema.Linha(tela);
        linha.SetPadding(Tema.Dp(tela, 20), 0, Tema.Dp(tela, 20), 0);
        linha.SetMinimumHeight(Tema.Dp(tela, 56));
        linha.Background = Tema.Tocavel(tela, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.Click += (_, _) => { fechar(); acao.Executar(); };
        linha.AddView(Tema.Icone(tela, acao.Icone, acao.Perigosa ? Tema.Erro : Tema.DestaqueSuave));
        var texto = Tema.Texto(tela, acao.Texto, 16, cor);
        texto.SetPadding(Tema.Dp(tela, 16), 0, 0, 0);
        linha.AddView(texto, Tema.Lp(0, Tema.Ajusta, 1));
        return linha;
    }

    /// <summary>Folga embaixo para a barra de gestos ou de botões não cobrir a última ação (a janela do diálogo pode desenhar por baixo dela).</summary>
    sealed class MargemDeBaixo : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        readonly int _minimo;
        public MargemDeBaixo(int minimo) { _minimo = minimo; }

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            int barra = OperatingSystem.IsAndroidVersionAtLeast(30)
                ? insets.GetInsets(WindowInsets.Type.SystemBars()).Bottom
#pragma warning disable CA1422 // antes do Android 11 não há outro jeito
                : insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
            v.SetPadding(0, v.PaddingTop, 0, Math.Max(_minimo, barra));
            return insets;
        }
    }
}
