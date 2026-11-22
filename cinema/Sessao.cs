using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// A linha do tempo do filme (spec 010, seção 7). O anfitrião é o dono de um único estado {tocando|pausado, posição, instante T no relógio dele};
/// quem assiste aplica esse estado no próprio mpv. Todo comando é "a partir do instante T, da posição P": todos começam juntos em vez de
/// cada um quando a mensagem chega (7.3). Depois, a correção contínua (7.4) mantém cada mpv no ponto certo.
/// Spike S1 (2026-11-11): só a hora marcada já deixa dois mpv a menos de 1 ms; a correção traz 200 ms de atraso para menos de 15 ms em ~5 s.
/// </summary>
public sealed class Sessao : IDisposable
{
    /// <summary>
    /// Folga da hora marcada: a ida e volta + isso (7.3 fala em 150 a 300 ms). Era 300; no teste da interface nova (11/11) o ▶ parecia
    /// travado, e quem recebe atrasado já começa um pouco à frente e é corrigido (AplicarAsync + 7.4).
    /// </summary>
    const int FolgaMs = 150;

    /// <summary>Ninguém mais com o arquivo aberto nem assistindo pela transmissão: não há com quem sincronizar, então tocar e pausar valem na hora, sem hora marcada.</summary>
    bool Sozinho => Anfitriao && _comArquivo.Count == 0 && (FluxoIds?.Invoke().Any() != true);

    /// <summary>
    /// Quem assiste pela transmissão, pelo id da conta (spec 011; o MainForm preenche no anfitrião). Eles seguem a mesma linha do tempo pelo buffer do mpv: contam para a hora marcada
    /// (todos começam juntos) e para a espera do pulo (D4), mas não entram nos <see cref="Amigos"/> (não têm arquivo para ficar "pronto").
    /// </summary>
    public Func<IEnumerable<string>>? FluxoIds { get; set; }

    /// <summary>O estado atual (filme_estado), para quem começa a assistir pela transmissão com o filme já rolando.</summary>
    public JsonObject EstadoAtual() => Estado();

    public bool Anfitriao { get; }
    public string Filme { get; }
    public string Nome { get; }
    public string NomeAnfitriao { get; }
    readonly IPlayerFilme _mpv;
    readonly Func<long?> _relogio;
    readonly Func<double> _idaVolta;
    readonly Action<JsonObject> _enviar;
    readonly string _meuNome;
    readonly string _meuId;
    /// <summary>Id → nome de quem já falou nesta sessão: só para o texto de tela (o nome pode mudar e repetir; quem manda é o id).</summary>
    readonly Dictionary<string, string> _nomes = new();
    /// <summary>O nome atual da pessoa na sala do grupo (o MainForm preenche); sem ele vale o último nome que ela mandou.</summary>
    public Func<string, string?>? NomeNaSala { get; set; }
    public string NomeDe(string id) => NomeNaSala?.Invoke(id) ?? (_nomes.TryGetValue(id, out var n) ? n : id);

    /// <summary>
    /// Quem mandou a mensagem: o id que o MainForm carimba a partir da ligação (campo "id"; o app antigo não manda nada e a ligação já diz quem é).
    /// Sem id (mensagem sem ligação, teste), cai no nome, como era.
    /// </summary>
    string Quem(JsonObject m, string padrao = "amigo")
    {
        string nome = m["nome"]?.GetValue<string>() ?? padrao;
        string id = m["id"]?.GetValue<string>() is { Length: > 0 } i ? i : nome;
        _nomes[id] = nome;
        return id;
    }

    public bool Tocando { get; private set; }
    double _pos;
    long _t;
    int _seq;
    public bool TodosControlam { get; private set; }
    /// <summary>Para o anfitrião: quem entrou e o estado de cada um (4.3), pelo id da conta.</summary>
    public Dictionary<string, string> Amigos { get; } = new();
    /// <summary>4.3: o primeiro ▶ do anfitrião espera todos ficarem prontos (padrão); "começar agora" fura a espera.</summary>
    public bool EsperarTodos { get; set; } = true;
    public bool Aguardando { get; private set; }
    double _posInicio;
    /// <summary>Como estava cada um quando o filme começou (relatório do teste).</summary>
    public string? ComecouCom { get; private set; }
    /// <summary>Quem já recebeu as permissões e o estado (quem tem o arquivo aberto, conferindo ou pronto).</summary>
    readonly HashSet<string> _comArquivo = new();
    // D4: depois de pular, espera até 5 s todos carregarem o trecho novo
    readonly HashSet<string> _carregados = new();
    bool _pulo;
    volatile int _meuCarregado;
    public long UltimaEsperaPuloMs { get; private set; } = -1;
    /// <summary>Quem não carregou o trecho (ou o episódio) a tempo no último pulo, separado por vírgula; nulo se todos carregaram (telemetria da fila).</summary>
    public string? UltimoAtraso { get; private set; }
    static readonly int TesteLentoMs = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_LENTO"), out var l) ? l : 0;

    public bool TodosProntos => Amigos.Values.All(v => v == "pronto");
    string Lista() => string.Join(",", Amigos.Select(kv => $"{NomeDe(kv.Key)}={kv.Value}"));

    int _gen;
    double _vel = 1;
    bool _alinhado;
    public double UltimoErroMs { get; private set; } = double.NaN;
    readonly List<double> _erros = new();
    int _pulos;

    public event Action? Mudou;
    public event Action<string>? Aviso;

    /// <param name="relogio">Hora do anfitrião em µs (nulo enquanto quem assiste ainda não tem o relógio comum, 7.2).</param>
    public Sessao(bool anfitriao, string filme, string nome, string nomeAnfitriao, string meuNome, IPlayerFilme mpv, Func<long?> relogio, Func<double> idaVolta, Action<JsonObject> enviar, string meuId = "")
    {
        Anfitriao = anfitriao; Filme = filme; Nome = nome; NomeAnfitriao = nomeAnfitriao; _meuNome = meuNome; _meuId = meuId;
        _mpv = mpv; _relogio = relogio; _idaVolta = idaVolta; _enviar = enviar;
    }

    /// <summary>Onde o filme deveria estar agora, pela linha do tempo comum.</summary>
    public double Posicao
    {
        get
        {
            if (!Tocando || _relogio() is not long now) return _pos;
            return _pos + Math.Max(0, now - _t) / 1e6;
        }
    }

    /// <summary>Relógio local em µs (o mesmo do Tunnel.NowUs), sem depender da rede.</summary>
    static long AgoraUs => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public bool PodeControlar => Anfitriao || TodosControlam;
    /// <summary>Velocidade que a correção contínua (7.4) aplicou no mpv agora (1 = normal).</summary>
    public double Velocidade => _vel;
    /// <summary>O filme já começou alguma vez (o card mostra "pausado" e não "no aguardo").</summary>
    public bool Comecou => _seq > 0;

    // ------------------------------------------------------------------ comandos (anfitrião aplica; quem assiste pede)

    public void Tocar(double? pos = null)
    {
        if (Aguardando) { ComecarAgora(); return; }
        if (Anfitriao && _seq == 0 && EsperarTodos && !TodosProntos)
        {
            Aguardando = true;
            _posInicio = pos ?? _pos;
            Aviso?.Invoke("O filme começa sozinho quando todos estiverem prontos. ▶ de novo começa agora.");
            Mudou?.Invoke();
            return;
        }
        Comando("tocar", pos);
    }

    /// <summary>4.3 "começar agora": quem não está pronto entra quando terminar (a stream do modo B vem na F2).</summary>
    public void ComecarAgora()
    {
        if (!Aguardando) return;
        Aguardando = false;
        ComecouCom = Lista();
        Comando("tocar", _posInicio);
    }

    void TalvezComecar()
    {
        if (!Aguardando || !TodosProntos) return;
        Aguardando = false;
        ComecouCom = Lista();
        Log.Info($"[filme] todos prontos ({ComecouCom}): começando.");
        Comando("tocar", _posInicio);
    }

    public void Pausar() { if (Aguardando) { Aguardando = false; Mudou?.Invoke(); return; } Comando("pausar", null); }
    public void Ir(double pos) => Comando("ir", Math.Max(0, pos));

    void Comando(string acao, double? pos)
    {
        if (!Anfitriao)
        {
            if (!TodosControlam) { Aviso?.Invoke($"Só {NomeAnfitriao} controla o filme."); return; }
            _enviar(new JsonObject { ["type"] = "filme_pedido", ["filme"] = Filme, ["acao"] = acao, ["pos"] = pos, ["nome"] = _meuNome, ["id"] = _meuId.Length > 0 ? _meuId : null });
            return;
        }
        long now = _relogio() ?? AgoraUs;
        long t = Sozinho ? now : now + (long)((Math.Max(0, _idaVolta()) + FolgaMs) * 1000);
        switch (acao)
        {
            case "tocar": Publicar(true, pos ?? Posicao, t); break;
            // pausa marcada: todos param no mesmo ponto do filme
            case "pausar" when Tocando: Publicar(false, _pos + (t - _t) / 1e6, t); break;
            // ir para outro ponto tocando: todos pausam no ponto novo, esperam carregar (D4) e voltam juntos
            case "ir" when Tocando: _ = PularAsync(pos ?? Posicao); break;
            case "ir": Publicar(false, pos ?? Posicao, now); break;
        }
    }

    /// <summary>
    /// D4 (spec 010, 17): depois de pular, cada um avisa (filme_carregado) quando o trecho novo carregou. O anfitrião espera todos
    /// os prontos, até 5 s; quem demora mais fica para trás e alcança sozinho pela correção contínua (7.4).
    /// </summary>
    async Task PularAsync(double pos)
    {
        _carregados.Clear();
        Publicar(false, pos, _relogio() ?? AgoraUs, pulo: true);
        int seq = _seq;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<string> faltam = new();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (_seq != seq) return; // outro comando passou na frente
            faltam = Amigos.Where(kv => kv.Value == "pronto" && !_carregados.Contains(kv.Key)).Select(kv => kv.Key)
                .Concat((FluxoIds?.Invoke() ?? Enumerable.Empty<string>()).Where(n => !_carregados.Contains(n))).ToList();
            if (faltam.Count == 0 && _meuCarregado == seq) break;
            await Task.Delay(20);
        }
        if (_seq != seq) return;
        UltimaEsperaPuloMs = sw.ElapsedMilliseconds;
        var nomesFaltam = faltam.Select(NomeDe).ToList();
        UltimoAtraso = faltam.Count > 0 ? string.Join(",", nomesFaltam) : null;
        if (faltam.Count > 0) Aviso?.Invoke($"Segui sem esperar {string.Join(", ", nomesFaltam)}: demorou mais de 5 s para carregar.");
        Log.Info($"[filme] pulo: todos carregaram em {UltimaEsperaPuloMs} ms" + (faltam.Count > 0 ? $" (segui sem {string.Join(", ", nomesFaltam)})." : "."));
        Comando("tocar", pos);
    }

    /// <summary>
    /// Fila de episódios: o arquivo novo já está carregado no mpv, parado em 0. Todos pausam em 0, avisam que carregaram (o mesmo D4 do pulo) e o anfitrião toca com a hora
    /// marcada; quem passar de 5 s alcança sozinho pela correção contínua. Só o anfitrião chama. Devolve quando o episódio começou a tocar.
    /// </summary>
    public Task ComecarDaFilaAsync() => Anfitriao ? PularAsync(0) : Task.CompletedTask;

    /// <summary>
    /// A fila passou para o episódio seguinte: esta sessão acabou, mas o mpv, a ligação e quem assiste continuam. Só para o que ela faz (a correção contínua e o que estiver
    /// sendo aplicado) sem fechar o player, que a sessão nova vai usar.
    /// </summary>
    public void Soltar() { _gen++; _seq++; Tocando = false; }

    /// <summary>
    /// A sessão do episódio novo herda as escolhas e as pessoas da anterior (todos controlam, quem entrou e o estado de cada um), para ninguém precisar entrar de novo e
    /// o "Na sessão" não esvaziar na troca. Quem não tiver o arquivo novo sai pela própria ligação e o anfitrião o tira da lista.
    /// </summary>
    public void HerdarDe(Sessao velha)
    {
        TodosControlam = velha.TodosControlam;
        EsperarTodos = velha.EsperarTodos;
        FluxoIds = velha.FluxoIds;
        NomeNaSala = velha.NomeNaSala;
        foreach (var kv in velha._nomes) _nomes[kv.Key] = kv.Value;
        foreach (var kv in velha.Amigos) Amigos[kv.Key] = kv.Value;
        foreach (var n in velha._comArquivo) _comArquivo.Add(n);
    }

    void Publicar(bool tocando, double pos, long t, bool pulo = false)
    {
        _seq++;
        _pulo = pulo;
        Definir(tocando, pos, t, pulo);
        _enviar(Estado());
    }

    JsonObject Estado() => new()
    {
        ["type"] = "filme_estado", ["filme"] = Filme, ["seq"] = _seq, ["tocando"] = Tocando, ["pos"] = _pos, ["t"] = _t, ["todos"] = TodosControlam, ["pulo"] = _pulo,
    };

    public void AmigoSaiu(string id) { _comArquivo.Remove(id); if (Amigos.Remove(id)) { Mudou?.Invoke(); TalvezComecar(); } }

    public void MudarPermissao(bool todos)
    {
        if (!Anfitriao) return;
        TodosControlam = todos;
        _enviar(new JsonObject { ["type"] = "filme_permissoes", ["filme"] = Filme, ["todos"] = todos });
        Mudou?.Invoke();
    }

    /// <summary>Mensagem filme_* do amigo (já na thread da janela).</summary>
    public void Receber(JsonObject m)
    {
        if (m["filme"]?.GetValue<string>() != Filme) return;
        switch (m["type"]?.GetValue<string>())
        {
            case "filme_estado" when !Anfitriao:
                int seq = m["seq"]?.GetValue<int>() ?? 0;
                TodosControlam = m["todos"]?.GetValue<bool>() ?? false;
                if (seq <= _seq) { Mudou?.Invoke(); return; }
                _seq = seq;
                Definir(m["tocando"]?.GetValue<bool>() ?? false, m["pos"]?.GetValue<double>() ?? 0, m["t"]?.GetValue<long>() ?? 0, m["pulo"]?.GetValue<bool>() ?? false);
                break;
            case "filme_carregado" when Anfitriao:
                if ((m["seq"]?.GetValue<int>() ?? -1) == _seq) _carregados.Add(Quem(m, ""));
                break;
            case "filme_permissoes" when !Anfitriao:
                TodosControlam = m["todos"]?.GetValue<bool>() ?? false;
                Mudou?.Invoke();
                break;
            case "filme_pronto" when Anfitriao:
            {
                // 4.3: cada um é "baixando" (com % e tempo; a vazão que ele mede já mostra o upload dividido entre quem baixa),
                // "conferindo" (tem o arquivo e o app confere contra o manifesto, 4.1) ou "pronto"
                string id = Quem(m);
                bool temArquivo = true;
                if (m["baixando"] is JsonObject b)
                {
                    double eta = b["eta_s"]?.GetValue<double>() ?? -1;
                    Amigos[id] = $"baixando {b["pct"]?.GetValue<int>() ?? 0}%" + (eta >= 0 ? $" ({Progresso.Faltam(eta)})" : "");
                    temArquivo = false;
                }
                else if (m["conferindo"] is JsonObject c) Amigos[id] = $"conferindo {c["pct"]?.GetValue<int>() ?? 0}%";
                else if (m["defeito"] is JsonNode d) Amigos[id] = $"arquivo com {d} pedaço(s) com defeito";
                else
                {
                    temArquivo = m["pronto"]?.GetValue<bool>() ?? false;
                    Amigos[id] = temArquivo ? "pronto" : "outro arquivo";
                }
                // quem abriu o arquivo recebe as permissões (seção 10) e, com o filme rolando, o estado atual para entrar no ponto certo
                // ("primeiro" = acabou de abrir o arquivo: quem fechou o app e voltou recebe de novo)
                if (temArquivo && (_comArquivo.Add(id) | (m["primeiro"]?.GetValue<bool>() ?? false)))
                {
                    _enviar(new JsonObject { ["type"] = "filme_permissoes", ["filme"] = Filme, ["todos"] = TodosControlam });
                    if (_seq > 0) _enviar(Estado());
                }
                Mudou?.Invoke();
                TalvezComecar();
                break;
            }
            case "filme_pedido" when Anfitriao:
                string acao = m["acao"]?.GetValue<string>() ?? "";
                string quem = NomeDe(Quem(m));
                if (!TodosControlam)
                {
                    Aviso?.Invoke($"{quem} pediu para {(acao == "pausar" ? "pausar" : acao == "tocar" ? "continuar" : "pular")}.");
                    return;
                }
                double? pos = m["pos"]?.GetValue<double?>();
                Aviso?.Invoke(acao switch { "pausar" => $"{quem} pausou.", "tocar" => $"{quem} continuou.", _ => $"{quem} foi para {Tempo(pos ?? 0)}." });
                Comando(acao, pos);
                break;
        }
    }

    // ------------------------------------------------------------------ aplicar no mpv

    void Definir(bool tocando, double pos, long t, bool pulo = false)
    {
        Tocando = tocando; _pos = pos; _t = t;
        int gen = ++_gen, seq = _seq;
        Mudou?.Invoke();
        _ = Task.Run(() => AplicarAsync(gen, tocando, pos, t, pulo ? seq : -1));
    }

    /// <param name="pulo">Seq do pulo (D4): depois de carregar o trecho novo, avisa o anfitrião; -1 = não é pulo.</param>
    async Task AplicarAsync(int gen, bool tocando, double pos, long t, int pulo = -1)
    {
        try
        {
            if (_relogio() is not long now) { Aviso?.Invoke("Ainda acertando o relógio com o anfitrião; o filme entra em instantes."); now = await EsperarRelogioAsync(gen); }
            await _mpv.Velocidade(1.0); _vel = 1;
            if (!tocando)
            {
                if (t > now) await EsperarAte(t);
                if (gen != _gen) return;
                await _mpv.Pausar(true);
                await _mpv.Ir(pos);
                if (pulo >= 0)
                {
                    await _mpv.EsperarIrAsync();
                    if (TesteLentoMs > 0) await Task.Delay(TesteLentoMs); // teste: alguém que demora para carregar
                    if (gen != _gen) return;
                    if (Anfitriao) _meuCarregado = pulo;
                    else _enviar(new JsonObject { ["type"] = "filme_carregado", ["filme"] = Filme, ["seq"] = pulo, ["nome"] = _meuNome, ["id"] = _meuId.Length > 0 ? _meuId : null });
                }
                return;
            }
            if (Sozinho && t <= now)
            {
                // sozinho: despausa na hora; só procura o ponto se o mpv estiver longe dele (procurar à toa custa ~100 ms)
                double atual = await _mpv.PosicaoAsync();
                if (double.IsNaN(atual) || Math.Abs(atual - pos) > 0.25) { await _mpv.Ir(pos); await _mpv.EsperarIrAsync(); }
                if (gen != _gen) return;
                await _mpv.Pausar(false);
                // a linha do tempo começa quando o mpv de fato despausou: senão a correção (7.4) acelera à toa para tirar esses ms
                if (gen == _gen && _relogio() is long agora) { _pos = pos; _t = agora + (long)(Math.Clamp(_mpv.AtrasoPartidaS, 0, 1) * 1e6); } // o som só anda AtrasoPartidaS depois
                _alinhado = false;
                await CorrigirAsync(gen);
                return;
            }
            // mensagem atrasada (ou quem entrou no meio): recomeça um pouco à frente, já no ponto em que todos estarão
            long inicio = t - now >= 150_000 ? t : now + 400_000;
            await _mpv.Pausar(true);
            double atraso = Math.Clamp(_mpv.AtrasoPartidaS, 0, 1);
            for (int tentativa = 0; ; tentativa++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _mpv.Ir(pos + (inicio - t) / 1e6);
                await _mpv.EsperarIrAsync();
                if (gen != _gen) return;
                // procurar demorou mais que a folga (ExoPlayer: ~1 s): a hora marcada já passou. Procura de novo, já para um instante à frente
                if (tentativa >= 2 || _relogio() is not long agora || agora + (long)(atraso * 1e6) <= inicio) break;
                inicio = agora + (long)(Math.Max(sw.Elapsed.TotalSeconds, 0.3) * 1e6);
                Log.Info($"[filme] a procura passou da hora marcada; procuro de novo para daqui a {(inicio - agora) / 1000} ms.");
            }
            // o player leva AtrasoPartidaS até o som andar depois de despausar: despausa esse tanto antes da hora marcada (Mpv: 0)
            await EsperarAte(inicio - (long)(Math.Clamp(_mpv.AtrasoPartidaS, 0, 1) * 1e6));
            if (gen != _gen) return;
            await _mpv.Pausar(false);
            _alinhado = false;
            await CorrigirAsync(gen);
        }
        catch (Exception e) { Log.Info($"[filme] falha ao aplicar o estado: {e.Message}"); }
    }

    async Task<long> EsperarRelogioAsync(int gen)
    {
        while (gen == _gen) { if (_relogio() is long n) return n; await Task.Delay(100); }
        throw new OperationCanceledException();
    }

    /// <summary>Até o instante (µs no relógio do anfitrião): dorme até perto e gira o último milissegundo (despausar na hora é o que dá o &lt;1 ms do S1).</summary>
    async Task EsperarAte(long alvoUs)
    {
        while (_relogio() is long n && alvoUs - n > 20_000) await Task.Delay((int)Math.Min(200, (alvoUs - n - 15_000) / 1000));
        while (_relogio() is long n && alvoUs - n > 2_000) Thread.Sleep(1);
        while (_relogio() is long n && n < alvoUs) Thread.SpinWait(50);
    }

    /// <summary>
    /// Tabela da 7.4: dentro da zona morta do player (mpv 15 ms) nada; até 1 s muda a velocidade (ver os degraus abaixo; no mpv ±5%, alcança em ~2 s, mantendo o tom); mais de 1 s pula.
    /// A posição vem do audio-pts (contínuo); sem som, do time-pos.
    /// </summary>
    async Task CorrigirAsync(int gen)
    {
        await Task.Delay(300);
        while (gen == _gen && Tocando && !_mpv.Saiu)
        {
            long? a0 = _relogio();
            double p = await _mpv.PosicaoAudioAsync();
            long? a1 = _relogio();
            if (double.IsNaN(p)) p = await _mpv.PosicaoAsync();
            if (gen != _gen) break;
            if (!double.IsNaN(p) && a0 is long x && a1 is long y)
            {
                double esperado = _pos + ((x + y) / 2 - _t) / 1e6;
                double err = p - esperado;
                UltimoErroMs = err * 1000;
                if (Math.Abs(err) < _mpv.ZonaMortaS) _alinhado = true;
                if (_alinhado) lock (_erros) _erros.Add(Math.Abs(err) * 1000);
                if (Math.Abs(err) > 1)
                {
                    _pulos++; _alinhado = false;
                    Log.Info($"[filme] fora do ponto {err * 1000:N0} ms: pulo.");
                    await _mpv.Velocidade(1.0); _vel = 1;
                    await _mpv.Ir(esperado + 0.3);
                }
                else
                {
                    // Dois degraus de velocidade: no caso frequente (erro até 200 ms) só um desvio pequeno; acima disso, até VelocidadeMaxDesvio.
                    // Por quê: no celular o Sonic mantendo o tom estala (campo 2026-11-22), então a velocidade muda o tom junto, o que é aceitável
                    // se for pequeno e raro. No mpv os dois degraus são 0,05 (o tom é mantido), então nada muda no Windows.
                    double pequeno = Math.Clamp(_mpv.VelocidadeDesvioPequeno, 0.01, 0.5);
                    double lim = Math.Abs(err) > 0.2 ? Math.Clamp(_mpv.VelocidadeMaxDesvio, pequeno, 0.5) : pequeno;
                    // Histerese: só começa a corrigir acima da zona morta do player e, uma vez corrigindo, só para abaixo de um terço dela
                    // (entre as duas mantém a velocidade atual). No celular aceitamos alguns ms de diferença em vez de corrigir toda hora.
                    double zona = _mpv.ZonaMortaS;
                    double v = Math.Abs(err) > zona ? Math.Clamp(1 - err / 2, 1 - lim, 1 + lim) : Math.Abs(err) < zona / 3 ? 1 : _vel;
                    if (Math.Abs(v - _vel) >= 0.001) { _vel = v; await _mpv.Velocidade(v); }
                }
            }
            await Task.Delay(250);
        }
    }

    /// <summary>Para o relatório do teste e do diagnóstico: erro contra o relógio comum depois de alinhar.</summary>
    public JsonObject Relatorio()
    {
        double[] e; lock (_erros) e = _erros.OrderBy(v => v).ToArray();
        double Q(double q) => e.Length == 0 ? -1 : Math.Round(e[Math.Min(e.Length - 1, (int)(e.Length * q))], 1);
        return new JsonObject
        {
            ["papel"] = Anfitriao ? "anfitriao" : "espectador", ["seq"] = _seq, ["tocando"] = Tocando, ["amostras"] = e.Length,
            ["erro_mediana_ms"] = Q(0.5), ["erro_p95_ms"] = Q(0.95), ["erro_max_ms"] = e.Length == 0 ? -1 : Math.Round(e[^1], 1), ["pulos"] = _pulos, ["espera_pulo_ms"] = UltimaEsperaPuloMs, ["comecou_com"] = ComecouCom,
        };
    }

    public static string Tempo(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _gen++;
        _mpv.Dispose();
    }
}
