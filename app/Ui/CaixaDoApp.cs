using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>Um botão da <see cref="CaixaDoApp"/>. <see cref="Perigoso"/> pinta de vermelho (sair, remover); <see cref="Principal"/> usa a cor de destaque.</summary>
sealed record BotaoDaCaixa(string Texto, Action? Executar = null, bool Principal = false, bool Perigoso = false);

/// <summary>
/// A caixa de aviso do app: o cartão da <see cref="Paleta"/> no meio da tela, com título, texto e botões do app, no lugar do AlertDialog do Android, que sai com a
/// cara do sistema. Vale para o tema claro e o escuro. A caixa de texto (<see cref="CaixaDeTexto"/>) usa o mesmo cartão. Tem de ser chamada da thread da tela.
/// Uso: <c>CaixaDoApp.Confirmar(this, "Sair da conta?", "Para voltar, é só entrar de novo.", "Sair", Sair, perigosa: true);</c>
/// </summary>
static class CaixaDoApp
{
    const int LarguraMaximaDp = 380;

    /// <summary>
    /// Mostra a caixa. Tocar num botão executa a ação e fecha. <paramref name="aoFechar"/> roda quando a caixa some, por botão, toque fora ou voltar.
    /// Com mais de dois botões (ou <paramref name="empilhar"/>) eles ficam um embaixo do outro.
    /// </summary>
    public static Dialog Mostrar(Activity tela, string titulo, string? texto, IReadOnlyList<BotaoDaCaixa> botoes, Action? aoFechar = null, bool selecionavel = false, bool empilhar = false)
    {
        var dialogo = new Dialog(tela);
        dialogo.RequestWindowFeature((int)WindowFeatures.NoTitle);
        var cartao = Cartao(tela, titulo, texto, selecionavel);
        var views = botoes.Select(b => (View)Botao(tela, b, dialogo)).ToList();
        cartao.AddView(LinhaDeBotoes(tela, views, empilhar || views.Count > 2), MargemDeCima(tela, 20));
        dialogo.SetContentView(cartao);
        if (aoFechar != null) dialogo.DismissEvent += (_, _) => aoFechar();
        Exibir(tela, dialogo, teclado: false);
        return dialogo;
    }

    /// <summary>
    /// Pergunta sim ou não. <paramref name="aoRecusar"/> roda quando a pessoa recusa (Cancelar, toque fora ou voltar); fechar a caixa por código não conta.
    /// Ex.: <c>CaixaDoApp.Confirmar(this, "Sair do grupo?", "Para voltar, vai precisar de um convite.", "Sair", () =&gt; Sair(g), perigosa: true);</c>
    /// </summary>
    public static Dialog Confirmar(Activity tela, string titulo, string texto, string rotuloConfirmar, Action aoConfirmar, bool perigosa = false,
        string rotuloCancelar = "Cancelar", Action? aoRecusar = null)
    {
        bool decidiu = false;
        var botoes = new[]
        {
            new BotaoDaCaixa(rotuloCancelar, () => { decidiu = true; aoRecusar?.Invoke(); }),
            new BotaoDaCaixa(rotuloConfirmar, () => { decidiu = true; aoConfirmar(); }, Principal: !perigosa, Perigoso: perigosa),
        };
        var dialogo = Mostrar(tela, titulo, texto, botoes, empilhar: rotuloConfirmar.Length > 14);   // rótulo longo não cabe em meia largura
        // toque fora e voltar são "cancelar"; fechar por código (a tela girou ou foi destruída) não é uma decisão da pessoa
        dialogo.CancelEvent += (_, _) => { if (!decidiu) aoRecusar?.Invoke(); };
        return dialogo;
    }

    /// <summary>O cartão com título e texto: cantos redondos e o fio da borda, a mesma pele dos cartões do app.</summary>
    public static LinearLayout Cartao(Activity tela, string titulo, string? texto, bool selecionavel = false)
    {
        var cartao = Tema.Coluna(tela);
        cartao.Background = Tema.Forma(tela, Tema.Cartao, 20, Tema.Borda);
        cartao.SetPadding(Tema.Dp(tela, 22), Tema.Dp(tela, 22), Tema.Dp(tela, 22), Tema.Dp(tela, 20));
        cartao.AddView(Tema.Texto(tela, titulo, 20, negrito: true));
        if (texto == null) return cartao;
        var corpo = Tema.Texto(tela, texto, 14, Tema.TextoApagado);
        corpo.SetPadding(0, Tema.Dp(tela, 6), 0, 0);
        if (selecionavel) corpo.SetTextIsSelectable(true);
        cartao.AddView(corpo);
        return cartao;
    }

    /// <summary>Os botões lado a lado (partes iguais) ou, empilhados, um embaixo do outro com a ação principal por último.</summary>
    public static LinearLayout LinhaDeBotoes(Activity tela, IReadOnlyList<View> botoes, bool empilhar = false)
    {
        var linha = empilhar ? Tema.Coluna(tela) : Tema.Linha(tela);
        for (int i = 0; i < botoes.Count; i++)
        {
            if (i > 0) linha.AddView(new View(tela), empilhar ? Tema.Lp(1, Tema.Dp(tela, 10)) : Tema.Lp(Tema.Dp(tela, 12), 1));
            linha.AddView(botoes[i], empilhar ? Tema.Lp(Tema.Cheio, Tema.Ajusta) : Tema.Lp(0, Tema.Ajusta, 1));
        }
        return linha;
    }

    public static LinearLayout.LayoutParams MargemDeCima(Activity tela, int dp)
    {
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(tela, dp);
        return lp;
    }

    /// <summary>Tira o fundo da janela do sistema, centraliza na largura de leitura e mostra. Com <paramref name="teclado"/>, abre o teclado e a caixa sobe com ele.</summary>
    public static void Exibir(Activity tela, Dialog dialogo, bool teclado)
    {
        var janela = dialogo.Window!;
        janela.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        if (teclado) janela.SetSoftInputMode(SoftInput.StateAlwaysVisible | SoftInput.AdjustResize);
        janela.SetLayout(Math.Min(Tema.Dp(tela, LarguraMaximaDp), tela.Resources!.DisplayMetrics!.WidthPixels - Tema.Dp(tela, 32)), ViewGroup.LayoutParams.WrapContent);
        dialogo.SetCanceledOnTouchOutside(true);
        dialogo.Show();
    }

    static TextView Botao(Activity tela, BotaoDaCaixa b, Dialog dialogo)
    {
        var botao = Tema.Botao(tela, b.Texto, b.Principal || b.Perigoso);
        if (b.Perigoso) botao.Background = Tema.Tocavel(tela, Tema.Erro, 14);
        botao.Click += (_, _) => { b.Executar?.Invoke(); dialogo.Dismiss(); };
        return botao;
    }
}
