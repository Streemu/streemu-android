using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Mais (spec 002): você, o diagnóstico, sobre (a versão e procurar atualização) e sair. Era a aba Conta até o plano 002.
/// </summary>
static partial class Telas
{
    // ================================================================================================ Mais

    /// <summary>Monta a aba Mais. Ex.: <c>Telas.Mais(this, TrocarApelido, ConfirmarSaida, AbrirNovidades)</c></summary>
    public static ScrollView Mais(Context c, Action trocarApelido, Action sair, Action abrirNovidades)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        // em tela larga a coluna fica com largura de leitura, centralizada
        var envelope = Tema.Coluna(c);
        envelope.SetGravity(GravityFlags.CenterHorizontal);
        envelope.AddView(col, new LinearLayout.LayoutParams(Math.Min(Tema.Dp(c, 640), c.Resources!.DisplayMetrics!.WidthPixels), Tema.Ajusta));
        col.AddView(TituloDaTela(c, "Mais"));
        col.AddView(TituloDeSecao(c, "Você"));
        col.AddView(CartaoDoNome(c, trocarApelido));
        col.AddView(TituloDeSecao(c, "Diagnóstico"));
        col.AddView(CartaoDoDiagnostico(c));
        col.AddView(TituloDeSecao(c, "Ao assistir"));
        col.AddView(CartaoDoAvisoDeQualidade(c));
        col.AddView(TituloDeSecao(c, "Sobre"));
        col.AddView(CartaoSobre(c, abrirNovidades));
        col.AddView(BotaoSair(c, sair));
        return Rolagem(c, envelope);
    }

    static View CartaoDoNome(Context c, Action trocarApelido)
    {
        var cartao = Cartao(c);
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 12), Tema.Dp(c, 14));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.Click += (_, _) => trocarApelido();
        var av = new Avatar(c);
        string conta = Sessao.Conta.ContaId ?? "eu";
        av.Mostrar(conta, Sessao.Apelido, Sessao.Avatar(conta));
        linha.AddView(av, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, Tema.Dp(c, 8), 0);
        textos.AddView(Tema.Texto(c, Sessao.Apelido, 18, negrito: true));
        textos.AddView(Tema.Texto(c, "Seu nome em todos os aparelhos · toque para mudar", 14, Tema.TextoApagado));
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        linha.AddView(Tema.Icone(c, Resource.Drawable.ic_seta, Tema.TextoApagado));
        cartao.AddView(linha);
        return cartao;
    }

    static View CartaoDoDiagnostico(Context c)
    {
        var cartao = Cartao(c);
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        linha.SetMinimumHeight(Tema.Dp(c, 64));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        var textos = Tema.Coluna(c);
        textos.AddView(Tema.Texto(c, "Enviar diagnóstico", 16));
        var estado = Tema.Texto(c, "", 13, Tema.TextoApagado);
        textos.AddView(estado);
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        var chave = new Chave(c);
        linha.AddView(chave, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 32)));
        void Mostrar()
        {
            chave.Ligada = Telemetria.Ligado;
            estado.Text = EstadoDoDiagnostico();
        }
        linha.Click += (_, _) => { Telemetria.Ligado = !Telemetria.Ligado; Telemetria.Respondido = true; Mostrar(); };
        Mostrar();
        cartao.AddView(linha);
        var sobre = Tema.Texto(c, Telemetria.TextoDoAviso, 13, Tema.TextoApagado);
        sobre.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 16), Tema.Dp(c, 14));
        cartao.AddView(sobre);
        return cartao;
    }

    static View CartaoDoAvisoDeQualidade(Context c)
    {
        var cartao = Cartao(c);
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        linha.SetMinimumHeight(Tema.Dp(c, 64));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        var textos = Tema.Coluna(c);
        textos.AddView(Tema.Texto(c, "Aviso de qualidade na tela", 16));
        textos.AddView(Tema.Texto(c, "Um sinal amarelo ou vermelho no canto do vídeo quando a rede, o celular ou quem transmite atrapalha. Vale a partir da próxima live.", 13, Tema.TextoApagado));
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        var chave = new Chave(c);
        linha.AddView(chave, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 32)));
        chave.Ligada = Streemu.Proto.Player.AvisoDeQualidade.Ligado(c);
        linha.Click += (_, _) => { bool novo = !Streemu.Proto.Player.AvisoDeQualidade.Ligado(c); Streemu.Proto.Player.AvisoDeQualidade.Definir(c, novo); chave.Ligada = novo; };
        cartao.AddView(linha);
        return cartao;
    }

    static string EstadoDoDiagnostico() =>
        !Telemetria.Ligado ? "Desligado: nada é gravado nem enviado"
        : Telemetria.UltimoErro is { } e ? "Último envio falhou: " + e
        : Telemetria.UltimoOk is { } ok ? $"Ligado · último envio às {ok:HH:mm}" : "Ligado";

    /// <summary>A versão instalada e "Procurar atualização" como uma linha com seta (o botão cinza de largura inteira destoava dos cartões).</summary>
    static View CartaoSobre(Context c, Action abrirNovidades)
    {
        var cartao = Cartao(c);
        cartao.AddView(LinhaSimples(c, $"Streemu para Android · {Atualizacao.VersaoInstalada}"));
        cartao.AddView(Fio(c), Tema.Lp(Tema.Cheio, Tema.Dp(c, 1)));
        cartao.AddView(LinhaComSeta(c, "Novidades", abrirNovidades));
        cartao.AddView(Fio(c), Tema.Lp(Tema.Cheio, Tema.Dp(c, 1)));
        bool ocupado = Atualizacao.Ocupado;
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 12), 0);
        linha.SetMinimumHeight(Tema.Dp(c, 52));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = !ocupado;
        linha.Click += (_, _) => { if (!Atualizacao.Ocupado) Atualizacao.Verificar(manual: true); };
        linha.AddView(Tema.Texto(c, ocupado ? "Procurando atualização..." : "Procurar atualização", 16, ocupado ? Tema.TextoApagado : Tema.CorTexto),
            Tema.Lp(0, Tema.Ajusta, 1));
        if (!ocupado) linha.AddView(Tema.Icone(c, Resource.Drawable.ic_seta, Tema.TextoApagado));
        cartao.AddView(linha);
        return cartao;
    }

    static View Fio(Context c) => new(c) { Background = new Android.Graphics.Drawables.ColorDrawable(Tema.Fio) };

    /// <summary>Uma linha tocável de 52 dp com o texto e a seta (Novidades).</summary>
    static View LinhaComSeta(Context c, string texto, Action tocar)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 12), 0);
        linha.SetMinimumHeight(Tema.Dp(c, 52));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.ContentDescription = texto;
        linha.Click += (_, _) => tocar();
        linha.AddView(Tema.Texto(c, texto, 16), Tema.Lp(0, Tema.Ajusta, 1));
        linha.AddView(Tema.Icone(c, Resource.Drawable.ic_seta, Tema.TextoApagado));
        return linha;
    }

    static View BotaoSair(Context c, Action sair)
    {
        var b = Tema.Botao(c, "Sair da conta", principal: false);
        b.SetTextColor(Tema.Erro);
        b.Background = Tema.Tocavel(c, Tema.Cartao, 14, Tema.Borda);
        b.Click += (_, _) => sair();
        return ComMargemDeCima(c, b, 24);
    }
}
