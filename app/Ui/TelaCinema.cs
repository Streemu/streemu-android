using System.Text.Json.Nodes;
using Android.Content;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Filme;
using JsonSeguro = Streemu.Util.JsonSeguro;

namespace Streemu.Proto.Ui;

/// <summary>
/// A aba Cinema (spec 028 F2a): as sessões que os amigos do grupo anunciaram, cada uma com "Tenho o arquivo". "Baixar" traz o arquivo do anfitrião (F2b); assistir pela transmissão vem depois.
/// </summary>
static partial class Telas
{
    /// <summary>Ex.: <c>Telas.Cinema(this, a => EscolherArquivo(a))</c>; o anúncio é o "filme_aberto" da sala (campos em <see cref="CinemaSala"/>).</summary>
    public static ScrollView Cinema(Context c, Action<JsonObject> tenhoOArquivo, Action<JsonObject> baixar, Action<JsonObject> assistir, Action<JsonObject> parar, Action<JsonObject>? assistirPelaTransmissao = null, Action? criarSessao = null)
    {
        var col = Tema.Coluna(c);
        int m = Margem(c);
        col.SetPadding(m, 0, m, m);
        col.AddView(TituloDaTela(c, "Cinema"));
        var sala = Sessao.Sala;
        if (sala is not { Connected: true } && sala?.Members.Count is null or 0)
        {
            col.AddView(EsperandoSala(c, sala));
            return Rolagem(c, col);
        }
        if (criarSessao != null)
        {
            var criar = Tema.Botao(c, "Criar sessão", principal: false);
            criar.ContentDescription = "Criar uma sessão de Cinema com um vídeo deste celular";
            criar.Click += (_, _) => criarSessao();
            col.AddView(criar, Tema.Lp(Tema.Cheio, Tema.Ajusta, 0, Tema.Dp(c, 4)));
        }
        var sessoes = CinemaSala.Anuncios();
        if (sessoes.Count == 0)
        {
            col.AddView(Vazio(c, Resource.Drawable.mumu_pipoca, "Nenhuma sessão agora", "Quando um amigo abrir um filme no grupo, ele aparece aqui."));
            return Rolagem(c, col);
        }
        col.AddView(TituloDeSecao(c, "Sessões do grupo", sessoes.Count.ToString()));
        foreach (var a in sessoes) col.AddView(CartaoDaSessao(c, a, tenhoOArquivo, baixar, assistir, parar, assistirPelaTransmissao), Tema.Lp(Tema.Cheio, Tema.Ajusta, 0, 0));
        return Rolagem(c, col);
    }

    static View CartaoDaSessao(Context c, JsonObject a, Action<JsonObject> tenhoOArquivo, Action<JsonObject> baixar, Action<JsonObject> assistir, Action<JsonObject> parar, Action<JsonObject>? assistirPelaTransmissao)
    {
        string dono = JsonSeguro.Texto(a["anfitriao"]) ?? "Um amigo", titulo = JsonSeguro.Texto(a["nome"]) ?? "um filme";
        var k = Tema.Coluna(c);
        k.Background = Tema.Forma(c, Tema.Cartao, 16, Tema.Borda);
        k.SetPadding(Tema.Dp(c, 16), Tema.Dp(c, 14), Tema.Dp(c, 16), Tema.Dp(c, 16));
        var t = Tema.Texto(c, $"{dono} vai ver {titulo}", 16, negrito: true);
        k.AddView(t);
        string estado = JsonSeguro.Texto(a["estado"]) switch { "rolando" => "rolando agora", "pausado" => "pausado", _ => "esperando todo mundo ficar pronto" };
        int pessoas = (int)(JsonSeguro.Inteiro(a["pessoas"]) ?? 0);
        var s = Tema.Texto(c, pessoas > 0 ? $"{estado} · {pessoas} na sessão" : estado, 13, Tema.TextoApagado);
        s.SetPadding(0, Tema.Dp(c, 4), 0, Tema.Dp(c, 12));
        k.AddView(s);
        string filme = JsonSeguro.Texto(a["filme"]) ?? "";
        var e = BaixarFilme.De(filme);
        if (e is { Fase: "pronto" })
        {
            var assistirB = Tema.Botao(c, "Assistir");
            assistirB.ContentDescription = $"Assistir {titulo} com o arquivo baixado";
            assistirB.Click += (_, _) => assistir(a);
            k.AddView(assistirB, Tema.Lp(Tema.Cheio, Tema.Ajusta));
            return ComMargemDeCima(c, k, 12);
        }
        if (e is { Ocupado: true })
        {
            string fase = e.Fase switch
            {
                "conferindo" => $"Conferindo o arquivo ({e.Pct}%)...",
                "baixando" => $"Baixando: {e.Pct}% · {e.Taxa / 1e6:N1} MB/s" + (e.EtaS >= 0 ? " · " + Streemu.Filme.Progresso.Faltam(e.EtaS) : ""),
                _ => "Conectando...",
            };
            k.AddView(Tema.Texto(c, fase, 13, Tema.TextoApagado));
            var barra = new ProgressBar(c, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Progress = e.Pct };
            barra.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Tema.Destaque);
            k.AddView(barra, Tema.Lp(Tema.Cheio, Tema.Dp(c, 8), 0, Tema.Dp(c, 8)));
            var parada = Tema.Botao(c, "Parar", principal: false);
            parada.Click += (_, _) => parar(a);
            k.AddView(parada, Tema.Lp(Tema.Cheio, Tema.Ajusta));
            return ComMargemDeCima(c, k, 12);
        }
        if (e?.Mensagem is { Length: > 0 } msg)
        {
            var erro = Tema.Texto(c, msg, 13, Tema.Erro);
            erro.SetPadding(0, 0, 0, Tema.Dp(c, 8));
            k.AddView(erro);
        }
        bool transmitindo = assistirPelaTransmissao != null && a["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var tv) && tv;
        // o anfitrião está transmitindo o arquivo dele (campo "stream" do anúncio): dá para ver sem baixar nada, e é o caminho principal
        if (transmitindo)
        {
            var st = Tema.Botao(c, "Assistir pela transmissão", principal: true);
            st.ContentDescription = $"Assistir {titulo} pela transmissão de {dono}, sem baixar";
            st.Click += (_, _) => assistirPelaTransmissao!(a);
            k.AddView(st, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        }
        int? parado = BaixarFilme.PctParado(c, filme);
        var baixarB = Tema.Botao(c, parado is int p ? $"Continuar ({p}%)" : "Baixar", principal: !transmitindo);
        baixarB.ContentDescription = $"Baixar {titulo} de {dono}";
        baixarB.Click += (_, _) => baixar(a);
        k.AddView(baixarB, Tema.Lp(Tema.Cheio, Tema.Ajusta, 0, transmitindo ? Tema.Dp(c, 8) : 0));
        var b = Tema.Botao(c, "Tenho o arquivo", principal: false);
        b.ContentDescription = $"Entrar na sessão de {dono} com o meu arquivo";
        b.Click += (_, _) => tenhoOArquivo(a);
        k.AddView(b, Tema.Lp(Tema.Cheio, Tema.Ajusta, 0, Tema.Dp(c, 8)));
        return ComMargemDeCima(c, k, 12);
    }
}
