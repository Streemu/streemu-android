using System.Globalization;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>
/// A tela Novidades (spec 002, tarefa 8 do plano): abre pela aba Mais, no lugar dela, com o que mudou em cada versão do app Android.
/// "Não lançado" só aparece no build de teste: no publicado, quem entrega já renomeou a seção para a versão.
/// </summary>
static partial class Telas
{
    // ================================================================================================ Novidades

    static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>Ex.: <c>Telas.Novidades(this, FecharSubtela, Novidades.Ler(), Atualizacao.VersaoInstalada, mostrarNaoLancado: false)</c></summary>
    public static ScrollView Novidades(Context c, Action voltar, IReadOnlyList<VersaoDeNovidades> versoes, string instalada, bool mostrarNaoLancado)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        col.AddView(TituloComVoltar(c, "Novidades", voltar));
        var visiveis = versoes.Where(v => mostrarNaoLancado || !v.NaoLancada).ToList();
        if (!visiveis.Any(v => v.Versao == instalada)) col.AddView(SemNovidadesDaVersao(c, instalada));
        foreach (var v in visiveis) AdicionarVersao(c, col, v, v.Versao == instalada);
        return Rolagem(c, col);
    }

    /// <summary>A versão instalada sem seção no arquivo (alguém entregou sem renomear "Não lançado"): diz isso, sem quebrar a tela.</summary>
    static View SemNovidadesDaVersao(Context c, string instalada)
    {
        var k = Cartao(c);
        k.AddView(LinhaSimples(c, $"Você está na versão {instalada}. Sem novidades registradas para esta versão."));
        return ComMargemDeCima(c, k, 12);
    }

    static void AdicionarVersao(Context c, LinearLayout col, VersaoDeNovidades v, bool instalada)
    {
        col.AddView(CabecalhoDaVersao(c, v, instalada));
        var k = Cartao(c);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 14));
        if (v.Resumo is { Length: > 0 } resumo) k.AddView(Tema.Texto(c, resumo, 15));
        foreach (var g in v.Grupos) AdicionarGrupo(c, k, g);
        col.AddView(k);
    }

    static View CabecalhoDaVersao(Context c, VersaoDeNovidades v, bool instalada)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 22), 0, Tema.Dp(c, 10));
        linha.AddView(Tema.Texto(c, v.NaoLancada ? "Ainda não lançado" : v.Versao, 17, negrito: true));
        if (v.Data is { } d)
        {
            var data = Tema.Texto(c, d.ToString("d 'de' MMM", PtBr), 13, Tema.TextoApagado);
            data.SetPadding(Tema.Dp(c, 10), 0, 0, 0);
            linha.AddView(data);
        }
        linha.AddView(new View(c), Tema.Lp(0, 1, 1));
        if (instalada) linha.AddView(Tema.Selo(c, "SUA VERSÃO", Tema.Selecionado, Tema.DestaqueSuave));
        return linha;
    }

    static void AdicionarGrupo(Context c, LinearLayout cartao, GrupoDeNovidades g)
    {
        var tipo = Tema.Texto(c, g.Tipo.ToUpper(PtBr), 12, Tema.DestaqueSuave, negrito: true);
        tipo.LetterSpacing = 0.08f;
        tipo.SetPadding(0, Tema.Dp(c, 12), 0, Tema.Dp(c, 2));
        cartao.AddView(tipo);
        foreach (var item in g.Itens) cartao.AddView(ItemDaNovidade(c, item));
    }

    /// <summary>No arquivo o detalhe segue os dois-pontos ("**Título:** detalhe"); na tela ele fica numa linha só dele e começa com maiúscula.</summary>
    static string Maiuscula(string texto) => char.ToUpper(texto[0], PtBr) + texto[1..];

    static View ItemDaNovidade(Context c, ItemDeNovidade item)
    {
        var col = Tema.Coluna(c);
        col.SetPadding(0, Tema.Dp(c, 8), 0, 0);
        col.AddView(Tema.Texto(c, item.Titulo, 15, negrito: true));
        if (item.Detalhe.Length > 0) col.AddView(Tema.Texto(c, Maiuscula(item.Detalhe), 14, Tema.TextoApagado));
        return col;
    }
}
