namespace Streemu.Proto.Player;

/// <summary>Um retângulo em pixels dentro da área do player.</summary>
readonly record struct Ret(int X, int Y, int W, int H);

/// <summary>
/// Onde fica cada peça do player (spec 003), como conta pura: sem view, só retângulos, na ordem das peças.
/// Grade: as peças do mesmo tamanho, no número de colunas que deixa cada vídeo maior (a última linha incompleta fica centralizada).
/// Destaque: a principal ocupa a área inteira e as outras ficam em miniaturas numa fileira horizontal no canto de baixo à esquerda,
/// cada uma 16:9 e a fileira com no máximo 1/4 da altura (a mesma ideia da ênfase do Windows, TileLayout.cs).
/// Uso: <c>var r = DisposicaoDoPlayer.Grade(4, 1280, 720, gap: 8);</c>
/// </summary>
static class DisposicaoDoPlayer
{
    public const double Proporcao = 16.0 / 9;
    const double AlturaMaximaDasMiniaturas = 0.25;
    const double LarguraMaximaDaMiniatura = 0.40;   // em pé, uma miniatura sozinha ocupava a tela quase toda

    public static Ret[] Grade(int quantas, int largura, int altura, int gap)
    {
        if (quantas <= 0 || largura <= 0 || altura <= 0) return [];
        int melhor = 1; double melhorLado = -1;
        for (int colunas = 1; colunas <= quantas; colunas++)
        {
            int linhas = (quantas + colunas - 1) / colunas;
            double lado = LadoDoVideo((largura - gap * (colunas - 1)) / (double)colunas, (altura - gap * (linhas - 1)) / (double)linhas);
            if (lado >= melhorLado) { melhorLado = lado; melhor = colunas; }   // empate: mais colunas (janela larga fica lado a lado)
        }
        return Colocar(quantas, melhor, largura, altura, gap);
    }

    public static Ret[] Destaque(int quantas, int principal, int largura, int altura, int gap, int margem, int margemInferior)
    {
        if (quantas <= 0 || largura <= 0 || altura <= 0) return [];
        var r = new Ret[quantas];
        r[principal] = new Ret(0, 0, largura, altura);
        int minis = quantas - 1;
        if (minis == 0) return r;
        int dispoLargura = largura - 2 * margem - gap * (minis - 1);
        int mw = Math.Max(1, (int)Math.Min(Math.Min(altura * AlturaMaximaDasMiniaturas * Proporcao, dispoLargura / (double)minis), largura * LarguraMaximaDaMiniatura));
        int mh = Math.Max(1, (int)Math.Round(mw / Proporcao));
        int y = altura - margemInferior - mh;
        for (int i = 0, k = 0; i < quantas; i++)
            if (i != principal) r[i] = new Ret(margem + k++ * (mw + gap), Math.Max(0, y), mw, mh);
        return r;
    }

    /// <summary>O maior vídeo 16:9 que cabe numa célula do tamanho dado (a largura do vídeo).</summary>
    static double LadoDoVideo(double w, double h) => Math.Min(w, h * Proporcao);

    static Ret[] Colocar(int quantas, int colunas, int largura, int altura, int gap)
    {
        int linhas = (quantas + colunas - 1) / colunas;
        int cw = (largura - gap * (colunas - 1)) / colunas, ch = (altura - gap * (linhas - 1)) / linhas;
        var r = new Ret[quantas];
        for (int i = 0; i < quantas; i++)
        {
            int linha = i / colunas, coluna = i % colunas;
            int naLinha = Math.Min(colunas, quantas - linha * colunas);
            int sobra = (colunas - naLinha) * (cw + gap) / 2;   // a última linha incompleta fica centralizada
            r[i] = new Ret(sobra + coluna * (cw + gap), linha * (ch + gap), cw, ch);
        }
        return r;
    }
}
