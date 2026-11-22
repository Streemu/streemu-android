using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using IdentidadeDeAudio = Streemu.Filme.IdentidadeDeAudio;

namespace Streemu.Proto.Filme;

/// <summary>
/// Embrulha um ExoPlayer (Media3) para tocar um arquivo do aparelho numa SurfaceView.
/// O ExoPlayer só aceita chamadas na thread em que nasceu: tudo passa pelo looper principal.
/// Os nomes dos métodos seguem o IPlayerFilme do app Windows.
/// </summary>
public sealed class PlayerFilme : Java.Lang.Object, IPlayerListener, Streemu.Filme.IPlayerFilme
{
    readonly Handler _principal = new(Looper.MainLooper!);
    readonly IExoPlayer _player;
    volatile bool _saiu;
    TaskCompletionSource? _pronto;
    volatile bool _querTocar;
    readonly System.Diagnostics.Stopwatch _irRelogio = new();
    double _atrasoIrS = 1.0;   // quanto um pulo leva até READY (média móvel); 1 s é o que se mediu no Galaxy S26 (spec 028 8.1)

    /// <summary>
    /// Quanto adiantar o alvo de um pulo para o filme chegar no ponto certo: o pulo leva ~1 s até READY e, tocando, o filme anda nesse tempo.
    /// Já desconta os 0,3 s que a <see cref="Streemu.Filme.Sessao"/> soma (constante do mpv). Zero se o player está pausado (a Sessao espera o instante certo).
    /// </summary>
    public double FolgaIrS => _querTocar ? Math.Clamp(_atrasoIrS - 0.3, 0, 2) : 0;

    public bool Saiu => _saiu;

    double _atrasoPartidaS = 0.25;   // média móvel do que o ExoPlayer leva entre PlayWhenReady=true e a posição andar de fato
    public double AtrasoPartidaS => _atrasoPartidaS;
    /// <summary>
    /// Achado de campo 2026-11-22 (S23 e S26): com o Sonic mantendo o tom (WSOLA, pitch 1) qualquer velocidade diferente de 1,0 estala; a 1,0 não.
    /// Por isso a velocidade vai com o tom junto (só reamostra, sem time-stretch): o tom muda até 4% por poucos segundos. Erros grandes a Sessao resolve com nova procura.
    /// </summary>
    public double VelocidadeMaxDesvio => 0.04;
    /// <summary>Até 200 ms de erro (o caso frequente) só ±2%: o tom junto muda pouco e raramente. A margem de 40 ms aceita alguns ms de diferença em vez de corrigir toda hora.</summary>
    public double VelocidadeDesvioPequeno => 0.02;
    public double ZonaMortaS => 0.040;

    /// <param name="comecarTocando">Falso para quem assiste numa sessão: o filme só toca quando a linha do tempo (Sessao) mandar.</param>
    public PlayerFilme(Context contexto, SurfaceView superficie, Android.Net.Uri arquivo, bool comecarTocando = true) : this(contexto, superficie, comecarTocando)
    {
        _player.SetMediaItem(MediaItem.FromUri(arquivo)!);
        // tunneling vem desligado por padrão e não o ligamos; offload de áudio também fica no padrão (desligado)
        _player.Prepare();
    }

    /// <summary>
    /// Player sem fonte (a transmissão, <see cref="PlayerStream"/>, dá o IMediaSource de cada época com <see cref="Abrir"/>). Com <paramref name="terminaNoFim"/> falso o
    /// fim da fonte não mata o player: o fim de uma época é só o fluxo ter acabado, e a próxima fonte vem logo depois.
    /// </summary>
    public PlayerFilme(Context contexto, SurfaceView superficie, bool comecarTocando, bool terminaNoFim = true)
    {
        _terminaNoFim = terminaNoFim;
        var baixaRam = ((ActivityManager?)contexto.GetSystemService(Context.ActivityService))?.IsLowRamDevice ?? false;
        var fabrica = new DefaultRenderersFactory(contexto);
        fabrica.SetEnableDecoderFallback(true);
        var carga = new DefaultLoadControl.Builder();
        if (baixaRam) carga.SetBufferDurationsMs(5_000, 15_000, 1_000, 2_000);
        else carga.SetBufferDurationsMs(15_000, 50_000, 2_500, 5_000);
        _player = new ExoPlayerBuilder(contexto, fabrica)
            .SetLoadControl(carga.Build()!)
            .SetSeekParameters(SeekParameters.Exact!)
            .SetVideoChangeFrameRateStrategy(C.VideoChangeFrameRateStrategyOnlyIfSeamless)   // troca a taxa da tela quando dá sem piscar
            .Build()!;
        _player.AddListener(this);
        _player.SetVideoSurfaceView(superficie);
        _player.PlayWhenReady = _querTocar = comecarTocando;
    }

    readonly bool _terminaNoFim = true;
    /// <summary>O fim da fonte (só quando <c>terminaNoFim</c> é falso): o fluxo da época acabou e o player tocou tudo.</summary>
    public event Action? Terminou;
    public event Action<string>? Falhou;

    /// <summary>Troca a fonte e começa em <paramref name="inicioMs"/> (exato). Volta quando o ExoPlayer aceitou; <see cref="EsperarIrAsync"/> espera o primeiro quadro.</summary>
    public Task Abrir(AndroidX.Media3.ExoPlayer.Source.IMediaSource fonte, long inicioMs, Action? dentro = null) => NoPrincipal(() =>
    {
        dentro?.Invoke();   // no looper principal, junto da troca: a posição que o <c>PlayerStream</c> calcula nunca vê a fonte nova com a base velha
        _pronto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _irRelogio.Restart();
        _player.SetMediaSource(fonte, inicioMs);
        _player.Prepare();
        return 0;
    });

    /// <summary>Vai para <paramref name="ms"/> dentro da fonte atual.</summary>
    public Task IrNaFonte(long ms) => NoPrincipal(() =>
    {
        _pronto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _irRelogio.Restart();
        _player.SeekTo(ms);
        return 0;
    });

    /// <summary>Estado do ExoPlayer (1 ocioso, 2 carregando, 3 pronto, 4 fim) e o que já tem à frente em s.</summary>
    public Task<(int Estado, double Buffer)> EstadoAsync() => NoPrincipal(() => (_player.PlaybackState, (_player.BufferedPosition - _player.CurrentPosition) / 1000.0));

    /// <summary>Roda no looper principal e devolve o resultado.</summary>
    internal Task<T> NoPrincipal<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _principal.Post(() =>
        {
            try { tcs.SetResult(_saiu ? default! : f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>Duração do filme em segundos (NaN até o ExoPlayer saber).</summary>
    public Task<double> DuracaoAsync() => NoPrincipal(() => _player.Duration is long d and > 0 ? d / 1000.0 : double.NaN);

    double _velAplicada = 1.0;
    long _velInstanteMs = long.MinValue / 2;

    /// <summary>
    /// Cada troca de velocidade reconfigura o Sonic do Media3 e pode estalar no som: só aplica se mudou >= 0,01 (passos de 0,01),
    /// com pelo menos 700 ms desde a última troca; voltar a 1,0 vale na hora.
    /// </summary>
    public Task Velocidade(double v) => NoPrincipal(() =>
    {
        v = Math.Round(v, 2);
        long agora = System.Environment.TickCount64;
        bool paraUm = v == 1.0;
        if (paraUm ? _velAplicada == 1.0 : Math.Abs(v - _velAplicada) < 0.01 - 1e-9 || agora - _velInstanteMs < 700) return 0;
        _velAplicada = v; _velInstanteMs = agora;
        _player.PlaybackParameters = new PlaybackParameters((float)v, (float)v);   // tom junto com a velocidade (ver VelocidadeMaxDesvio); em 1,0 vira (1f, 1f)
        return 0;
    });

    public Task Pausar(bool pausar) => NoPrincipal(() =>
    {
        _player.PlayWhenReady = _querTocar = !pausar;
        if (!pausar) MedirPartida(); else _medindo++;
        return 0;
    });

    int _medindo;

    /// <summary>Mede quanto tempo passa entre despausar e a posição começar a andar (no looper principal, a cada 5 ms).</summary>
    void MedirPartida()
    {
        int id = ++_medindo;
        double p0 = _player.CurrentPosition / 1000.0;
        var rel = System.Diagnostics.Stopwatch.StartNew();
        void Passo()
        {
            if (_saiu || id != _medindo) return;
            double p = _player.CurrentPosition / 1000.0;
            if (p > p0 + 0.0005 || (p != p0 && rel.ElapsedMilliseconds > 20))
            {
                double med = rel.Elapsed.TotalSeconds;
                _atrasoPartidaS = Math.Clamp(0.5 * _atrasoPartidaS + 0.5 * med, 0, 1);
                Android.Util.Log.Info("Streemu", FormattableString.Invariant($"FILME despausar levou {med * 1000:F0} ms (média {_atrasoPartidaS * 1000:F0} ms)."));
            }
            else if (rel.ElapsedMilliseconds < 3000) _principal.PostDelayed(Passo, 5);
        }
        _principal.PostDelayed(Passo, 5);
    }

    public Task Ir(double s) => NoPrincipal(() =>
    {
        _pronto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _irRelogio.Restart();
        _player.SeekParameters = SeekParameters.Exact;
        _player.SeekTo((long)((s + FolgaIrS) * 1000));
        return 0;
    });

    /// <summary>Espera o estado READY depois do Ir. Falso se estourou o tempo (8 s) ou o player morreu.</summary>
    public async Task<bool> EsperarIrAsync()
    {
        var t = await NoPrincipal(() => (Task?)_pronto?.Task ?? Task.CompletedTask);
        var fim = await Task.WhenAny(t, Task.Delay(8000));
        return fim == t && !_saiu;
    }

    public Task<double> PosicaoAudioAsync() => NoPrincipal(() => _player.CurrentPosition / 1000.0);   // o relógio do ExoPlayer é o do áudio

    public Task<double> PosicaoAsync() => NoPrincipal(() => _player.CurrentPosition / 1000.0);

    /// <summary>Só no looper principal (dentro de <see cref="NoPrincipal"/>): a posição do ExoPlayer em s.</summary>
    internal double PosicaoDireta => _player.CurrentPosition / 1000.0;

    public void Fechar()
    {
        if (_saiu) return;
        _saiu = true;
        _principal.Post(() =>
        {
            try { _player.RemoveListener(this); } catch { }
            try { _player.Release(); } catch { }
        });
    }

    // a Sessao (cinema/) chama Dispose ao terminar; o Java.Lang.Object fica vivo porque o ExoPlayer ainda guarda o ouvinte até o Release
    void IDisposable.Dispose() => Fechar();

    public void OnPlaybackStateChanged(int estado)
    {
        if (estado == 3 /* STATE_READY */)
        {
            if (_irRelogio.IsRunning)
            {
                double med = _irRelogio.Elapsed.TotalSeconds; _irRelogio.Reset();
                if (med < 5) _atrasoIrS = 0.5 * _atrasoIrS + 0.5 * med;
                Android.Util.Log.Info("Streemu", FormattableString.Invariant($"FILME pulo levou {med * 1000:F0} ms até READY (média {_atrasoIrS * 1000:F0} ms)."));
            }
            var p = _pronto; _pronto = null; p?.TrySetResult();
        }
        else if (estado == 4 /* STATE_ENDED */) { if (_terminaNoFim) _saiu = true; else Terminou?.Invoke(); }
    }

    // ------------------------------------------------------------------ faixas de áudio e legendas

    const int TipoAudio = 1, TipoTexto = 3;   // C.TRACK_TYPE_AUDIO e C.TRACK_TYPE_TEXT
    IdentidadeDeAudio? _audioAlvo;

    /// <summary>Uma faixa do arquivo para o menu. <see cref="Indice"/> é a posição entre as faixas do mesmo tipo, na ordem do arquivo (a do MediaExtractor).</summary>
    public sealed record FaixaDoPlayer(int Indice, string Rotulo, bool Selecionada, IdentidadeDeAudio? Identidade = null);

    /// <summary>As faixas do arquivo mudaram (o ExoPlayer acabou de lê-las, ou uma seleção valeu). Vem do looper principal.</summary>
    public event Action? FaixasMudaram;

    /// <summary>
    /// Toca a faixa de áudio <paramref name="alvo"/> (identificada por codec, canais e número no arquivo, não por posição: o ExoPlayer e o MediaExtractor listam em ordens diferentes) e a mantém
    /// quando o ExoPlayer ler as faixas de novo. Sem isso ele escolhe sozinho pelo idioma do aparelho, e o anfitrião ouviria uma língua enquanto a transmissão leva outra (campo 2026-11-22).
    /// </summary>
    public void ForcarAudio(IdentidadeDeAudio alvo)
    {
        _audioAlvo = alvo;
        _principal.Post(() => { if (!_saiu) AplicarAudioDesejado(); });
    }

    void AplicarAudioDesejado()
    {
        if (_audioAlvo is not { } alvo) return;
        var grupos = GruposDe(TipoAudio);
        int n = alvo.ProcurarEm(IdentidadesDe(grupos));
        if (n < 0 || n >= grupos.Count || grupos[n].IsSelected) return;
        _player.TrackSelectionParameters = _player.TrackSelectionParameters.BuildUpon()!
            .SetOverrideForType(new TrackSelectionOverride(grupos[n].MediaTrackGroup, 0))!.Build()!;
    }

    List<Tracks.Group> GruposDe(int tipo)
    {
        var r = new List<Tracks.Group>();
        foreach (var o in _player.CurrentTracks.Groups) if (o is Tracks.Group g && g.Type == tipo) r.Add(g);   // lista do Java: sem LINQ
        return r;
    }

    static List<IdentidadeDeAudio> IdentidadesDe(List<Tracks.Group> grupos) =>
        IdentidadeDeAudio.Numerar(grupos.Select(g => { var f = g.GetTrackFormat(0); return (f.SampleMimeType, f.ChannelCount, f.Id); }));

    /// <summary>As faixas de áudio e de legenda do arquivo e se as legendas estão ligadas.</summary>
    public Task<(List<FaixaDoPlayer> Audios, List<FaixaDoPlayer> Legendas, bool LegendaLigada)> FaixasAsync() => NoPrincipal(() =>
        (ListarAudios(), Listar(TipoTexto), !_player.TrackSelectionParameters.DisabledTrackTypes.Contains(Java.Lang.Integer.ValueOf(TipoTexto)) && GruposDe(TipoTexto).Any(g => g.IsSelected)));

    List<FaixaDoPlayer> ListarAudios()
    {
        var grupos = GruposDe(TipoAudio);
        var ids = IdentidadesDe(grupos);
        return grupos.Select((g, i) => new FaixaDoPlayer(i, Rotulo(g.GetTrackFormat(0), i), g.IsSelected, ids[i])).ToList();
    }

    List<FaixaDoPlayer> Listar(int tipo)
    {
        var grupos = GruposDe(tipo);
        return grupos.Select((g, i) => new FaixaDoPlayer(i, Rotulo(g.GetTrackFormat(0), i), g.IsSelected)).ToList();
    }

    /// <summary>"Português · 5.1" (o nome da faixa que o arquivo traz, ou o idioma por extenso), sem repetir o que o arquivo já escreve.</summary>
    static string Rotulo(Format f, int i)
    {
        string nome = f.Label ?? "";
        string lingua = f.Language is { Length: > 0 } l && l != "und" ? new Java.Util.Locale(l).DisplayLanguage : "";
        string canais = f.ChannelCount > 0 ? (f.ChannelCount switch { 1 => "mono", 2 => "estéreo", 6 => "5.1", 8 => "7.1", _ => f.ChannelCount + " canais" }) : "";
        var partes = new[] { nome != "" ? nome : lingua != "" ? char.ToUpperInvariant(lingua[0]) + lingua[1..] : $"Faixa {i + 1}", canais }.Where(x => x != "");
        return string.Join(" · ", partes);
    }

    /// <summary>Troca a faixa de áudio. O envio da transmissão é de quem chama (<c>CopiaDoFilme</c>).</summary>
    public Task SelecionarAudio(IdentidadeDeAudio alvo) => NoPrincipal(() => { _audioAlvo = alvo; AplicarAudioDesejado(); return 0; });

    /// <summary>Liga a legenda <paramref name="indice"/> (entre as de texto) ou desliga todas (nulo). A legenda só aparece aqui: a transmissão do filme leva o vídeo e o áudio, sem legenda.</summary>
    public Task SelecionarLegenda(int? indice) => NoPrincipal(() =>
    {
        var b = _player.TrackSelectionParameters.BuildUpon()!;
        var grupos = GruposDe(TipoTexto);
        if (indice is not int n || n < 0 || n >= grupos.Count) b.SetTrackTypeDisabled(TipoTexto, true);
        else b.SetTrackTypeDisabled(TipoTexto, false)!.SetOverrideForType(new TrackSelectionOverride(grupos[n].MediaTrackGroup, 0));
        _player.TrackSelectionParameters = b.Build()!;
        return 0;
    });

    public void OnTracksChanged(Tracks tracks)
    {
        foreach (var f in ListarAudios())
            Android.Util.Log.Info("Streemu", $"FILME faixa de áudio do player (ExoPlayer) #{f.Indice}: {f.Identidade?.Mime} {f.Identidade?.Canais} canais, número {f.Identidade?.TrackId ?? "?"}, \"{f.Rotulo}\"{(f.Selecionada ? " <- tocando" : "")}.");
        AplicarAudioDesejado();
        FaixasMudaram?.Invoke();
    }

    public void OnPlayerError(PlaybackException erro)
    {
        Android.Util.Log.Error("Streemu", "FILME erro " + erro.ErrorCodeName + " " + erro.Message);
        if (!_terminaNoFim) { _pronto?.TrySetResult(); Falhou?.Invoke(erro.ErrorCodeName + " " + erro.Message); return; }
        _saiu = true;
        _pronto?.TrySetResult();
    }
}
