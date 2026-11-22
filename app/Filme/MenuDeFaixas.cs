using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using static Streemu.Proto.Filme.PlayerFilme;
using IdentidadeDeAudio = Streemu.Filme.IdentidadeDeAudio;

namespace Streemu.Proto.Filme;

/// <summary>
/// "Áudio e legendas" no player do filme: lista as faixas do arquivo (o que o ExoPlayer leu) e troca a que a pessoa tocar. Com a transmissão do filme ligada, trocar o áudio também troca a
/// faixa que o envio manda (<see cref="CopiaDoFilme.TrocarAudio"/>), e quem assiste pelo PC ou por outro celular passa a ouvir a mesma. Legenda é só do player daqui: a transmissão não a leva.
/// </summary>
static class MenuDeFaixas
{
    /// <param name="copia">O envio em cópia direta, se houver; troca de áudio vai por ele para os espectadores.</param>
    /// <param name="avisar">Mostra um texto curto na tela (motivo de uma troca recusada).</param>
    public static async Task MostrarAsync(Activity tela, PlayerFilme player, CopiaDoFilme? copia, Action<string> avisar)
    {
        var (audios, legendas, ligada) = await player.FaixasAsync();
        tela.RunOnUiThread(() => Abrir(tela, player, copia, avisar, audios, legendas, ligada));
    }

    static void Abrir(Activity tela, PlayerFilme player, CopiaDoFilme? copia, Action<string> avisar, List<FaixaDoPlayer> audios, List<FaixaDoPlayer> legendas, bool ligada)
    {
        if (tela.IsFinishing || tela.IsDestroyed) return;
        var raiz = new LinearLayout(tela) { Orientation = Orientation.Vertical };
        raiz.SetPadding(Dp(tela, 20), Dp(tela, 8), Dp(tela, 20), Dp(tela, 8));
        var rolagem = new ScrollView(tela);
        rolagem.AddView(raiz);
        var dialogo = new AlertDialog.Builder(tela).SetTitle("Áudio e legendas")!.SetView(rolagem)!.SetPositiveButton("Fechar", (_, _) => { })!.Create()!;
        Secao(tela, raiz, "Áudio", audios.Select(a => (a.Rotulo, a.Selecionada)).ToList(),
            n => TrocarAudio(player, copia, avisar, audios[n]), dialogo, "Este arquivo só tem uma faixa de áudio.");
        bool temLegenda = legendas.Count > 0;
        var itens = new List<(string, bool)> { ("Desligadas", !ligada) };
        itens.AddRange(legendas.Select(l => (l.Rotulo, l.Selecionada && ligada)));
        Secao(tela, raiz, "Legendas (só aqui; a transmissão não leva)", temLegenda ? itens : new List<(string, bool)>(),
            n => player.SelecionarLegenda(n == 0 ? null : n - 1), dialogo, "Este arquivo não tem legenda embutida.");
        dialogo.Show();
    }

    /// <summary>Uma lista de escolha única com o título da seção; <paramref name="aoEscolher"/> recebe a posição escolhida. Lista vazia mostra <paramref name="vazio"/>.</summary>
    static void Secao(Activity tela, LinearLayout raiz, string titulo, List<(string Rotulo, bool Selecionada)> itens, Func<int, Task> aoEscolher, AlertDialog dialogo, string vazio)
    {
        var cabecalho = new TextView(tela) { Text = titulo };
        cabecalho.SetTypeface(null, TypefaceStyle.Bold);
        cabecalho.SetPadding(0, Dp(tela, 12), 0, Dp(tela, 4));
        raiz.AddView(cabecalho);
        if (itens.Count == 0) { raiz.AddView(new TextView(tela) { Text = vazio }); return; }
        var grupo = new RadioGroup(tela);
        for (int i = 0; i < itens.Count; i++)
        {
            int posicao = i;
            var botao = new RadioButton(tela) { Text = itens[i].Rotulo, Id = View.GenerateViewId(), Checked = itens[i].Selecionada };
            botao.Click += async (_, _) => { await aoEscolher(posicao); dialogo.Dismiss(); };
            grupo.AddView(botao);
        }
        raiz.AddView(grupo);
    }

    /// <summary>Troca o áudio no player e, com a transmissão ligada, no envio. Se o envio recusa a faixa (não vai em cópia), o player fica como estava e o motivo aparece na tela. Roda na thread da tela.</summary>
    static async Task TrocarAudio(PlayerFilme player, CopiaDoFilme? copia, Action<string> avisar, FaixaDoPlayer faixa)
    {
        if (faixa.Identidade is not { } alvo) return;
        string? recusa = copia?.TrocarAudio(alvo);
        if (recusa != null) { avisar(recusa); return; }
        await player.SelecionarAudio(alvo);
    }

    static int Dp(Android.Content.Context c, int dp) => (int)(dp * c.Resources!.DisplayMetrics!.Density);
}
