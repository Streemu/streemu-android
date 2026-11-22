using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Player;
using Streemu.Proto.Ui;

namespace Streemu.Proto;

// As peças do player (spec 003): abrir e fechar lives, a grade, o destaque com as miniaturas, qual live toca o som, e as travas (decodificador e dados móveis).
public partial class PlayerActivity
{
    readonly List<MolduraDaPeca> _molduras = [];
    readonly Dictionary<MolduraDaPeca, FaseDaPeca> _fases = [];
    MolduraDaPeca? _principal, _doSom;
    bool _destaque, _minisEscondidas, _avisouDados;

    // ================================================================================================ abrir e fechar

    void AbrirPeca(PecaDoPlayer peca)
    {
        var m = new MolduraDaPeca(this, peca);
        peca.Quantas = () => _molduras.Count;
        peca.Indice = _molduras.Count;
        peca.Mudou += () => AoMudarPeca(m);
        peca.NumerosMudaram += AtualizarNumeros;
        m.Tocada += AoTocarPeca;
        m.FecharPedido += FecharPeca;
        m.SomPedido += mm => { _doSom = mm; AplicarSom(); };
        m.DeNovoPedido += mm => mm.Peca.TentarDeNovo();
        _molduras.Add(m);
        _doSom ??= m;
        _area.AddView(m, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Cheio));
        foreach (var x in _molduras) x.Multi(_molduras.Count > 1);
        Organizar();
        AplicarSom();
        AtualizarBotoesMulti();
        peca.Iniciar();
    }

    /// <summary>Fecha uma peça (o "×" ou o "Fechar" do erro): as outras continuam. Fechar a última fecha o player, como antes.</summary>
    void FecharPeca(MolduraDaPeca m)
    {
        if (_molduras.Count <= 1) { Finish(); return; }
        _molduras.Remove(m);
        _fases.Remove(m);
        m.Peca.Fechar();
        _area.RemoveView(m);
        if (_principal == m) _principal = _molduras[0];
        if (_doSom == m) _doSom = _principal != null && _destaque ? _principal : _molduras[0];
        for (int i = 0; i < _molduras.Count; i++) _molduras[i].Peca.Indice = i;
        foreach (var x in _molduras) x.Multi(_molduras.Count > 1);
        if (_molduras.Count == 1) _destaque = false;
        Organizar();
        AplicarSom();
        AtualizarBotoesMulti();
        AtualizarNumeros();
    }

    /// <summary>O estado de uma peça mudou (espera, erro, primeiro quadro, tamanho do vídeo): redesenha a moldura e, na primeira imagem, mostra os controles.</summary>
    void AoMudarPeca(MolduraDaPeca m)
    {
        if (_fechando || !_molduras.Contains(m)) return;
        var p = m.Peca;
        m.Atualizar();
        _fases.TryGetValue(m, out var antes);
        _fases[m] = p.Fase;
        if (m == _doSom) AtualizarBarra();
        if (p.Fase != FaseDaPeca.Tocando && _molduras.Count == 1) { _editorProporcao?.Dismiss(); Controles(false); }
        if (p.Fase == FaseDaPeca.Tocando && antes != FaseDaPeca.Tocando)
        {
            Controles(true);
            if (p.TomarAvisoDeVideo()) MostrarAvisoDeVideo(p.AvisoVideo);
        }
    }

    // ================================================================================================ grade e destaque

    /// <summary>Põe cada moldura no seu retângulo (grade ou destaque) e escolhe a camada da Surface de cada uma. Não reconecta nem reabre nada.</summary>
    void Organizar()
    {
        int n = _molduras.Count, w = _area.Width, h = _area.Height;
        if (n == 0 || w <= 0 || h <= 0) return;
        bool destaque = n > 1 && _destaque;
        int principal = destaque ? Math.Max(0, _molduras.IndexOf(_principal!)) : 0;
        bool deitado = w > h;
        var r = destaque ? DisposicaoDoPlayer.Destaque(n, principal, w, h, Tema.Dp(this, 6), Tema.Dp(this, 8), Tema.Dp(this, deitado ? 8 : 64))
                         : DisposicaoDoPlayer.Grade(n, w, h, Tema.Dp(this, 4));
        for (int i = 0; i < n; i++)
        {
            var m = _molduras[i];
            bool mini = destaque && i != principal;
            m.Modo = n == 1 || (destaque && !mini) ? _enquadramento : Enquadramento.Original;
            m.Personalizada = _proporcaoPersonalizada;
            m.TrocarVideo(sobreposta: mini);
            m.Visibility = mini && _minisEscondidas ? ViewStates.Gone : ViewStates.Visible;
            if (m.LayoutParameters is not FrameLayout.LayoutParams p || p.Width != r[i].W || p.Height != r[i].H || p.LeftMargin != r[i].X || p.TopMargin != r[i].Y)
                m.LayoutParameters = new FrameLayout.LayoutParams(r[i].W, r[i].H, GravityFlags.Top | GravityFlags.Start) { LeftMargin = r[i].X, TopMargin = r[i].Y };
            m.Ajustar();
        }
        // as miniaturas desenham por último, para o nome e o "×" ficarem por cima da principal
        for (int i = 0; i < n; i++) if (destaque && i != principal) _molduras[i].BringToFront();
    }

    /// <summary>Toque numa peça: com uma só, mostra ou esconde os controles. Com várias, uma peça fora do destaque vira a principal (um modelo só para "tornar principal").</summary>
    void AoTocarPeca(MolduraDaPeca m)
    {
        if (_molduras.Count > 1 && (!_destaque || m != _principal)) { DefinirPrincipal(m); return; }
        if (m.Peca.EstaTocando) Controles(!_controlesVisiveis);
    }

    void DefinirPrincipal(MolduraDaPeca m)
    {
        (_principal, _destaque, _doSom) = (m, true, m);
        Organizar();
        AplicarSom();
        AtualizarBotoesMulti();
    }

    void AlternarModo()
    {
        if (_destaque) _destaque = false;
        else (_principal, _destaque) = (_doSom ?? _molduras[0], true);
        Organizar();
        AplicarSom();
        AtualizarBotoesMulti();
    }

    void AlternarMiniaturas()
    {
        _minisEscondidas = !_minisEscondidas;
        _btMinis.ContentDescription = _minisEscondidas ? "Mostrar as miniaturas" : "Esconder as miniaturas";
        Organizar();
    }

    /// <summary>Só uma live toca o som (várias juntas viram barulho no alto-falante do celular): a escolhida, a menos que o "Sem som" esteja ligado.</summary>
    void AplicarSom()
    {
        int n = _molduras.Count;
        foreach (var m in _molduras)
        {
            m.Peca.Mudo = _mudoGlobal || (n > 1 && m != _doSom);
            m.SomLigado(n > 1 && m == _doSom && !_mudoGlobal);
        }
        AtualizarBarra();
    }

    void AtualizarBotoesMulti()
    {
        int n = _molduras.Count;
        _btMais.Visibility = n < MaximoDePecas && _codigoTeste == null ? ViewStates.Visible : ViewStates.Gone;
        _btModo.Visibility = n > 1 ? ViewStates.Visible : ViewStates.Gone;
        _btMinis.Visibility = n > 1 && _destaque ? ViewStates.Visible : ViewStates.Gone;
        AtualizarBotaoDeLayout();
    }

    // ================================================================================================ o "+" e as travas

    /// <summary>"+" do player: lista as lives do grupo que ainda não estão abertas.</summary>
    void AdicionarLive()
    {
        if (Sessao.Sala is not { Connected: true } sala) { AvisarControle("Sem conexão com o grupo."); return; }
        var abertas = _molduras.Select(m => m.Peca.Conta).ToHashSet();
        var opcoes = sala.Friends.Where(f => f.Live != null && !abertas.Contains(f.Id)).ToList();
        if (opcoes.Count == 0) { AvisarControle("Ninguém mais está ao vivo no grupo."); return; }
        var nomes = opcoes.Select(f => $"{f.Nick} · {string.Join(" ", new[] { f.Live!.Game, f.Live.Quality }.Where(s => s.Length > 0))}").ToArray();
        new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)
            .SetTitle("Assistir mais uma live")!
            .SetItems(nomes, (_, e) => TentarAbrir(opcoes[e.Which]))!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Show();
    }

    /// <summary>Antes de abrir a próxima live: o decodificador aguenta a soma das lives abertas e a nova? E, nos dados móveis, quanto isso gasta?</summary>
    void TentarAbrir(Member f)
    {
        if (f.Live is not { } live || _molduras.Count >= MaximoDePecas) return;
        var qualidades = _molduras.Select(m => m.Peca.Qualidade).Append(live.Quality).Select(q => Aparelho.LerQualidade(q) ?? (1920, 1080, 60)).ToList();
        _ = Task.Run(() =>
        {
            var r = Aparelho.AguentaVarias(qualidades, out var detalhe);
#if DEBUG
            // só no Debug: força o limite do critério C6 sem precisar de um celular fraco (adb ... --ei limite_lives 2)
            if (Intent!.GetIntExtra("limite_lives", 0) is > 0 and var limite && qualidades.Count > limite) (r, detalhe) = (Aparelho.Aguenta.Nao, $"limite de teste de {limite} lives");
#endif
            Streemu.Util.Log.Info($"CAPACIDADE de {qualidades.Count} lives: {r} ({detalhe})");
            Telemetria.Evento("capacidade", new System.Text.Json.Nodes.JsonObject { ["pecas"] = qualidades.Count, ["resultado"] = r.ToString(), ["detalhe"] = detalhe });
            RunOnUiThread(() => { if (!_fechando) Decidir(f, live, r); });
        });
    }

    void Decidir(Member f, LiveInfo live, Aparelho.Aguenta r)
    {
        int n = _molduras.Count + 1;
        if (r == Aparelho.Aguenta.Nao)
        {
            Avisar("Este celular não aguenta mais uma live",
                   $"O decodificador de vídeo do aparelho não promete dar conta de {n} lives nessa qualidade ao mesmo tempo. Feche uma delas (no ×) e abra de novo, ou peça para quem transmite baixar a qualidade.");
            return;
        }
        if (!_avisouDados && _rede?.IsActiveNetworkMetered == true)
        {
            double mbit = _molduras.Sum(m => m.Peca.MbitEstimado) + (Aparelho.LerQualidade(live.Quality) is { } q ? 8.0 * q.W * q.H * q.Fps / (1920.0 * 1080 * 60) : 8);
            double gbPorHora = mbit * 0.45;   // Mbit/s x 3600 s / 8 / 1000
            new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)
                .SetTitle("Você está nos dados móveis")!
                .SetMessage($"Cada live gasta uns {gbPorHora / n:0.0} GB por hora. Com {n} lives abertas, uns {gbPorHora:0.0} GB por hora.")!
                .SetPositiveButton("Abrir assim mesmo", (_, _) => { _avisouDados = true; AbrirDaSala(f, live); })!
                .SetNegativeButton("Cancelar", (_, _) => { })!
                .Show();
            return;
        }
        AbrirDaSala(f, live);
    }

    void AbrirDaSala(Member f, LiveInfo live)
    {
        if (_fechando || _molduras.Count >= MaximoDePecas || _molduras.Any(m => m.Peca.Conta == f.Id)) return;
        AbrirPeca(new PecaDoPlayer(this, RunOnUiThread, f.Id, f.Nick, live.Game, live.Quality, live.SinceUnixMs, null));
    }

    void Avisar(string titulo, string texto) =>
        new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert).SetTitle(titulo)!.SetMessage(texto)!.SetPositiveButton("Entendi", (_, _) => { })!.Show();
}
