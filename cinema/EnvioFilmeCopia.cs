using System.Diagnostics;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// O anfitrião manda o próprio arquivo como mpegts, em cópia direta (spec 028, 5.2): o espelho do <c>EnvioStream</c> do Windows (spec 011), sem ffmpeg. Quem lê o arquivo e monta o mpegts é a
/// <see cref="IFonteTs"/>; aqui ficam o protocolo de épocas, o envio adiantado e a repartição para as <see cref="SaidaFilme"/>. O SRT de cada espectador é de quem usa esta classe.
/// <para>
/// <b>Época</b> = um ponto de partida da fonte. Abre quando alguém começa a assistir, quando a Sessão pula para fora do que já foi mandado (ou para antes do que todos guardam) ou quando quem
/// assiste pede <see cref="Recarregar"/>. Cada época avisa o espectador (<see cref="Mudou"/>), que abre o SRT de novo. Pausar e continuar não abrem época: o envio lê até
/// <see cref="AdiantadoS"/> à frente da posição da Sessão e segura a fonte aí; quem assiste guarda o que chega e segue o relógio comum.
/// </para>
/// Quem chega no meio da época começa do último quadro-chave guardado (<see cref="MaxGopBytes"/>). Quem manda no tempo é a Sessão: este objeto só a segue (<see cref="Seguir"/>).
/// Uso: <c>var e = new EnvioFilmeCopia(info, () =&gt; new MinhaFonte()); var s = e.Adicionar(id, nome); e.Seguir(sessao.Posicao, sessao.Tocando);</c>
/// </summary>
public sealed class EnvioFilmeCopia : IDisposable
{
    /// <summary>Quanto o envio corre à frente da posição da Sessão: o que quem assiste tem guardado para pular para frente sem recarregar.</summary>
    public const double AdiantadoS = 30;
    /// <summary>Quanto atrás da posição quem assiste ainda tem guardado (o cache do <c>PlayerStream</c>): pular para trás dentro disso não recarrega.</summary>
    public const double AtrasS = 40;
    /// <summary>Latência do SRT: cada época paga essa latência para voltar a tocar (o mesmo valor do Windows, spec 011).</summary>
    public const int LatenciaMs = 150;
    /// <summary>Teto do trecho guardado para quem conecta depois do início da época; acima disso quem entra começa no próximo quadro-chave.</summary>
    public const int MaxGopBytes = 2_500_000;
    const double RajadaS = 1.0;
    /// <summary>Quanto tempo uma época recém-aberta, sem pulo, pode levar para mandar algo e alcançar a Sessão antes de ser reaberta.</summary>
    const double AberturaMaxS = 10;
    /// <summary>
    /// Uma fonte atrasada que ainda anda não é motivo para reabrir a época: reabrir recomeça do mesmo quadro-chave (num filme de GOP longo, ~10 s antes do ponto) e só joga fora o que já foi mandado.
    /// Caso de 22/11 (3 espectadores no celular): a fonte lia ~0,37x o tempo real, o envio reabria a época a cada 10 s, e quem assistia ficava ~5 s sem imagem a cada vez (15 épocas em 6 min).
    /// Só reabre se a fonte parou de avançar por tanto tempo.
    /// </summary>
    const double SemAvancoMaxS = 3;
    /// <summary>Enquanto o envio não está tanto à frente da posição, ele está alcançando: o começo de uma época sai do quadro-chave anterior ao ponto (num filme de GOP longo, uns 3 a 10 s antes).</summary>
    const double AlcanceAteS = 2;
    /// <summary>
    /// Épocas seguidas em que a fonte abriu e não entregou nenhum bloco. Chegando a isso, o envio para de reabrir (cada reabertura derruba o SRT de quem assiste) e avisa por <see cref="Falhou"/>.
    /// Caso de 22/11: a fonte abria e terminava na hora, e o envio reabriu a época a cada 2 s, de 1 a 85.
    /// </summary>
    public const int MaxEpocasVazias = 3;

    volatile FonteInfo _info;
    Func<IFonteTs> _criarFonte;   // sob _gate
    readonly object _gate = new(), _dist = new();   // a ordem das travas é sempre _gate e depois _dist
    readonly Dictionary<string, SaidaFilme> _saidas = new();
    readonly List<SaidaFilme> _ativas = new();      // sob _dist: as saídas com o SRT conectado nesta época
    // cache da época (sob _dist): os blocos desde o último quadro-chave do vídeo e até onde ela já mandou (s do filme)
    readonly List<byte[]> _gop = new();
    int _gopBytes;
    bool _gopValido;
    double _gopPts = double.NaN, _ptsMax = double.NaN, _ptsChave = double.NaN;
    volatile IFonteTs? _fonte;   // a fonte da época em curso; nula = parada (sem espectador, ou época nova a caminho). A thread de cada fonte só vale enquanto ela é a atual
    double _pos, _epocaInicio, _maiorPos;
    bool _tocando, _fimDoArquivo, _fonteParou;
    readonly Stopwatch _posLida = Stopwatch.StartNew(), _epocaRelogio = new(), _avancou = new();   // _avancou: desde que o maior carimbo mandado subiu pela última vez (sob _dist)
    bool _avisouLenta;
    volatile int _epoca;
    volatile bool _fim;
    long _bytesEnviados;
    double _velocidadeLeitura = double.NaN;

    public EnvioFilmeCopia(FonteInfo info, Func<IFonteTs> criarFonte) { _info = info; _criarFonte = criarFonte; }

    /// <summary>Upload que o envio pode usar (Mbps), dividido entre os espectadores: decide o ritmo do envio adiantado.</summary>
    public double UploadMbps { get; set; } = 30;
    /// <summary>A fonte falhou <see cref="MaxEpocasVazias"/> vezes seguidas sem mandar nada: o texto é para quem assiste. O envio não reabre mais até entrar um espectador novo.</summary>
    public event Action<string>? Falhou;
    volatile int _vazias;
    /// <summary>(época, ponto em que ela começa em s, tocando): chamado a cada época nova, para quem assiste reabrir o fluxo.</summary>
    public event Action<int, double, bool>? Mudou;

    public FonteInfo Info => _info;
    public int Epoca => _epoca;
    public bool Tocando { get { lock (_gate) return _tocando; } }
    public int Espectadores { get { lock (_gate) return _saidas.Count; } }
    public double Posicao { get { lock (_gate) return PosicaoLocked(); } }
    /// <summary>Quantos s do filme o envio já mandou à frente da posição (NaN sem época ou antes do primeiro quadro).</summary>
    public double Adiantado { get { double p = Posicao; lock (_dist) return _ptsMax - p; } }
    public bool LeuAteOFim { get { lock (_gate) return _fimDoArquivo; } }
    public long BytesEnviados => Interlocked.Read(ref _bytesEnviados);

    /// <summary>
    /// Quantos s de filme a fonte entregou por s de relógio nos últimos ~2 s desta época (NaN até medir). Abaixo de 1 com o envio querendo andar (não parado pelo adiantado) = a fonte não dá conta
    /// de alimentar quem assiste em tempo real: o filme do espectador acaba antes do próximo dado (caso de 22/11 com 3 espectadores).
    /// </summary>
    public double VelocidadeDeLeitura => Volatile.Read(ref _velocidadeLeitura);

    /// <summary>Soma ao total os bytes que o SRT de um espectador despachou (a tela mostra o Mbps).</summary>
    public void ContarBytes(long n) => Interlocked.Add(ref _bytesEnviados, n);

    double PosicaoLocked() => _tocando ? _pos + _posLida.Elapsed.TotalSeconds : _pos;

    // ------------------------------------------------------------------ espectadores

    /// <summary>Um espectador novo (a saída dele só recebe blocos depois do <see cref="Conectar"/>). O mesmo id duas vezes devolve a mesma saída.</summary>
    public SaidaFilme? Adicionar(string id, string apelido)
    {
        lock (_gate)
        {
            if (_fim) return null;
            if (_saidas.TryGetValue(id, out var ja)) return ja;
            if (_saidas.Count == 0) _vazias = 0;   // alguém novo chegou: outra chance
            return _saidas[id] = new SaidaFilme(id, apelido);
        }
    }

    public void Remover(string id)
    {
        SaidaFilme? s;
        lock (_gate)
        {
            if (!_saidas.Remove(id, out s)) return;
            if (_saidas.Count == 0) PararFonteLocked();   // sem espectadores não há buffer a alimentar nem motivo para manter o arquivo aberto
        }
        Desconectar(s);
        s.Fechar();
    }

    /// <summary>O SRT do espectador conectou nesta época: ele começa pelo último GOP guardado (ou pelo próximo quadro-chave, se não há) e segue ao vivo.</summary>
    public void Conectar(SaidaFilme s)
    {
        lock (_dist)
        {
            s.EpocaConectada = _epoca;
            s.Inicio = double.NaN;
            s.Zerar(esperarChave: !_gopValido);
            if (_gopValido)
            {
                for (int i = 0; i < _gop.Count; i++) s.Enfileirar(_gop[i], chave: i == 0);
                s.Inicio = _gopPts;
            }
            s.Ativa = true;
            if (!_ativas.Contains(s)) _ativas.Add(s);
        }
    }

    /// <summary>O SRT do espectador caiu (ele reabriu o player, ou a época mudou): para de receber até o próximo <see cref="Conectar"/>.</summary>
    public void Desconectar(SaidaFilme s)
    {
        lock (_dist) { s.Ativa = false; _ativas.Remove(s); s.Inicio = double.NaN; }
    }

    // ------------------------------------------------------------------ seguir a Sessão

    double _posAntes = double.NaN;
    bool _tocandoAntes, _saltoPendente, _emRajada;
    readonly Stopwatch _passada = new(), _ultimoSalto = new();
    int _saltosJuntos;

    /// <summary>
    /// A Sessão do anfitrião está em <paramref name="pos"/> (tocando ou não): chamar a cada mudança dela e a cada 400 ms. Abre uma época nova só quando quem assiste não tem esse ponto guardado.
    /// </summary>
    public void Seguir(double pos, bool tocando)
    {
        pos = Math.Max(0, pos);
        lock (_gate)
        {
            if (_fim) return;
            bool salto = RegistrarPosicaoLocked(pos, tocando);
            if (_saidas.Count == 0) return;   // ninguém assistindo: a primeira época abre quando alguém entra
            string? motivo = MotivoParaAbrirLocked(pos, out bool jaTem);
            if (jaTem) { _saltoPendente = false; _saltosJuntos = 0; return; }
            // uma rajada de pulos (seta segurada) vale só depois de 600 ms sem pulo novo: o primeiro vale na hora
            if (_saltoPendente && _emRajada && _ultimoSalto.ElapsedMilliseconds < 600) { if (salto) _saltosJuntos++; return; }
            if (_saltosJuntos > 0) Log.Info($"[filme] {_saltosJuntos + 1} pulos seguidos juntados numa época só.");
            _saltosJuntos = 0; _saltoPendente = false;
            IniciarEpocaLocked(pos, motivo!);
        }
    }

    /// <summary>Guarda a posição nova da Sessão; devolve se foi um pulo (mais de 1 s fora de onde ela andaria sozinha).</summary>
    bool RegistrarPosicaoLocked(double pos, bool tocando)
    {
        double passou = _passada.IsRunning ? _passada.Elapsed.TotalSeconds : 0;
        bool salto = !double.IsNaN(_posAntes) && Math.Abs(pos - (_posAntes + (_tocandoAntes ? passou : 0))) > 1.0;
        _posAntes = pos; _tocandoAntes = tocando; _passada.Restart();
        _pos = pos; _tocando = tocando; _posLida.Restart();
        _maiorPos = Math.Max(_maiorPos, pos);
        if (!salto) return false;
        _emRajada = _ultimoSalto.IsRunning && _ultimoSalto.ElapsedMilliseconds < 1500;   // outro pulo há menos de 1,5 s
        _ultimoSalto.Restart();
        _saltoPendente = true;
        return true;
    }

    string? MotivoParaAbrirLocked(double pos, out bool jaTem)
    {
        jaTem = false;
        if (_vazias >= MaxEpocasVazias) { jaTem = true; return null; }   // a fonte não entrega nada: reabrir só derruba o SRT de quem assiste
        if (_fonte == null) return "alguém começou a assistir";
        if (_fonteParou && _epocaRelogio.Elapsed.TotalSeconds > 3) return "a fonte do envio parou";
        if (!TemGuardadoLocked(pos, _saltoPendente, out string porque)) return porque;
        if (_saltoPendente && _saltosJuntos == 0) Log.Info($"[filme] pulo para {pos:0.#} s sem reabrir o envio: quem assiste já tem esse trecho.");
        jaTem = true;
        return null;
    }

    /// <summary>
    /// Quem assiste tem o ponto guardado? Para trás, até o primeiro quadro-chave que o espectador que entrou por último recebeu (e <see cref="AtrasS"/>). Para frente, até o que já foi mandado;
    /// num pulo, só até o último quadro-chave mandado (o player só procura entre quadros-chave guardados).
    /// </summary>
    bool TemGuardadoLocked(double pos, bool salto, out string porque)
    {
        porque = "";
        lock (_dist)
        {
            // época que ainda está abrindo (um arquivo grande leva s para soltar o 1º bloco): sem pulo de verdade ela tem tempo; reabrir a cada 400 ms deixava o filme preto (caso de 22/11)
            bool abrindo = !salto && _fonte != null && !_fonteParou && (_epocaRelogio.Elapsed.TotalSeconds < AberturaMaxS || _avancou.Elapsed.TotalSeconds < SemAvancoMaxS);
            if (double.IsNaN(_ptsMax))
            {
                if (Math.Abs(pos - _epocaInicio) < 2 || abrindo) return true;
                porque = salto ? $"pulo para {pos:0.#} s antes de a época anterior mandar algo" : $"a época não mandou nada em {AberturaMaxS:0} s (a Sessão está em {pos:0.#} s)";
                return false;
            }
            double atras = Math.Max(InicioDeTodosLocked(), _maiorPos - AtrasS);   // voltar não devolve o que o player já descartou: o limite segue o ponto mais distante da época
            if (pos < atras - 0.2) { porque = $"pulo para {pos:0.#} s, antes do que quem assiste tem guardado ({atras:0.#} s)"; return false; }
            double frente = _fimDoArquivo ? double.MaxValue : salto ? _ptsChave : _ptsMax + 2;
            if (pos > frente && !abrindo) { porque = $"pulo para {pos:0.#} s, além do que já foi mandado ({(salto ? _ptsChave : _ptsMax):0.#} s)"; return false; }
            return true;
        }
    }

    /// <summary>Sob <c>_dist</c>: o começo do que TODOS os espectadores conectados têm (o maior "primeiro quadro-chave" entre eles).</summary>
    double InicioDeTodosLocked()
    {
        double inicio = double.NegativeInfinity;
        foreach (var s in _ativas) if (!double.IsNaN(s.Inicio) && s.Inicio > inicio) inicio = s.Inicio;
        return double.IsNegativeInfinity(inicio) ? _epocaInicio - 10 : inicio;
    }

    /// <summary>Quem assiste não tem o ponto (o cache dele começa depois, ou o fluxo não chegou): abre uma época nova no ponto da Sessão, no máximo uma a cada 3 s.</summary>
    public void Recarregar(string quem)
    {
        double faltaS;
        int epocaDoPedido;
        lock (_gate)
        {
            if (_fim || _saidas.Count == 0 || _saltoPendente) return;   // com um pulo pendente, a época dele já vem
            faltaS = IdadeMinimaDaEpocaS - _epocaRelogio.Elapsed.TotalSeconds;
            epocaDoPedido = _epoca;
            if (faltaS <= 0) { IniciarEpocaLocked(PosicaoLocked(), $"{quem} não tinha o trecho guardado"); return; }
            if (_recargaAdiada) return;
            _recargaAdiada = true;
        }
        // caso de 22/11: quem entra provoca uma época para todos e pede recarga na hora; descartar o pedido deixava o celular esperando 15 s por um trecho que ninguém ia mandar
        Log.Info($"[filme] pedido de {quem} chegou {IdadeMinimaDaEpocaS - faltaS:0.0} s depois da época {epocaDoPedido}; reabro em {faltaS:0.0} s se ainda for preciso.");
        _ = Task.Delay(TimeSpan.FromSeconds(faltaS + 0.05)).ContinueWith(_ => RecargaAdiada(quem, epocaDoPedido));
    }

    void RecargaAdiada(string quem, int epocaDoPedido)
    {
        lock (_gate)
        {
            _recargaAdiada = false;
            if (_fim || _saidas.Count == 0 || _saltoPendente || _epoca != epocaDoPedido) return;   // outra época abriu nesse meio-tempo: ela já traz o ponto
            IniciarEpocaLocked(PosicaoLocked(), $"{quem} não tinha o trecho guardado");
        }
    }

    const double IdadeMinimaDaEpocaS = 3;
    bool _recargaAdiada;

    // ------------------------------------------------------------------ épocas

    /// <summary>
    /// O anfitrião trocou a faixa de áudio que o envio manda (a fonte nova lê a faixa nova): com uma época aberta, abre outra no ponto da Sessão, como o <c>TrocarAudio</c> do Windows.
    /// Quem assiste reabre o SRT ao receber o <c>filme_stream_epoca</c>; o áudio antigo some no corte.
    /// </summary>
    public void TrocarFonte(FonteInfo nova, Func<IFonteTs> criarFonte, string motivo)
    {
        lock (_gate)
        {
            if (_fim) return;
            _info = nova; _criarFonte = criarFonte;
            if (_fonte != null) IniciarEpocaLocked(PosicaoLocked(), motivo);
        }
    }

    void IniciarEpocaLocked(double pos, string motivo)
    {
        PararFonteLocked();
        int ep = ++_epoca;
        _epocaInicio = _maiorPos = pos; _epocaRelogio.Restart(); _avancou.Restart(); _avisouLenta = false; _fimDoArquivo = _fonteParou = false;
        Log.Info($"[filme] época {ep}: envio a partir de {pos:0.#} s ({motivo}), {_saidas.Count} espectador(es), {(_tocando ? "tocando" : "pausado")}.");
        IFonteTs fonte;
        try { fonte = _fonte = _criarFonte(); }
        catch (Exception e) { Log.Info($"[filme] o envio do filme não abriu a fonte: {e.Message}"); _fonteParou = true; return; }
        new Thread(() => LerFonte(fonte, ep, pos)) { IsBackground = true, Name = "filme-copia-fonte" }.Start();
        bool tocando = _tocando;
        Task.Run(() => Mudou?.Invoke(ep, pos, tocando));
    }

    void PararFonteLocked()
    {
        _fonte = null;   // a thread da época vê que a época mudou e fecha a fonte dela
        lock (_dist)
        {
            _gop.Clear(); _gopBytes = 0; _gopValido = false; _gopPts = double.NaN;
            _ptsMax = _ptsChave = double.NaN;
            foreach (var s in _ativas) { s.Ativa = false; s.Inicio = double.NaN; }
            _ativas.Clear();   // o SRT de cada um reabre na época nova e chama Conectar de novo
        }
    }

    /// <summary>A thread da época: abre a fonte no ponto, lê no ritmo certo e reparte. Ao acabar (outra época, fim do arquivo ou erro), fecha a fonte.</summary>
    void LerFonte(IFonteTs fonte, int epoca, double pos)
    {
        try
        {
            var abertura = Stopwatch.StartNew();
            fonte.Abrir(pos);
            Log.Info($"[filme] época {epoca}: fonte aberta em {abertura.ElapsedMilliseconds} ms.");
            if (!Vale(fonte)) return;
            Repartir(fonte, epoca);
        }
        catch (Exception e) when (Vale(fonte))
        {
            Log.Info($"[filme] [aviso] a leitura do filme parou na época {epoca}: {e.Message}");
            bool desistiu = false;
            lock (_gate) if (Vale(fonte)) { _fonteParou = true; desistiu = ++_vazias == MaxEpocasVazias; }
            if (desistiu) Falhou?.Invoke("A transmissão do filme não conseguiu ler o arquivo no celular. Baixe o arquivo para assistir.");
        }
        finally { try { fonte.Dispose(); } catch (Exception e) { Log.Info("[filme] a fonte do envio não fechou direito: " + e.Message); } }
    }

    /// <summary>
    /// O ritmo é daqui: no máximo <see cref="RitmoAgora"/> s de filme por s (para caber no upload; uma rajada solta no SRT "live" vira bloco descartado por atraso) e nunca mais de
    /// <see cref="AdiantadoS"/> à frente da Sessão. Parar de ler segura a fonte, sem encerrar a época.
    /// </summary>
    void Repartir(IFonteTs fonte, int epoca)
    {
        double credito = RajadaS, ultimoPts = double.NaN, ptsAmostra = double.NaN;
        var relogio = Stopwatch.StartNew(); var amostra = Stopwatch.StartNew();
        long lidos = 0;
        Volatile.Write(ref _velocidadeLeitura, double.NaN);
        while (Vale(fonte))
        {
            double dt = relogio.Elapsed.TotalSeconds; relogio.Restart();
            double adiantado = Adiantado;
            credito = Math.Min(Math.Max(RajadaS, credito), credito + dt * RitmoAgora(alcancando: !(adiantado >= AlcanceAteS)));
            if (adiantado > AdiantadoS) { Thread.Sleep(100); continue; }
            if (credito <= 0) { Thread.Sleep(15); continue; }
            if (fonte.Proximo() is not { } bloco) { AcabouLeitura(fonte, epoca, lidos); return; }
            if (++lidos == 1) _vazias = 0;
            if (lidos == 1) Log.Info($"[filme] época {epoca}: primeiro bloco em {_epocaRelogio.ElapsedMilliseconds} ms ({(bloco.Chave ? "quadro-chave" : "sem quadro-chave")}, {bloco.Dados.Length} bytes).");
            if (!Publicar(bloco, fonte, out double pts)) return;
            if (double.IsNaN(pts)) continue;
            MedirLeitura(pts, ref ptsAmostra, amostra);
            if (!double.IsNaN(ultimoPts) && pts > ultimoPts) credito -= pts - ultimoPts;
            ultimoPts = double.IsNaN(ultimoPts) ? pts : Math.Max(ultimoPts, pts);
        }
    }

    /// <summary>Esta fonte ainda é a da época em curso? Falso depois de outra época, de o último espectador sair ou do <see cref="Dispose"/>.</summary>
    bool Vale(IFonteTs fonte) => !_fim && ReferenceEquals(_fonte, fonte);

    /// <summary>A cada ~2 s: s de filme lidos por s de relógio (<see cref="VelocidadeDeLeitura"/>). Só conta enquanto a fonte anda; quando o adiantado a segura, o laço nem passa por aqui.</summary>
    void MedirLeitura(double pts, ref double ptsAmostra, Stopwatch amostra)
    {
        if (double.IsNaN(ptsAmostra)) { ptsAmostra = pts; amostra.Restart(); return; }
        if (amostra.Elapsed.TotalSeconds < 2) return;
        double v = (pts - ptsAmostra) / amostra.Elapsed.TotalSeconds;
        Volatile.Write(ref _velocidadeLeitura, v);
        ptsAmostra = pts; amostra.Restart();
        if (v < 0.9 && Adiantado < 2 && !_avisouLenta)
        {
            _avisouLenta = true;
            Log.Info($"[filme] [aviso] a leitura do filme anda a {v:0.00}x do tempo real e o envio está atrasado em relação à posição (época {Epoca}): quem assiste vai travar. Provável: o celular não dá conta de ler, montar e mandar para {Espectadores} espectador(es).");
        }
    }

    void AcabouLeitura(IFonteTs fonte, int epoca, long lidos)
    {
        bool desistiu = false;
        lock (_gate)
        {
            if (!Vale(fonte)) return;
            if (lidos > 0) { _fimDoArquivo = true; Log.Info($"[filme] o envio leu o filme até o fim (época {epoca})."); }
            else
            {
                _fonteParou = true;
                desistiu = ++_vazias == MaxEpocasVazias;
                Log.Info($"[filme] [aviso] a fonte do envio acabou sem mandar nada (época {epoca}, {_vazias} seguida(s)).");
            }
        }
        if (desistiu) Falhou?.Invoke("A transmissão do filme não conseguiu ler o arquivo no celular. Baixe o arquivo para assistir.");
    }

    /// <summary>
    /// S de filme por s de relógio: o upload dividido pelo fluxo de cada espectador. No ritmo calmo (com folga de 30%) fica entre 1,1 e 2, como no Windows; <paramref name="alcancando"/> (o envio ainda
    /// não está <see cref="AlcanceAteS"/> à frente da posição) usa 90% do upload e vai até 4x: no campo de 22/11 o ritmo calmo (1,33x com 3 espectadores) levou ~10 s para alcançar a posição depois
    /// de cada época, e é esse o tempo em que quem assiste fica sem imagem. A bancada no celular (3 espectadores em loopback) rodou sem descartar nada a até ~20 Mbit/s no total.
    /// </summary>
    public static double Ritmo(double uploadMbps, double fluxoMbps, int espectadores, bool alcancando)
    {
        double r = uploadMbps * (alcancando ? 0.9 : 0.7) / (Math.Max(0.1, fluxoMbps) * Math.Max(1, espectadores));
        return Math.Clamp(double.IsFinite(r) ? r : 1.1, 1.1, alcancando ? 4.0 : 2.0);
    }

    double RitmoAgora(bool alcancando)
    {
        int n; lock (_gate) n = _saidas.Count;
        return Ritmo(UploadMbps, _info.Mbps, n, alcancando);
    }

    /// <summary>Guarda o bloco no cache da época e põe na fila de cada espectador conectado. Falso se a época mudou no meio (quem chamou para de ler). <paramref name="ptsMax"/>: até onde ela já mandou.</summary>
    bool Publicar(BlocoTs b, IFonteTs fonte, out double ptsMax)
    {
        lock (_dist)
        {
            ptsMax = _ptsMax;
            if (!Vale(fonte)) return false;
            Guardar(b);
            foreach (var s in _ativas) s.Enfileirar(b.Dados, b.Chave);
            ptsMax = _ptsMax;
            return true;
        }
    }

    /// <summary>Sob <c>_dist</c>: o trecho desde o último quadro-chave do vídeo e até onde a época já mandou.</summary>
    void Guardar(BlocoTs b)
    {
        if (b.Video)
        {
            if (double.IsNaN(_ptsMax) || b.PtsS > _ptsMax) { _ptsMax = b.PtsS; _avancou.Restart(); }
            if (b.Chave) NovoGop(b.PtsS);
        }
        if (!_gopValido) return;
        _gop.Add(b.Dados); _gopBytes += b.Dados.Length;
        if (_gopBytes <= MaxGopBytes) return;
        _gop.Clear(); _gopBytes = 0; _gopValido = false;   // GOP grande demais para guardar: quem entra agora começa no próximo
    }

    void NovoGop(double pts)
    {
        _gop.Clear(); _gopBytes = 0; _gopValido = true;
        _ptsChave = _gopPts = pts;
        foreach (var s in _ativas) if (double.IsNaN(s.Inicio)) s.Inicio = pts;   // quem conectou sem o trecho guardado começa a ter o que procurar daqui
    }

    public void Dispose()
    {
        List<SaidaFilme> todas;
        lock (_gate) { _fim = true; todas = _saidas.Values.ToList(); _saidas.Clear(); _tocando = false; PararFonteLocked(); }
        foreach (var s in todas) s.Fechar();
    }
}
