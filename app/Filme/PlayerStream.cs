using Android.Content;
using Android.Views;
using Streemu.Filme;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// O player de quem assiste o filme pela transmissão (spec 028, 5.1), no lugar do mpv da <c>StreamTela</c> do Windows: um <see cref="IPlayerFilme"/> que a <see cref="Sessao"/>
/// comanda como a de quem tem o arquivo. Por baixo, um <see cref="PlayerFilme"/> (ExoPlayer) toca o <see cref="CacheTs"/> da época.
/// <para>
/// <b>Épocas.</b> O anfitrião abre o envio num ponto (época) e manda <c>filme_stream_epoca</c>: <see cref="Epoca"/> abre um SRT novo e um cache novo. A época velha deixa de ser lida.
/// </para>
/// <para>
/// <b>Carimbos.</b> O anfitrião manda o MPEG-TS no tempo do filme (<c>-output_ts_offset</c> do ffmpeg, igual ao <c>--rebase-start-time=no</c> do mpv): o PTS é a posição do
/// filme em segundos. O extrator de TS do ExoPlayer leva o primeiro carimbo que vê a 0, então a posição do filme é <c>base + posição do ExoPlayer</c>, com a base sendo o PTS
/// do quadro-chave em que a fonte começou (a fonte sempre começa num quadro-chave do índice do cache, com a PAT e a PMT na frente). Cada abertura guarda a sua base.
/// </para>
/// <para>
/// <b>Pulo (v1).</b> <see cref="Ir"/> procura o quadro-chave do ponto no cache da época mais nova: tendo, abre uma fonte que começa nele e o ExoPlayer vai ao ponto exato
/// (descarta o que está entre o quadro-chave e o ponto); o ponto ainda não chegou, espera; ficou antes do que o cache tem, pede <c>filme_stream_recarregar</c> (<see cref="Recarregar"/>)
/// e espera a época nova, como o Windows. Sem índice PTS dentro do cache além dos quadros-chave que ele guarda (a v2 da spec).
/// </para>
/// </summary>
public sealed class PlayerStream : IPlayerFilme
{
    readonly PlayerFilme _p;
    readonly string _pasta;
    readonly object _g = new();
    CacheTs? _ultima;          // a época mais nova
    IDisposable? _leitorUltima;
    CacheTs? _tocando;         // o cache que o ExoPlayer lê agora
    double _base;              // PTS (s do filme) em que a fonte atual começou: posição do filme = _base + posição do ExoPlayer
    double _inicioPts = double.NaN;
    int _epoca = -1, _irGen, _aberturas;
    readonly System.Diagnostics.Stopwatch _pediu = new();
    CacheTs? _pediuNaEpoca;
    readonly System.Diagnostics.Stopwatch _pediuNaEpocaEm = new();

    /// <summary>Logcat e log do app: só o log do app vai para a telemetria, e o congelamento de 23/11 num celular de espectador ficou sem prova porque isto só ia para o Logcat.</summary>
    static void Registrar(string mensagem)
    {
        Logcat.Info("Streemu", mensagem);
        Streemu.Util.Log.Info("[filme] " + mensagem);
    }
    volatile bool _fechado;

    /// <summary>O player não achou o ponto no que chegou: pede ao anfitrião para reabrir o envio nele (<c>filme_stream_recarregar</c>).</summary>
    public event Action? Recarregar;
    /// <summary>Uma época nova entrou (número).</summary>
    public event Action<int>? EpocaNova;
    public event Action<string>? Falhou;

    /// <summary>Só o teste mexe: tetos do cache (RAM e anel do arquivo) para exercitar a passagem para o arquivo com um TS pequeno.</summary>
    public long LimiteRam { get; set; } = 64L << 20;
    public long CapArquivo { get; set; } = 512L << 20;

    public int EpocasRecebidas { get; private set; }
    public int Reaberturas => _aberturas;
    public int RecargasPedidas { get; private set; }

    public PlayerStream(Context contexto, SurfaceView superficie, bool comecarTocando = false)
    {
        _pasta = Path.Combine(contexto.CacheDir!.AbsolutePath, "filme-stream");
        try { if (Directory.Exists(_pasta)) foreach (var f in Directory.GetFiles(_pasta, "filme-*.ts")) try { File.Delete(f); } catch { } } catch { }
        _p = new PlayerFilme(contexto, superficie, comecarTocando, terminaNoFim: false);
        _p.Falhou += m => { Logcat.Error("Streemu", "FILME stream: " + m); Falhou?.Invoke(m); };
    }

    public bool Saiu => _fechado || _p.Saiu;
    public double AtrasoPartidaS => _p.AtrasoPartidaS;
    public double VelocidadeMaxDesvio => _p.VelocidadeMaxDesvio;
    public double VelocidadeDesvioPequeno => _p.VelocidadeDesvioPequeno;
    public double ZonaMortaS => _p.ZonaMortaS;

    /// <summary>Duração (s) que o anfitrião informou (o fluxo ao vivo não tem).</summary>
    public double Duracao { get; set; } = double.NaN;

    // ------------------------------------------------------------------ épocas

    /// <summary>O anfitrião abriu o envio num ponto novo (<c>filme_stream_epoca</c>): lê o SRT dela para um cache novo. Números velhos são ignorados.</summary>
    public void Epoca(int numero, double pos, int porta, string senha) => Epoca(numero, pos, c => new LeitorSrt(porta, senha, c));

    /// <summary>Igual, com quem escreve o cache escolhido por quem chama (o teste escreve de um arquivo).</summary>
    public void Epoca(int numero, double pos, Func<CacheTs, IDisposable?> leitor)
    {
        if (_fechado) return;
        CacheTs c; IDisposable? l;
        lock (_g)
        {
            if (numero <= _epoca) return;
            _epoca = numero;
            c = new CacheTs(_pasta, LimiteRam, CapArquivo);
            l = leitor(c);
            _leitorUltima?.Dispose();
            var velha = _ultima;
            _ultima = c; _leitorUltima = l;
            if (velha != null && velha != _tocando) velha.Dispose();
        }
        EpocasRecebidas++;
        Registrar(FormattableString.Invariant($"FILME época {numero} recebida: o anfitrião manda a partir de {pos:F1} s."));
        EpocaNova?.Invoke(numero);
        int gen = _irGen;
        if (_tocando != null) _ = Task.Run(() => SeguirEpocaAsync(c, gen));
    }

    /// <summary>
    /// Época nova com o player já tocando (troca de perfil, de faixa, mais um espectador: o anfitrião reabre o envio no mesmo ponto, sem pulo na Sessão): quando a época velha
    /// acaba e a nova já tem o ponto de agora, passa para ela sem mudar o play/pause. Um <see cref="Ir"/> no meio assume e este desiste.
    /// </summary>
    async Task SeguirEpocaAsync(CacheTs nova, int gen)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_fechado && gen == _irGen && sw.ElapsedMilliseconds < 12000)
            {
                var velha = _tocando;
                if (velha == null || velha == nova) return;
                if (velha.Terminou)
                {
                    double pos = await PosicaoAsync();
                    if (nova.Procurar(pos, out long kp, out double kf) == CacheTs.Lugar.Pronto && gen == _irGen)
                    {
                        Registrar(FormattableString.Invariant($"FILME a época anterior acabou em {pos:F1} s: passando para a nova."));
                        await Reabrir(nova, kp, kf, pos, gen);
                        return;
                    }
                }
                await Task.Delay(150);
            }
        }
        catch (Exception e) { Registrar("FILME seguir a época: " + e.Message); }
    }

    // ------------------------------------------------------------------ IPlayerFilme

    public Task Velocidade(double v) => _p.Velocidade(v);
    public Task Pausar(bool pausar) => _p.Pausar(pausar);

    /// <summary>Vai para o ponto do filme; espera o dado dele chegar (ou a época nova, 12 s), como a StreamTela. O <see cref="EsperarIrAsync"/> espera o primeiro quadro.</summary>
    public async Task Ir(double s)
    {
        int gen = Interlocked.Increment(ref _irGen);
        s += _p.FolgaIrS;   // o pulo do ExoPlayer leva ~1 s até READY: mira à frente nesse tanto (só tocando)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? avisou = null;
        while (!_fechado && gen == _irGen)
        {
            CacheTs? c; lock (_g) c = _ultima;
            if (c != null)
            {
                var r = c.Procurar(s, out long pos, out double kf);
                if (r == CacheTs.Lugar.Pronto) { await Reabrir(c, pos, kf, s, gen); return; }
                // só pede depois de a época atual ter mandado dados que não têm o ponto, e uma vez por época: época ainda abrindo (nenhum byte; o anfitrião recomprimindo um 4K
                // pesado leva vários s) ou pausada esperando só alimentava o laço de épocas do anfitrião (caso de 22/11, um pedido a cada 4 s)
                bool temDados = c.Bytes > 0 && !double.IsNaN(c.PtsMax);
                // o anfitrião pode adiar ou descartar o pedido (época recém-aberta): vale pedir de novo na mesma época depois de 6 s, em vez de esperar os 15 s do fim (caso de 23/11)
                bool jaPediu = ReferenceEquals(c, _pediuNaEpoca) && _pediuNaEpocaEm.IsRunning && _pediuNaEpocaEm.ElapsedMilliseconds < 6000;
                if (temDados && !jaPediu && ((r == CacheTs.Lugar.Antes && sw.ElapsedMilliseconds > 2000) || sw.ElapsedMilliseconds > 12000))
                {
                    _pediuNaEpoca = c; _pediuNaEpocaEm.Restart();
                    PedirRecarregar($"o ponto {s:F1} s {(r == CacheTs.Lugar.Antes ? "ficou antes do que chegou" : "não chegou em 12 s")}");
                }
                avisou ??= $"o ponto {s:F1} s ainda não chegou (cache: {c.PrimeiraChave:F1} a {c.PtsMax:F1} s)";
            }
            if (sw.ElapsedMilliseconds > 15000) { Registrar("FILME " + (avisou ?? "sem época") + ": desisti de esperar."); return; }
            await Task.Delay(150);
        }
    }

    void PedirRecarregar(string motivo)
    {
        if (_pediu.IsRunning && _pediu.ElapsedMilliseconds < 4000) return;
        _pediu.Restart();
        RecargasPedidas++;
        Registrar("FILME pedindo ao anfitrião para reabrir o envio: " + motivo + ".");
        Recarregar?.Invoke();
    }

    async Task Reabrir(CacheTs c, long pos, double kf, double s, int gen)
    {
        if (_fechado || gen != _irGen) return;
        // o ponto está na fonte que já toca e perto do começo dela: o ExoPlayer só relê o cache do começo da fonte e descarta até o ponto
        if (c == _tocando && s >= _base - 0.001 && s - _base < 6)
        {
            await _p.IrNaFonte((long)Math.Max(0, (s - _base) * 1000));
            return;
        }
        var fonte = FabricaFonteCache.Criar(c, pos);
        CacheTs? antigo = null;
        await _p.Abrir(fonte, (long)Math.Max(0, (s - kf) * 1000), () =>
        {
            antigo = _tocando; _tocando = c; _base = kf; _inicioPts = kf;
        });
        _aberturas++;
        Registrar(FormattableString.Invariant($"FILME fonte aberta no cache: ponto {s:F2} s, quadro-chave {kf:F2} s (byte {pos}), base {kf:F2}."));
        if (antigo != null && antigo != c) { bool deMenos; lock (_g) deMenos = antigo != _ultima; if (deMenos) antigo.Dispose(); }
    }

    public Task<bool> EsperarIrAsync() => _p.EsperarIrAsync();

    /// <summary>Posição do filme pelo áudio (o relógio do ExoPlayer é o do áudio): base da fonte + posição do player. NaN sem fonte.</summary>
    public Task<double> PosicaoAudioAsync() => PosicaoAsync();

    public Task<double> PosicaoAsync() => _p.NoPrincipal(() => _tocando == null ? double.NaN : _base + _p.PosicaoDireta);

    /// <summary>Para o log do teste: carimbo base e posição do ExoPlayer.</summary>
    public Task<(double Base, double Player)> BaseEPlayerAsync() => _p.NoPrincipal(() => (_base, _p.PosicaoDireta));

    public Task<(int Estado, double Buffer)> EstadoAsync() => _p.EstadoAsync();

    public Task<double> DuracaoAsync() => Task.FromResult(Duracao);

    public CacheTs? CacheAtual { get { lock (_g) return _ultima; } }

    public void Fechar()
    {
        if (_fechado) return;
        _fechado = true;
        Interlocked.Increment(ref _irGen);
        CacheTs? a, b; IDisposable? l;
        lock (_g) { a = _ultima; b = _tocando; l = _leitorUltima; _ultima = null; _leitorUltima = null; }
        l?.Dispose();
        _p.Fechar();
        // o ExoPlayer solta a fonte no Release (já postado); fechar o cache antes só faz a leitura acabar
        a?.Dispose(); if (b != a) b?.Dispose();
    }

    void IDisposable.Dispose() => Fechar();
}
