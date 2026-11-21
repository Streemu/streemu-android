using Android.Content;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>
/// Cinema e Arquivos antes de existirem no celular (spec 002, decisão 1): só o Mumu e o que vem, sem nenhum botão que leve a algo que não funciona.
/// </summary>
static partial class Telas
{
    /// <summary>Ex.: <c>Telas.EmBreve(this, Resource.Drawable.mumu_pipoca, "Cinema em breve", "Assistir filmes junto com o grupo.")</c></summary>
    public static ScrollView EmBreve(Context c, int imagem, string titulo, string texto) => Rolagem(c, Vazio(c, imagem, titulo, texto));
}
