using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Transmissao;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Conta: você, o diagnóstico, sobre e sair.
/// </summary>
static partial class Telas
{
    // ================================================================================================ Conta

    public static ScrollView Conta(Context c, Action trocarApelido, Action sair)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        // em tela larga a coluna fica com largura de leitura, centralizada
        var envelope = Tema.Coluna(c);
        envelope.SetGravity(GravityFlags.CenterHorizontal);
        envelope.AddView(col, new LinearLayout.LayoutParams(Math.Min(Tema.Dp(c, 640), c.Resources!.DisplayMetrics!.WidthPixels), Tema.Ajusta));

        col.AddView(TituloDeSecao(c, "Você"));
        var eu = Cartao(c);
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 14));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.Click += (_, _) => trocarApelido();
        var av = new Avatar(c);
        av.Mostrar(Sessao.Conta.ContaId ?? "eu", Sessao.Apelido, null);
        linha.AddView(av, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 52)));
        var textos = Tema.Coluna(c);
        textos.SetPadding(Tema.Dp(c, 14), 0, 0, 0);
        textos.AddView(Tema.Texto(c, Sessao.Apelido, 18, negrito: true));
        textos.AddView(Tema.Texto(c, "Seu nome em todos os aparelhos · toque para mudar", 14, Tema.TextoApagado));
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        eu.AddView(linha);
        col.AddView(eu);

        col.AddView(TituloDeSecao(c, "Diagnóstico"));
        var diag = Cartao(c);
        var linhaDiag = Tema.Linha(c);
        linhaDiag.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        linhaDiag.SetMinimumHeight(Tema.Dp(c, 64));
        linhaDiag.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linhaDiag.Clickable = true;
        var textosDiag = Tema.Coluna(c);
        textosDiag.AddView(Tema.Texto(c, "Enviar diagnóstico", 16));
        var estadoDiag = Tema.Texto(c, "", 13, Tema.TextoApagado);
        textosDiag.AddView(estadoDiag);
        linhaDiag.AddView(textosDiag, Tema.Lp(0, Tema.Ajusta, 1));
        var chave = new Chave(c);
        linhaDiag.AddView(chave, Tema.Lp(Tema.Dp(c, 52), Tema.Dp(c, 32)));
        void Mostrar()
        {
            chave.Ligada = Telemetria.Ligado;
            estadoDiag.Text = !Telemetria.Ligado ? "Desligado: nada é gravado nem enviado"
                            : Telemetria.UltimoErro is { } e ? "Último envio falhou: " + e
                            : Telemetria.UltimoOk is { } ok ? $"Ligado · último envio às {ok:HH:mm}" : "Ligado";
        }
        linhaDiag.Click += (_, _) => { Telemetria.Ligado = !Telemetria.Ligado; Telemetria.Respondido = true; Mostrar(); };
        Mostrar();
        diag.AddView(linhaDiag);
        var sobreDiag = Tema.Texto(c, Telemetria.TextoDoAviso, 13, Tema.TextoApagado);
        sobreDiag.SetPadding(Tema.Dp(c, 16), 0, Tema.Dp(c, 16), Tema.Dp(c, 14));
        diag.AddView(sobreDiag);
        col.AddView(diag);

        col.AddView(TituloDeSecao(c, "Sobre"));
        var sobre = Cartao(c);
        sobre.AddView(LinhaSimples(c, $"Streemu para Android · {Atualizacao.VersaoInstalada}"));
        var procurar = Tema.Botao(c, "Procurar atualização", principal: false);
        procurar.Enabled = !Atualizacao.Ocupado;
        procurar.Click += (_, _) => Atualizacao.Verificar(manual: true);
        sobre.AddView(procurar);
        col.AddView(sobre);

        var sairB = Tema.Botao(c, "Sair da conta", principal: false);
        sairB.SetTextColor(Tema.Erro);
        sairB.Background = Tema.Tocavel(c, Tema.Cartao, 14);
        sairB.Click += (_, _) => sair();
        var lp = Tema.Lp(Tema.Cheio, Tema.Ajusta);
        lp.TopMargin = Tema.Dp(c, 24);
        col.AddView(sairB, lp);
        return Rolagem(c, envelope);
    }
}
