using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Net;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Grupos (spec 002, layout do mockup com o fundo do app): criar e entrar, um cartão por grupo da conta e, embaixo, quem está no grupo aberto
/// (online, offline e atividade recente).
/// </summary>
static partial class Telas
{
    // ================================================================================================ Grupos

    /// <summary>Monta a aba Grupos. Ex.: <c>Telas.Grupos(this, Assistir, criar, entrar, g => Convites.Convidar(this, g), sair)</c></summary>
    public static ScrollView Grupos(Context c, Action<Member> assistir, Action criarGrupo, Action entrarComConvite,
                                    Action<GrupoDaConta> convidar, Action<GrupoDaConta> sairDoGrupo)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        if (Atualizacao.BloqueiaSala && Atualizacao.Disponivel is { } nova)
        {
            col.AddView(AvisoDaAtualizacao(c, nova));
            return Rolagem(c, col);
        }
        col.AddView(TituloDaTela(c, "Grupos"));
        col.AddView(ComMargemDeCima(c, BotoesDeGrupo(c, criarGrupo, entrarComConvite), 8));
        AdicionarGruposDaConta(c, col, convidar, sairDoGrupo);
        if (Sessao.Conta.Grupos.Count == 0) return Rolagem(c, col);   // sem grupo não há sala: os botões acima já dizem o que fazer
        AdicionarPessoasDoGrupo(c, col, assistir);
        return Rolagem(c, col);
    }

    static View CabecalhoDosGrupos(Context c)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 22), 0, Tema.Dp(c, 2));
        linha.AddView(Tema.Texto(c, "Seus grupos", 17, negrito: true), Tema.Lp(0, Tema.Ajusta, 1));
        return linha;
    }

    static void AdicionarGruposDaConta(Context c, LinearLayout col, Action<GrupoDaConta> convidar, Action<GrupoDaConta> sairDoGrupo)
    {
        var grupos = Sessao.Conta.Grupos;
        col.AddView(CabecalhoDosGrupos(c));
        if (grupos.Count == 0)
        {
            var vazio = Cartao(c);
            vazio.AddView(LinhaSimples(c, "Você ainda não está em um grupo. Crie um ou entre com um convite."));
            col.AddView(vazio);
            return;
        }
        foreach (var g in grupos) col.AddView(CartaoDeGrupo(c, g, convidar, sairDoGrupo));
    }

    /// <summary>Um grupo: tocar abre a sala dele; "⋯" convida ou sai. O grupo aberto fica destacado e mostra quem está nele.</summary>
    static View CartaoDeGrupo(Context c, GrupoDaConta g, Action<GrupoDaConta> convidar, Action<GrupoDaConta> sairDoGrupo)
    {
        bool aberto = g.Id == Sessao.GrupoId;
        var k = Tema.Linha(c);
        k.SetPadding(Tema.Dp(c, 14), Tema.Dp(c, 14), Tema.Dp(c, 4), Tema.Dp(c, 14));
        k.Background = Tema.Tocavel(c, Tema.Cartao, 16, aberto ? Tema.DestaqueSuave : Tema.Borda);   // o aberto só ganha a borda clara
        k.Clickable = true;
        k.ContentDescription = aberto ? $"{g.Nome}, aberto agora" : $"Abrir o grupo {g.Nome}";
        k.Click += (_, _) => Sessao.AbrirSala(g.Id);
        k.AddView(IconeEmQuadro(c, Resource.Drawable.ic_grupo), Tema.Lp(Tema.Dp(c, 44), Tema.Dp(c, 44)));
        k.AddView(TextosDoGrupo(c, g, aberto), Tema.Lp(0, Tema.Ajusta, 1));
        k.AddView(BotaoDeOpcoes(c, g, convidar, sairDoGrupo), Tema.Lp(Tema.Dp(c, 48), Tema.Dp(c, 48)));
        return ComMargemDeCima(c, k, 10);
    }

    static View TextosDoGrupo(Context c, GrupoDaConta g, bool aberto)
    {
        var col = Tema.Coluna(c);
        col.SetPadding(Tema.Dp(c, 14), 0, Tema.Dp(c, 4), 0);
        var nome = Tema.Texto(c, g.Nome, 16, negrito: true);
        nome.SetMaxLines(2);
        nome.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        col.AddView(nome);
        if (ResumoDoGrupo(g.Id) is { } resumo) col.AddView(Tema.Texto(c, resumo, 13, Tema.TextoApagado));
        if (aberto && Sessao.Sala is { } sala && sala.Members.Count > 0) col.AddView(ComMargemDeCima(c, AvataresDoGrupo(c, sala), 8));
        return col;
    }

    /// <summary>"6 pessoas · Você é membro", do <c>GET /conta</c>. Sem resposta do servidor (ou antes dela), nada: o cartão fica só com o nome.</summary>
    static string? ResumoDoGrupo(string id)
    {
        if (Sessao.Detalhe(id) is not { } d) return null;
        string papel = d.Dono ? "Você é o dono" : "Você é membro";
        return d.Membros is { } n ? $"{(n == 1 ? "1 pessoa" : $"{n} pessoas")} · {papel}" : papel;
    }

    /// <summary>Até 4 avatares sobrepostos e "+N". Só no grupo aberto: dos outros grupos, o servidor manda só quantos são.</summary>
    static View AvataresDoGrupo(Context c, GroupRoom sala)
    {
        var pessoas = sala.Members.Select(p => (p.Id, p.Nick)).Concat(sala.Ausentes.Select(a => (a.Id, a.Nick))).ToList();
        var linha = Tema.Linha(c);
        int mostrar = Math.Min(4, pessoas.Count);
        for (int i = 0; i < mostrar; i++)
        {
            var av = new Avatar(c);
            av.Mostrar(pessoas[i].Id, pessoas[i].Nick, Sessao.Avatar(pessoas[i].Id));
            linha.AddView(av, LugarDoAvatar(c, i));
        }
        if (pessoas.Count == mostrar) return linha;
        var resto = Tema.Texto(c, $"+{pessoas.Count - mostrar}", 11, negrito: true);
        resto.Gravity = GravityFlags.Center;
        resto.Background = Tema.Forma(c, Tema.Neutro, 13);
        linha.AddView(resto, LugarDoAvatar(c, mostrar));
        return linha;
    }

    static LinearLayout.LayoutParams LugarDoAvatar(Context c, int indice)
    {
        var lp = Tema.Lp(Tema.Dp(c, 26), Tema.Dp(c, 26));
        if (indice > 0) lp.LeftMargin = -Tema.Dp(c, 6);   // sobrepostos, como no mockup
        return lp;
    }

    static View BotaoDeOpcoes(Context c, GrupoDaConta g, Action<GrupoDaConta> convidar, Action<GrupoDaConta> sairDoGrupo)
    {
        var b = new FrameLayout(c) { Clickable = true, ContentDescription = $"Opções do grupo {g.Nome}" };
        b.Background = Tema.Tocavel(c, Color.Transparent, 24);
        b.AddView(Tema.Icone(c, Resource.Drawable.ic_mais, Tema.TextoApagado), new FrameLayout.LayoutParams(Tema.Dp(c, 24), Tema.Dp(c, 24), GravityFlags.Center));
        b.Click += (_, _) =>
        {
            if (c is not Activity tela) return;
            FolhaDeAcoes.Mostrar(tela, g.Nome, new[]
            {
                new AcaoDaFolha(Resource.Drawable.ic_criar_grupo, "Convidar", () => convidar(g)),
                new AcaoDaFolha(Resource.Drawable.ic_sair, "Sair do grupo", () => sairDoGrupo(g), Perigosa: true),
            });
        };
        return b;
    }

    // ------------------------------------------------------------------------------------ quem está no grupo aberto

    static void AdicionarPessoasDoGrupo(Context c, LinearLayout col, Action<Member> assistir)
    {
        var sala = Sessao.Sala;
        if (sala is not { Connected: true } && sala?.Members.Count is null or 0) { col.AddView(EsperandoSala(c, sala)); return; }
        AdicionarOnline(c, col, sala!, assistir);
        AdicionarOffline(c, col, sala!);
        AdicionarAtividade(c, col, sala!);
    }

    static void AdicionarOnline(Context c, LinearLayout col, GroupRoom sala, Action<Member> assistir)
    {
        var online = sala.Friends.OrderByDescending(f => f.Live != null).ThenBy(f => f.Nick, StringComparer.CurrentCultureIgnoreCase).ToList();
        col.AddView(TituloDeSecao(c, "Online", online.Count.ToString()));
        var cartao = Cartao(c);
        if (online.Count == 0) cartao.AddView(LinhaSimples(c, "Ninguém online agora além de você."));
        foreach (var f in online)
            cartao.AddView(Pessoa(c, f.Id, f.Nick, f.Live is { } l ? $"Ao vivo{(l.Game.Length > 0 ? ": " + l.Game : "")}" : "Online",
                f.Live != null ? Tema.AoVivo : Tema.Ok, f.Live != null ? () => assistir(f) : null));
        col.AddView(cartao);
    }

    static void AdicionarOffline(Context c, LinearLayout col, GroupRoom sala)
    {
        if (sala.Ausentes.Count == 0) return;
        col.AddView(TituloDeSecao(c, "Offline", sala.Ausentes.Count.ToString()));
        var off = Cartao(c);
        foreach (var a in sala.Ausentes.OrderBy(a => a.Nick, StringComparer.CurrentCultureIgnoreCase))
        {
            var p = Pessoa(c, a.Id, a.Nick, "Offline", null, null);
            p.Alpha = 0.6f;
            off.AddView(p);
        }
        col.AddView(off);
    }

    static void AdicionarAtividade(Context c, LinearLayout col, GroupRoom sala)
    {
        var frases = FrasesDeAtividade.Montar(sala.Atividades, (_, apelido) => apelido, DateTimeOffset.Now, meuId: sala.MyId);
        var ativ = Cartao(c);
        if (frases.Count == 0) ativ.AddView(LinhaSimples(c, FrasesDeAtividade.Vazio));
        foreach (var f in frases) ativ.AddView(LinhaDeAtividade(c, f.Tipo, f.Texto, f.Quando));
        ativ.Visibility = _atividadeAberta ? ViewStates.Visible : ViewStates.Gone;
        col.AddView(CabecalhoDaAtividade(c, ativ));
        col.AddView(ativ);
    }

    /// <summary>Lembra se a lista está aberta entre redesenhos da aba: o padrão é fechada, porque a lista longa empurrava o resto da tela.</summary>
    static bool _atividadeAberta;

    /// <summary>"Atividade recente" com uma seta: tocar abre ou fecha a lista no lugar, sem redesenhar a tela.</summary>
    static View CabecalhoDaAtividade(Context c, View lista)
    {
        var linha = Tema.Linha(c);
        linha.SetPadding(0, Tema.Dp(c, 20), 0, Tema.Dp(c, 10));
        linha.AddView(Tema.Texto(c, "Atividade recente", 14, Tema.DestaqueSuave, negrito: true), Tema.Lp(0, Tema.Ajusta, 1));
        var seta = Tema.Icone(c, Resource.Drawable.ic_seta_baixo, Tema.DestaqueSuave, 24);
        seta.Rotation = _atividadeAberta ? 180f : 0f;
        linha.AddView(seta, Tema.Lp(Tema.Dp(c, 24), Tema.Dp(c, 24)));
        linha.Clickable = true;
        linha.ContentDescription = "Mostrar ou esconder a atividade recente";
        linha.Click += (_, _) =>
        {
            _atividadeAberta = !_atividadeAberta;
            lista.Visibility = _atividadeAberta ? ViewStates.Visible : ViewStates.Gone;
            seta.Rotation = _atividadeAberta ? 180f : 0f;
        };
        return linha;
    }

    static View LinhaDeAtividade(Context c, string tipo, string texto, string quando)
    {
        var linha = Tema.Linha(c, GravityFlags.Top);
        linha.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 12), Tema.Dp(c, 16), Tema.Dp(c, 12));
        var ponto = new View(c) { Background = Tema.Forma(c, tipo is "live" ? Tema.AoVivo : tipo is "live_fim" ? Tema.DestaqueSuave : Tema.Neutro, 4) };
        var lpP = Tema.Lp(Tema.Dp(c, 8), Tema.Dp(c, 8));
        lpP.TopMargin = Tema.Dp(c, 7);
        linha.AddView(ponto, lpP);
        var t = Tema.Texto(c, texto, 15);
        t.SetPadding(Tema.Dp(c, 12), 0, Tema.Dp(c, 8), 0);
        linha.AddView(t, Tema.Lp(0, Tema.Ajusta, 1));
        linha.AddView(Tema.Texto(c, quando, 13, Tema.TextoApagado));
        return linha;
    }
}
