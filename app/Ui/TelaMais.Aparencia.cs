using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace Streemu.Proto.Ui;

/// <summary>A seção Aparência da aba Mais: o botão sol/lua (claro ou escuro) e a opção de seguir o tema do celular.</summary>
static partial class Telas
{
    /// <summary>
    /// Duas linhas: "Tema" com o botão sol/lua e "Automático" com uma chave. Tocar no sol/lua escolhe o tema oposto ao de agora e desliga o Automático
    /// (com ele ligado, o botão mostra o tema que o celular está usando). Desligar o Automático fixa o tema de agora. Ex.: <c>col.AddView(CartaoDaAparencia(c))</c>
    /// </summary>
    static View CartaoDaAparencia(Context c)
    {
        var cartao = Cartao(c);
        var botao = new BotaoDeTema(c);
        var estadoDoTema = Tema.Texto(c, "", 13, Tema.TextoApagado);
        var chave = new Chave(c);
        void Mostrar()
        {
            var modo = PreferenciaDeTema.Ler(c);
            botao.Claro = Tema.Atual.Claro;
            chave.Ligada = modo == ModoDeTema.Automatico;
            estadoDoTema.Text = TextoDoTema(modo);
        }
        void TrocarTema() => Escolher(c, Tema.Atual.Claro ? ModoDeTema.Escuro : ModoDeTema.Claro, Mostrar);
        void AlternarAutomatico() => Escolher(c, ModoDaChaveAutomatica(PreferenciaDeTema.Ler(c)), Mostrar);
        cartao.AddView(LinhaDaAparencia(c, "Tema", estadoDoTema, botao, 64, TrocarTema));
        cartao.AddView(Fio(c), Tema.Lp(Tema.Cheio, Tema.Dp(c, 1)));
        cartao.AddView(LinhaDaAparencia(c, "Automático", Tema.Texto(c, "Segue o tema do celular", 13, Tema.TextoApagado), chave, 52, AlternarAutomatico));
        Mostrar();
        return cartao;
    }

    /// <summary>Ligar o Automático vale já; desligar fixa o tema que está na tela, sem a tela mudar.</summary>
    static ModoDeTema ModoDaChaveAutomatica(ModoDeTema atual) =>
        atual == ModoDeTema.Automatico ? (Tema.Atual.Claro ? ModoDeTema.Claro : ModoDeTema.Escuro) : ModoDeTema.Automatico;

    static string TextoDoTema(ModoDeTema modo) =>
        modo == ModoDeTema.Automatico ? $"Automático · agora {(Tema.Atual.Claro ? "claro" : "escuro")}" : Tema.Atual.Claro ? "Claro" : "Escuro";

    /// <summary>Grava a escolha. Se o tema que vale muda, recria a tela (é assim que o Android reaplica estilo, barras e cores); senão, só redesenha o cartão.</summary>
    static void Escolher(Context c, ModoDeTema modo, Action redesenharCartao)
    {
        bool claroAntes = Tema.Atual.Claro;
        PreferenciaDeTema.Gravar(c, modo);
        if (PreferenciaDeTema.Resolver(modo, c) != claroAntes && c is Activity tela) { tela.Recreate(); return; }
        redesenharCartao();
    }

    static View LinhaDaAparencia(Context c, string titulo, TextView subtitulo, View controle, int larguraDp, Action tocar)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        linha.SetMinimumHeight(Tema.Dp(c, 64));
        linha.Background = Tema.Tocavel(c, Tema.Cartao, 0);
        linha.Clickable = true;
        linha.Click += (_, _) => tocar();
        var textos = Tema.Coluna(c);
        textos.AddView(Tema.Texto(c, titulo, 16));
        textos.AddView(subtitulo);
        linha.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        linha.AddView(controle, Tema.Lp(Tema.Dp(c, larguraDp), Tema.Dp(c, 32)));
        return linha;
    }
}
