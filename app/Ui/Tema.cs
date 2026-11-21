using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>
/// A identidade do Streemu no celular: as cores do tema escuro do app Windows (DESIGN.md / Util/Theme.cs, conferidas em 2026-11-21), em dp e com toque de
/// celular (ripple, alvos de 48 dp, cantos maiores). Sem biblioteca Material: só componentes do Android, para o app ficar leve e abrir rápido.
/// </summary>
static class Tema
{
    public static readonly Color Fundo = Color.ParseColor("#2A2640");      // surface
    public static readonly Color Cartao = Color.ParseColor("#1E1B2E");     // surface-raised
    public static readonly Color Campo = Color.ParseColor("#25213A");      // surface-input
    public static readonly Color Barra = Color.ParseColor("#171523");      // title-bar: barras de navegação
    public static readonly Color Borda = Color.ParseColor("#4B4463");      // card-border
    public static readonly Color CorTexto = Color.ParseColor("#F4F0FA");
    public static readonly Color TextoApagado = Color.ParseColor("#C5BADB");
    public static readonly Color Destaque = Color.ParseColor("#7C62DC");   // accent: a ação principal
    public static readonly Color DestaqueSuave = Color.ParseColor("#B9A8F4");
    public static readonly Color Selecionado = Color.ParseColor("#3A3160");
    public static readonly Color Neutro = Color.ParseColor("#49415F");
    public static readonly Color Ok = Color.ParseColor("#3DDC6A");
    public static readonly Color Aviso = Color.ParseColor("#F0B232");
    public static readonly Color Erro = Color.ParseColor("#E5484D");
    public static readonly Color AoVivo = Color.ParseColor("#E5484D");     // selo LIVE (o mesmo vermelho do app no PC)

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
        new RippleDrawable(ColorStateList.ValueOf(Color.Argb(60, 255, 255, 255)), Forma(c, cor, raioDp, borda), Forma(c, Color.White, raioDp));

    public static TextView Texto(Context c, string t, float sp, Color? cor = null, bool negrito = false)
    {
        var v = new TextView(c) { Text = t };
        v.SetTextColor(cor ?? CorTexto);
        v.SetTextSize(ComplexUnitType.Sp, sp);
        if (negrito) v.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        return v;
    }

    /// <summary>Botão de ação principal (destaque) ou secundário (neutro): 52 dp de altura, texto médio, cantos de 14 dp.</summary>
    public static TextView Botao(Context c, string t, bool principal = true)
    {
        var b = Texto(c, t, 16, principal ? Color.White : CorTexto, negrito: true);
        b.Gravity = GravityFlags.Center;
        b.SetMinHeight(Dp(c, 52));
        b.SetPadding(Dp(c, 20), 0, Dp(c, 20), 0);
        b.Background = Tocavel(c, principal ? Destaque : Neutro, 14);
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
