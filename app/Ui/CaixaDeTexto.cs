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
/// Pede uma linha de texto numa caixa no visual do app (o cartão da <see cref="CaixaDoApp"/>, campo com a borda do tema, botões Cancelar e Salvar do app), no lugar
/// do AlertDialog do Android, que sai com a cara do sistema. Vale para o tema claro e o escuro. A caixa fica aberta com o texto digitado enquanto salvar der erro
/// ou estiver sem rede. Tem de ser chamada da thread da tela.
/// Uso: <c>CaixaDeTexto.Mostrar(this, "Mudar nome", "É como os amigos veem você.", "Seu nome", 24, SalvarNome, () =&gt; _aberta = false);</c>
/// </summary>
static class CaixaDeTexto
{
    /// <param name="aoSalvar">Recebe o texto e devolve null se deu certo (a caixa fecha) ou a frase de erro (a caixa fica aberta e mostra a frase).</param>
    /// <param name="rotuloSalvar">O texto do botão que confirma ("Salvar", "Entrar", "Criar"); <paramref name="rotuloOcupado"/> aparece nele enquanto espera.</param>
    /// <param name="dica">Texto cinza do campo vazio (ex.: "Cole o link ou o código").</param>
    /// <param name="semSugestoes">Sem sugestões do teclado, para códigos e links.</param>
    public static Dialog Mostrar(Activity tela, string titulo, string explicacao, string inicial, int maximo, Func<string, Task<string?>> aoSalvar, Action aoFechar,
        string rotuloSalvar = "Salvar", string rotuloOcupado = "Salvando...", string? dica = null, bool semSugestoes = false)
    {
        var dialogo = new Dialog(tela);
        dialogo.RequestWindowFeature((int)WindowFeatures.NoTitle);
        var campo = Campo(tela, inicial, maximo, dica, semSugestoes);
        var erro = Tema.Texto(tela, "", 14, Tema.Erro);
        erro.Visibility = ViewStates.Gone;
        erro.SetPadding(Tema.Dp(tela, 4), Tema.Dp(tela, 8), 0, 0);
        var cancelar = Tema.Botao(tela, "Cancelar", principal: false);
        var salvar = Tema.Botao(tela, rotuloSalvar);
        salvar.Click += async (_, _) => await Salvar(dialogo, campo, erro, salvar, aoSalvar, rotuloSalvar, rotuloOcupado);
        cancelar.Click += (_, _) => dialogo.Dismiss();
        campo.EditorAction += (_, e) => { if (e.ActionId == ImeAction.Done) salvar.PerformClick(); };
        var cartao = CaixaDoApp.Cartao(tela, titulo, explicacao);
        cartao.AddView(campo, CaixaDoApp.MargemDeCima(tela, 16));
        cartao.AddView(erro);
        cartao.AddView(CaixaDoApp.LinhaDeBotoes(tela, new View[] { cancelar, salvar }), CaixaDoApp.MargemDeCima(tela, 20));
        dialogo.SetContentView(cartao);
        dialogo.DismissEvent += (_, _) => aoFechar();
        CaixaDoApp.Exibir(tela, dialogo, teclado: true);
        campo.RequestFocus();
        return dialogo;
    }

    /// <summary>O campo na pele do app: fundo e borda do tema (a borda ganha a cor de destaque com o foco), texto, dica, cursor e seleção nas cores do tema.</summary>
    static EditText Campo(Context c, string inicial, int maximo, string? dica, bool semSugestoes)
    {
        var campo = new EditText(c) { Text = inicial };
        if (dica != null) campo.Hint = dica;
        campo.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(maximo) });
        campo.SetSingleLine(true);
        if (semSugestoes) campo.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
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

    static async Task Salvar(Dialog dialogo, EditText campo, TextView erro, TextView salvar, Func<string, Task<string?>> aoSalvar, string rotulo, string rotuloOcupado)
    {
        if (!salvar.Enabled) return;   // o botão do teclado e o da caixa não salvam duas vezes
        salvar.Enabled = false;
        salvar.Alpha = 0.6f;
        salvar.Text = rotuloOcupado;
        string? aviso = await aoSalvar(campo.Text ?? "");
        if (aviso == null) { dialogo.Dismiss(); return; }
        salvar.Enabled = true;
        salvar.Alpha = 1f;
        salvar.Text = rotulo;
        erro.Text = aviso;
        erro.Visibility = ViewStates.Visible;
    }
}
