using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>
/// Pede uma linha de texto numa caixa no visual do app (cartão da <see cref="Paleta"/>, campo com a borda do tema, botões Cancelar e Salvar do app), no lugar do
/// AlertDialog do Android, que sai com a cara do sistema. Vale para o tema claro e o escuro. A caixa fica aberta com o texto digitado enquanto salvar der erro
/// ou estiver sem rede. Tem de ser chamada da thread da tela.
/// Uso: <c>CaixaDeTexto.Mostrar(this, "Mudar nome", "É como os amigos veem você.", "Seu nome", 24, SalvarNome, () =&gt; _aberta = false);</c>
/// </summary>
static class CaixaDeTexto
{
    const int LarguraMaximaDp = 380;

    /// <param name="aoSalvar">Recebe o texto e devolve null se deu certo (a caixa fecha) ou a frase de erro (a caixa fica aberta e mostra a frase).</param>
    public static void Mostrar(Activity tela, string titulo, string explicacao, string inicial, int maximo, Func<string, Task<string?>> aoSalvar, Action aoFechar)
    {
        var dialogo = new Dialog(tela);
        dialogo.RequestWindowFeature((int)WindowFeatures.NoTitle);
        var campo = Campo(tela, inicial, maximo);
        var erro = Tema.Texto(tela, "", 14, Tema.Erro);
        erro.Visibility = ViewStates.Gone;
        var cancelar = Tema.Botao(tela, "Cancelar", principal: false);
        var salvar = Tema.Botao(tela, "Salvar");
        salvar.Click += async (_, _) => await Salvar(dialogo, campo, erro, salvar, aoSalvar);
        cancelar.Click += (_, _) => dialogo.Dismiss();
        campo.EditorAction += (_, e) => { if (e.ActionId == ImeAction.Done) salvar.PerformClick(); };
        dialogo.SetContentView(Montar(tela, titulo, explicacao, campo, erro, cancelar, salvar));
        dialogo.DismissEvent += (_, _) => aoFechar();
        Exibir(tela, dialogo, campo);
    }

    static void Exibir(Activity tela, Dialog dialogo, EditText campo)
    {
        var janela = dialogo.Window!;
        janela.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        janela.SetSoftInputMode(SoftInput.StateAlwaysVisible | SoftInput.AdjustResize);
        janela.SetLayout(Math.Min(Tema.Dp(tela, LarguraMaximaDp), tela.Resources!.DisplayMetrics!.WidthPixels - Tema.Dp(tela, 32)), ViewGroup.LayoutParams.WrapContent);
        dialogo.SetCanceledOnTouchOutside(true);
        dialogo.Show();
        campo.RequestFocus();
    }

    static View Montar(Activity tela, string titulo, string explicacao, View campo, View erro, View cancelar, View salvar)
    {
        var cartao = Tema.Coluna(tela);
        cartao.Background = Tema.Forma(tela, Tema.Cartao, 20, Tema.Borda);
        cartao.SetPadding(Tema.Dp(tela, 22), Tema.Dp(tela, 22), Tema.Dp(tela, 22), Tema.Dp(tela, 20));
        cartao.AddView(Tema.Texto(tela, titulo, 20, negrito: true));
        var texto = Tema.Texto(tela, explicacao, 14, Tema.TextoApagado);
        texto.SetPadding(0, Tema.Dp(tela, 6), 0, Tema.Dp(tela, 16));
        cartao.AddView(texto);
        cartao.AddView(campo);
        erro.SetPadding(Tema.Dp(tela, 4), Tema.Dp(tela, 8), 0, 0);
        cartao.AddView(erro);
        var botoes = Tema.Linha(tela);
        botoes.AddView(cancelar, Tema.Lp(0, Tema.Ajusta, 1));
        botoes.AddView(new View(tela), Tema.Lp(Tema.Dp(tela, 12), 1));
        botoes.AddView(salvar, Tema.Lp(0, Tema.Ajusta, 1));
        var linha = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        linha.TopMargin = Tema.Dp(tela, 20);
        cartao.AddView(botoes, linha);
        return cartao;
    }

    /// <summary>O campo na pele do app: fundo e borda do tema (a borda ganha a cor de destaque com o foco), texto, dica, cursor e seleção nas cores do tema.</summary>
    static EditText Campo(Context c, string inicial, int maximo)
    {
        var campo = new EditText(c) { Text = inicial };
        campo.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(maximo) });
        campo.SetSingleLine(true);
        campo.SetSelectAllOnFocus(true);
        campo.ImeOptions = ImeAction.Done;
        campo.SetTextColor(Tema.CorTexto);
        campo.SetHintTextColor(Tema.TextoApagado);
        campo.SetTextSize(Android.Util.ComplexUnitType.Sp, 17);
        campo.SetHighlightColor(Color.Argb(110, Tema.Destaque.R, Tema.Destaque.G, Tema.Destaque.B));
        campo.Background = FundoDoCampo(c);
        campo.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 14));
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) campo.TextCursorDrawable = Cursor(c);
        return campo;
    }

    static Drawable FundoDoCampo(Context c)
    {
        var comFoco = Tema.Forma(c, Tema.Campo, 12, Tema.DestaqueSuave);
        comFoco.SetStroke(Tema.Dp(c, 2), Tema.Destaque);
        var lista = new StateListDrawable();
        lista.AddState(new[] { Android.Resource.Attribute.StateFocused }, comFoco);
        lista.AddState(Array.Empty<int>(), Tema.Forma(c, Tema.Campo, 12, Tema.Borda));
        return lista;
    }

    static Drawable Cursor(Context c)
    {
        var cursor = new GradientDrawable();
        cursor.SetColor(Tema.Destaque);
        cursor.SetSize(Tema.Dp(c, 2), Tema.Dp(c, 22));
        return cursor;
    }

    static async Task Salvar(Dialog dialogo, EditText campo, TextView erro, View salvar, Func<string, Task<string?>> aoSalvar)
    {
        salvar.Enabled = false;
        salvar.Alpha = 0.6f;
        string? aviso = await aoSalvar(campo.Text ?? "");
        if (aviso == null) { dialogo.Dismiss(); return; }
        salvar.Enabled = true;
        salvar.Alpha = 1f;
        erro.Text = aviso;
        erro.Visibility = ViewStates.Visible;
    }
}
