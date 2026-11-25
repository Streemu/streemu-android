using Android.App;
using Android.Graphics;
using Android.Views;

namespace Streemu.Proto.Ui;

/// <summary>
/// Desenho de ponta a ponta (o padrão do Android 15+): as barras do sistema ficam transparentes sobre o roxo do app, e o conteúdo recebe de margem só
/// o espaço delas, do recorte da câmera e do teclado. Assim o app encaixa em qualquer celular (entalhe, barra de gestos ou de botões, dobrável).
/// </summary>
static class Janela
{
    public static void BordaABorda(Activity a)
    {
        var w = a.Window!;
        if (OperatingSystem.IsAndroidVersionAtLeast(35)) { }   // no Android 15+ o app já desenha de ponta a ponta
        else if (OperatingSystem.IsAndroidVersionAtLeast(30)) w.SetDecorFitsSystemWindows(false);
        else
#pragma warning disable CA1422 // antes do Android 11 não há outro jeito
            w.DecorView.SystemUiFlags = SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
#pragma warning restore CA1422
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CA1422
            w.SetStatusBarColor(Color.Transparent);
            w.SetNavigationBarColor(Color.Transparent);
#pragma warning restore CA1422
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            w.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
    }

    /// <summary>
    /// Cor dos ícones das barras do sistema (relógio, bateria, gestos): escuros sobre o fundo claro, claros sobre o escuro. Com as barras transparentes
    /// sobre o app, ícone claro em fundo claro some. Chamar depois do <see cref="BordaABorda"/>, que mexe nas mesmas flags antes do Android 11.
    /// Ex.: <c>Janela.IconesDasBarras(this, fundoClaro: Tema.Atual.Claro);</c>
    /// </summary>
    public static void IconesDasBarras(Activity a, bool fundoClaro)
    {
        var w = a.Window!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            int mascara = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
            w.InsetsController?.SetSystemBarsAppearance(fundoClaro ? mascara : 0, mascara);
            return;
        }
#pragma warning disable CA1422 // antes do Android 11 não há outro jeito
        var flags = w.DecorView.SystemUiFlags;
        var claras = SystemUiFlags.LightStatusBar | (OperatingSystem.IsAndroidVersionAtLeast(26) ? SystemUiFlags.LightNavigationBar : 0);
        w.DecorView.SystemUiFlags = fundoClaro ? flags | claras : flags & ~claras;
#pragma warning restore CA1422
    }

    /// <summary>Margem do tamanho das barras do sistema, do recorte e do teclado (o teclado aberto empurra o conteúdo para cima).</summary>
    public static void AplicarMargens(View raiz)
    {
        raiz.SetOnApplyWindowInsetsListener(new Margens());
        raiz.RequestApplyInsets();
    }

    sealed class Margens : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout() | WindowInsets.Type.Ime());
                v.SetPadding(i.Left, i.Top, i.Right, i.Bottom);
            }
            else
            {
#pragma warning disable CA1422
                v.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }
            return insets;
        }
    }
}
