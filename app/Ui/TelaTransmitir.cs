using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Transmissao;

namespace Streemu.Proto.Ui;

/// <summary>
/// A tela Transmitir (spec 002, tarefa 4 do plano): abre pelo cartão do Início, no lugar dele, com a barra de baixo à vista como no mockup.
/// Só o que o celular faz de verdade: 720p, 1080p e 1080p alta, sempre a 60 fps; quem escolhe entre um app e a tela inteira é o diálogo do Android.
/// </summary>
static partial class Telas
{
    // ================================================================================================ Transmitir

    /// <summary>Ex.: <c>Telas.Transmitir(this, FecharTelaTransmitir, EscolherQualidade, EscolherSom, ComecarTransmissao)</c></summary>
    public static ScrollView Transmitir(Context c, Action voltar, Action<int> escolherQualidade, Action<bool> escolherSom, Action comecar)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        col.AddView(TituloComVoltar(c, "Transmitir", voltar));
        col.AddView(SecaoDeQualidade(c, escolherQualidade));
        col.AddView(SecaoDoSom(c, escolherSom));
        if (ServicoTransmissao.Erro is { } erro) col.AddView(AvisoDaTransmissao(c, erro, Tema.Erro));
        bool conectada = Sessao.Sala is { Connected: true };
        if (!conectada) col.AddView(AvisoDaTransmissao(c, "Conecte-se ao grupo para transmitir.", Tema.Aviso));
        col.AddView(BotaoComecar(c, conectada && !ServicoTransmissao.Preparando, comecar));
        return Rolagem(c, col);
    }

    static View TituloComVoltar(Context c, string titulo, Action voltar)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 4), 0, 0);
        var botao = new FrameLayout(c) { Clickable = true, ContentDescription = "Voltar" };
        botao.Background = Tema.Tocavel(c, Color.Transparent, 24);
        botao.AddView(Tema.Icone(c, Resource.Drawable.ic_voltar, Tema.CorTexto), new FrameLayout.LayoutParams(Tema.Dp(c, 24), Tema.Dp(c, 24), GravityFlags.Center));
        botao.Click += (_, _) => voltar();
        var lpB = Tema.Lp(Tema.Dp(c, 48), Tema.Dp(c, 48));
        lpB.LeftMargin = -Tema.Dp(c, 12);   // a seta alinha com a margem do texto, e o alvo de toque continua com 48 dp
        linha.AddView(botao, lpB);
        linha.AddView(TituloDaTela(c, titulo), Tema.Lp(0, Tema.Ajusta, 1));
        return linha;
    }

    /// <summary>Um bloco da tela: o nome em cima e o conteúdo num cartão.</summary>
    static LinearLayout SecaoDeTransmitir(Context c, string nome)
    {
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16, Tema.Borda);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 16));
        var t = Tema.Texto(c, nome, 16, negrito: true);
        t.SetPadding(0, 0, 0, Tema.Dp(c, 10));
        k.AddView(t);
        ComMargemDeCima(c, k, 16);
        return k;
    }

    static View SecaoDeQualidade(Context c, Action<int> escolherQualidade)
    {
        var k = SecaoDeTransmitir(c, "Qualidade");
        int escolhida = Qualidade.IndiceEscolhido(c);
        var pilulas = Tema.Linha(c);
        for (int i = 0; i < Qualidade.Todas.Length; i++)
        {
            int indice = i;
            var lp = Tema.Lp(0, Tema.Ajusta, 1);
            if (i > 0) lp.LeftMargin = Tema.Dp(c, 8);
            pilulas.AddView(PilulaDeEscolha(c, Qualidade.Todas[i].Nome, i == escolhida, () => escolherQualidade(indice)), lp);
        }
        k.AddView(pilulas, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        var nota = Tema.Texto(c, "Sempre a 60 quadros por segundo.", 13, Tema.TextoApagado);
        nota.SetPadding(0, Tema.Dp(c, 10), 0, 0);
        k.AddView(nota);
        return k;
    }

    /// <summary>Uma escolha entre poucas, lado a lado (qualidade, som): a marcada fica com o fundo e a borda de destaque.</summary>
    static View PilulaDeEscolha(Context c, string texto, bool marcada, Action escolher)
    {
        var p = Tema.Texto(c, texto, 14, marcada ? Tema.CorTexto : Tema.TextoApagado, negrito: true);
        p.Gravity = GravityFlags.Center;
        p.SetMinHeight(Tema.Dp(c, 44));
        p.Background = Tema.Tocavel(c, marcada ? Tema.Selecionado : Tema.Campo, 12, marcada ? Tema.DestaqueSuave : null);
        p.Clickable = true;
        p.Focusable = true;
        p.Selected = marcada;
        p.Click += (_, _) => escolher();
        return p;
    }

    /// <summary>
    /// "Som do celular" (o som dos apps, se o Android der a permissão de áudio) ou "Sem som" (só a imagem, sem pedir permissão nenhuma).
    /// O microfone nunca entra: a voz fica na call.
    /// </summary>
    static View SecaoDoSom(Context c, Action<bool> escolherSom)
    {
        var k = SecaoDeTransmitir(c, "Som");
        bool ligado = SomDaTransmissao.Ligado(c);
        var pilulas = Tema.Linha(c);
        pilulas.AddView(PilulaDeEscolha(c, "Som do celular", ligado, () => escolherSom(true)), Tema.Lp(0, Tema.Ajusta, 1));
        var lp = Tema.Lp(0, Tema.Ajusta, 1);
        lp.LeftMargin = Tema.Dp(c, 8);
        pilulas.AddView(PilulaDeEscolha(c, "Sem som", !ligado, () => escolherSom(false)), lp);
        k.AddView(pilulas, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        string nota = ligado ? "O Android pede a permissão de áudio. Sem ela, a live sai só com imagem." : "A live sai só com imagem.";
        var t = Tema.Texto(c, nota, 13, Tema.TextoApagado);
        t.SetPadding(0, Tema.Dp(c, 10), 0, 0);
        k.AddView(t);
        return k;
    }

    static View AvisoDaTransmissao(Context c, string texto, Color cor)
    {
        var t = Tema.Texto(c, texto, 14, cor);
        t.SetPadding(Tema.Dp(c, 4), Tema.Dp(c, 14), Tema.Dp(c, 4), 0);
        return t;
    }

    static View BotaoComecar(Context c, bool pode, Action comecar)
    {
        var b = Tema.Botao(c, ServicoTransmissao.Preparando ? "Abrindo a transmissão..." : "Começar a transmitir");
        b.Enabled = pode;
        b.Alpha = pode ? 1f : 0.5f;
        b.Click += (_, _) => { if (pode) comecar(); };
        return ComMargemDeCima(c, b, 20);
    }
}
