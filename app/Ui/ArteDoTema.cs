using Android.Content;

namespace Streemu.Proto.Ui;

/// <summary>
/// A arte (Mumu, gato, logo) para o tema da tela: no claro troca pela versão <c>_claro</c>, a mesma do app Windows (<c>UiBrand.ThemedName</c>).
/// A Mumu do controle não tem versão clara no PC e fica como está. Telas escuras fixas (player, filme) sempre recebem a escura.
/// Uso: <c>img.SetImageResource(ArteDoTema.Para(c, Resource.Drawable.mumu_pipoca));</c>
/// </summary>
static class ArteDoTema
{
    public static int Para(Context c, int escura) => Paleta.De(c).Claro ? Clara(escura) ?? escura : escura;

    static int? Clara(int escura) =>
        escura == Resource.Drawable.mumu_dormindo ? Resource.Drawable.mumu_dormindo_claro
        : escura == Resource.Drawable.mumu_pipoca ? Resource.Drawable.mumu_pipoca_claro
        : escura == Resource.Drawable.gato_arquivo ? Resource.Drawable.gato_arquivo_claro
        : escura == Resource.Drawable.gato ? Resource.Drawable.gato_claro
        : escura == Resource.Drawable.logo ? Resource.Drawable.logo_claro
        : null;
}
