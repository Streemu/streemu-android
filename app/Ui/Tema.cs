using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>
/// A identidade do Streemu no celular: as cores dos temas escuro e claro do app Windows (DESIGN.md / Util/ThemePalette.cs; ver <see cref="Paleta"/>), em dp e com toque de
/// celular (ripple, alvos de 48 dp, cantos maiores). Sem biblioteca Material: só componentes do Android, para o app ficar leve e abrir rápido.
/// </summary>
static class Tema
{
    public static Paleta Atual => Paleta.Atual;
    /// <summary>A paleta escura fixa: o player e o filme ficam sobre vídeo e não mudam de tema.</summary>
    public static Paleta Escuro => Paleta.Escura;

    public static Color Fundo => Atual.Fundo;
    public static Color Cartao => Atual.Cartao;
    public static Color Campo => Atual.Campo;
    public static Color Barra => Atual.Barra;                  // barras de navegação
    public static Color Borda => Atual.Borda;
    public static Color Fio => Atual.Fio;
    public static Color CorTexto => Atual.CorTexto;
    public static Color TextoApagado => Atual.TextoApagado;
    public static Color Destaque => Atual.Destaque;            // a ação principal
    public static Color DestaqueSuave => Atual.DestaqueSuave;
    public static Color Selecionado => Atual.Selecionado;
    public static Color Neutro => Atual.Neutro;
    public static Color Ok => Atual.Ok;
    public static Color Aviso => Atual.Aviso;
    public static Color Erro => Atual.Erro;
    public static Color AoVivo => Atual.AoVivo;                // selo LIVE (o mesmo vermelho do app no PC)

    /// <summary>Lê a escolha do tema e a deixa valendo. Chamar no começo do OnCreate da tela principal, antes de montar qualquer coisa.</summary>
    public static void Aplicar(Context c) => Paleta.Atual = PreferenciaDeTema.EhClaro(c) ? Paleta.Clara : Paleta.Escura;

    /// <summary>O tema do Android da tela principal: dele vêm as cores do PopupMenu, do campo de texto e do cursor, que não passam pela <see cref="Paleta"/>.</summary>
    public static int EstiloDaTela => Atual.Claro ? Android.Resource.Style.ThemeDeviceDefaultLightNoActionBar : Android.Resource.Style.ThemeDeviceDefaultNoActionBar;

    /// <summary>O estilo das caixas de aviso (AlertDialog) da tela principal; as do player seguem escuras.</summary>
    public static int EstiloDoDialogo => Atual.Claro ? Android.Resource.Style.ThemeDeviceDefaultLightDialogAlert : Android.Resource.Style.ThemeDeviceDefaultDialogAlert;

    public static int Dp(Context c, float dp) => (int)Math.Round(TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, c.Resources!.DisplayMetrics));
    public static float LarguraDp(Context c) => c.Resources!.Configuration!.ScreenWidthDp;

    public static GradientDrawable Forma(Context c, Color cor, float raioDp, Color? borda = null)
    {
        var g = new GradientDrawable();
        g.SetColor(cor);
        g.SetCornerRadius(Dp(c, raioDp));
        if (borda is { } b) g.SetStroke(Dp(c, 1), b);
        return g;
    }

    /// <summary>Fundo com o ripple do Android por cima (o retorno do toque que o celular espera).</summary>
    public static Drawable Tocavel(Context c, Color cor, float raioDp, Color? borda = null) =>
        new RippleDrawable(ColorStateList.ValueOf(Paleta.De(c).Brilho), Forma(c, cor, raioDp, borda), Forma(c, Color.White, raioDp));

    public static TextView Texto(Context c, string t, float sp, Color? cor = null, bool negrito = false)
    {
        var v = new TextView(c) { Text = t };
        v.SetTextColor(cor ?? Paleta.De(c).CorTexto);
        v.SetTextSize(ComplexUnitType.Sp, sp);
        if (negrito) v.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        return v;
    }

    /// <summary>Botão de ação principal (destaque) ou secundário (neutro): 52 dp de altura, texto médio, cantos de 14 dp.</summary>
    public static TextView Botao(Context c, string t, bool principal = true)
    {
        var p = Paleta.De(c);
        var b = Texto(c, t, 16, principal ? Color.White : p.TextoNeutro, negrito: true);
        b.Gravity = GravityFlags.Center;
        b.SetMinHeight(Dp(c, 52));
        b.SetPadding(Dp(c, 20), 0, Dp(c, 20), 0);
        b.Background = Tocavel(c, principal ? p.Destaque : p.Neutro, 14);
        b.Clickable = true; b.Focusable = true;
        return b;
    }

    /// <summary>Selo pequeno de cantos redondos (LIVE, qualidade, contagem).</summary>
    public static TextView Selo(Context c, string t, Color fundo, Color? texto = null)
    {
        var s = Texto(c, t, 11, texto ?? Color.White, negrito: true);
        s.SetPadding(Dp(c, 7), Dp(c, 2), Dp(c, 7), Dp(c, 2));
        s.Background = Forma(c, fundo, 6);
        s.LetterSpacing = 0.04f;
        return s;
    }

    public static ImageView Icone(Context c, int recurso, Color cor, float tamanhoDp = 24)
    {
        var i = new ImageView(c);
        i.SetImageResource(recurso);
        i.ImageTintList = ColorStateList.ValueOf(cor);
        i.LayoutParameters = new LinearLayout.LayoutParams(Dp(c, tamanhoDp), Dp(c, tamanhoDp));
        return i;
    }

    public static LinearLayout Linha(Context c, GravityFlags g = GravityFlags.CenterVertical)
        { var l = new LinearLayout(c) { Orientation = Android.Widget.Orientation.Horizontal }; l.SetGravity(g); return l; }

    public static LinearLayout Coluna(Context c) => new(c) { Orientation = Android.Widget.Orientation.Vertical };

    public static LinearLayout.LayoutParams Lp(int w, int h, float peso = 0, int margem = 0)
    {
        var p = new LinearLayout.LayoutParams(w, h, peso);
        p.SetMargins(margem, margem, margem, margem);
        return p;
    }

    public const int Cheio = ViewGroup.LayoutParams.MatchParent, Ajusta = ViewGroup.LayoutParams.WrapContent;

    /// <summary>Cor estável por pessoa para o avatar sem foto (a mesma conta sempre ganha a mesma cor).</summary>
    public static Color CorDe(string id)
    {
        Color[] cores = { Color.ParseColor("#7C62DC"), Color.ParseColor("#C0569A"), Color.ParseColor("#3E8ED0"), Color.ParseColor("#2FA37A"),
                          Color.ParseColor("#D08A3E"), Color.ParseColor("#8E5BD6"), Color.ParseColor("#4F6BD8") };
        uint h = 2166136261;
        foreach (char ch in id) h = (h ^ ch) * 16777619;
        return cores[h % cores.Length];
    }

    /// <summary>"Há 12 min", "há 2 h 05 min": há quanto tempo a live começou.</summary>
    public static string AoVivoHa(long desdeUnixMs)
    {
        desdeUnixMs = Streemu.Util.JsonSeguro.HoraUnixMs(desdeUnixMs);   // vem de quem transmite: fora da faixa, FromUnixTimeMilliseconds lança
        if (desdeUnixMs <= 0) return "";
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(desdeUnixMs);
        if (d.TotalMinutes < 1) return "agora";
        return "há " + Streemu.Net.FrasesDeAtividade.Duracao((int)d.TotalMinutes);
    }
}
