using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.Content;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// O diagnóstico do celular, no mesmo formato do app Windows (spec 009, app/Streemu/Telemetria, relay/telemetria.js), para o servidor aceitar sem mudança:
/// cada abertura do app é uma sessão com uma pasta própria (log.txt, eventos.ndjson, perfil-&lt;hash&gt;.json, assistir.csv), e um laço sobe para
/// /telemetria/enviar o que cada arquivo cresceu desde o último byte que o servidor confirmou.
/// <para>
/// Quem é quem: o id do dispositivo (<see cref="Pc"/>) sai do ANDROID_ID, com um prefixo próprio no hash. Assim dois dispositivos da mesma conta
/// (o PC e o celular, ou dois celulares) têm diários separados no servidor; a conta só os liga à mesma pessoa, e o servidor a tira do token, nunca do pacote.
/// </para>
/// Vem ligado (<see cref="Ligado"/>); o aviso no Ao vivo (<see cref="Respondido"/>) explica e oferece desligar. Desligado é desligado de verdade:
/// nenhuma sessão é aberta, nada é gravado nem enviado, as threads ficam paradas e o que ainda não tinha subido é apagado do celular.
/// </summary>
static class Telemetria
{
    const int PedacoMax = 512 * 1024, PacoteMax = 1536 * 1024, EventosPorPacote = 200;
    const long PastaMaxBytes = 40L * 1024 * 1024;   // o que ainda não subiu, no máximo (sem conta por dias, por exemplo)
    public const string Canal = "android";

    static ISharedPreferences _prefs = null!;
    static string _raiz = "";
    static Sessao? _atual;
    static readonly object _gate = new();
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly AutoResetEvent _acordar = new(false);
    static Func<bool, Task<string?>>? _token;
    static Func<string?>? _servidor;
    static Func<string>? _apelido;
    static int _ritmoAtivo = 5, _ritmoParado = 60;
    static bool _iniciada, _threads;
    static volatile bool _perfilAFazer;
    static Context _app = null!;
    static bool _jaLogouOk;
    static DateTime _ultimoAvisoLog = DateTime.MinValue;

    /// <summary>O id deste dispositivo para a telemetria: 16 hex, como o do Windows (SHA-256 do MachineGuid).</summary>
    public static string Pc { get; private set; } = "";
    /// <summary>Assistindo uma live: sobe a cada poucos segundos (senão, a cada minuto).</summary>
    public static volatile bool Ativo;
    public static string? UltimoErro { get; private set; }
    public static DateTime? UltimoOk { get; private set; }

    /// <summary>A pessoa já viu o aviso do diagnóstico (só decide se o aviso aparece; o envio segue <see cref="Ligado"/>).</summary>
    public static bool Respondido
    {
        get => _prefs.GetBoolean("diagnostico_respondido", false);
        set => _prefs.Edit()!.PutBoolean("diagnostico_respondido", value)!.Apply();
    }

    /// <summary>Diagnóstico (aba Mais), ligado por padrão. Desligar para tudo e apaga o que ainda estava no celular; ligar abre uma sessão nova.</summary>
    public static bool Ligado
    {
        get => _prefs.GetBoolean("diagnostico_ligado", true);
        set
        {
            if (!_iniciada || value == Ligado) return;
            _prefs.Edit()!.PutBoolean("diagnostico_ligado", value)!.Apply();
            if (value) { Ligar(); LogApp.Info("[telemetria] diagnóstico ligado pela pessoa."); }
            else Desligar();
        }
    }

    public const string TextoDoAviso =
        "O Streemu manda diagnóstico para a equipe consertar bugs: o log do app, dados do celular (modelo, chip, Android, memória, tela, rede) e as " +
        "métricas das lives que você assiste, ligados à sua conta. Sem IP, sem senhas, sem nada das suas outras apps. Só a equipe lê, e os logs ficam " +
        "guardados 120 dias. Vem ligado, e você pode desligar a qualquer momento na aba Mais. Desligado, nada é gravado nem enviado, e o que ainda " +
        "não tinha subido é apagado do celular. Para apagar o que já foi enviado, peça à equipe.";

    public static void Iniciar(Context c, Func<bool, Task<string?>> token, Func<string?> servidor, Func<string> apelido)
    {
        if (_iniciada) return;
        _iniciada = true;
        _prefs = c.GetSharedPreferences("streemu", FileCreationMode.Private)!;
        _token = token; _servidor = servidor; _apelido = apelido;
        _app = c.ApplicationContext!;
        _raiz = System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "telemetria");
        LogApp.Line += l => _atual?.Linha(l);
        OuvirCrashes();
        if (Ligado) Ligar();
        else _ = Task.Run(ApagarPendentes);   // desligado numa abertura anterior: não pode sobrar nada
    }

    /// <summary>Abre a sessão desta abertura e liga as threads (só na primeira vez). Roda na thread principal.</summary>
    static void Ligar()
    {
        if (_atual != null) return;
        Directory.CreateDirectory(_raiz);
        string androidId = Android.Provider.Settings.Secure.GetString(_app.ContentResolver, Android.Provider.Settings.Secure.AndroidId) ?? "";
        if (androidId.Length < 8) androidId = _prefs.GetString("telemetria_id_reserva", null) ?? Guid.NewGuid().ToString("N");
        _prefs.Edit()!.PutString("telemetria_id_reserva", androidId)!.Apply();
        Pc = Hash("streemu-android:" + androidId.ToLowerInvariant());

        // sessões que ficaram abertas: o app foi fechado à força (ou o Android matou o processo)
        foreach (var dir in Directory.EnumerateDirectories(_raiz))
        {
            var s = Sessao.Abrir(dir);
            if (s is { Aberta: true }) { s.Aberta = false; s.Evento("sessao_sem_fim", null); s.Salvar(); Descarregar(new Dictionary<string, StringBuilder>()); }
        }
        _atual = Sessao.Nova(_raiz);
        var agora = DateTimeOffset.Now;
        LogApp.Info($"Log de {agora:yyyy-MM-dd}, fuso UTC{agora:zzz} (agora são {agora.UtcDateTime:HH:mm:ss} UTC). Dispositivo da telemetria {Pc}.");
        _atual.Evento("sessao_inicio", new JsonObject { ["versao"] = Program.Version, ["canal"] = Canal, ["plataforma"] = "android" });
        _perfilAFazer = true;
        _acordar.Set();
        if (_threads) return;
        _threads = true;
        // tudo o que pesa (o perfil lista os decodificadores; o envio comprime e sobe) roda numa thread de prioridade de fundo, fora da tela e da live
        new Thread(() => { Fundo(); Laco(); }) { IsBackground = true, Name = "telemetria" }.Start();
        new Thread(Gravador) { IsBackground = true, Name = "telemetria-disco" }.Start();
    }

    /// <summary>Para de gravar na hora e apaga, em fundo, o que ainda não tinha subido. Roda na thread principal.</summary>
    static void Desligar()
    {
        _atual = null;
        _perfilAFazer = false;
        _perfilPendente = null;
        _acordar.Set();
        _ = Task.Run(ApagarPendentes);
    }

    /// <summary>Espera um envio em curso terminar (_gate) e apaga as sessões guardadas. Se a pessoa religou nesse meio-tempo, nada é apagado.</summary>
    static void ApagarPendentes()
    {
        lock (_gate) lock (_disco)
        {
            if (Ligado) return;
            while (_fila.TryDequeue(out _)) { }
            try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, true); } catch { }
        }
    }

    public static void Evento(string tipo, JsonObject? campos = null) { _atual?.Evento(tipo, campos); if (tipo is "live" or "live_fim" or "midia_falha") _acordar.Set(); }

    // ================================================================================================ crash

    // Sem isto, um crash do app sumia sem uma linha: a sessão só aparecia como "não fechou normalmente", igual a o Android matar o processo por
    // memória ou a pessoa fechar à força (JC, 21/11 17:55, gravador de tela do sistema durante a transmissão). Agora o crash fica no log e vira o
    // evento "crash", gravados no disco na hora (a fila da thread de disco morreria junto com o processo); sobem na abertura seguinte.
    static int _crashRegistrado;

    static void OuvirCrashes()
    {
        // exceção .NET que atravessa para o Java (thread principal, callbacks do Android): chega aqui antes do Android derrubar o app.
        // Handled fica false: o app cai como antes, só que com registro.
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => RegistrarCrash("android", e.Exception);
        // exceção solta numa thread .NET (as nossas threads de rede, SRT, decodificador)
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RegistrarCrash("dotnet", e.ExceptionObject as Exception);
        // exceção do próprio Java (framework, bibliotecas): registra e devolve para quem tratava antes (o que mostra "o app parou")
        Java.Lang.Thread.DefaultUncaughtExceptionHandler = new CrashJava(Java.Lang.Thread.DefaultUncaughtExceptionHandler);
        // Task com exceção que ninguém leu: não derruba o app, mas costuma ser erro escondido
        TaskScheduler.UnobservedTaskException += (_, e) => LogApp.Info("AVISO: exceção de Task não observada: " + e.Exception.GetBaseException());
    }

    sealed class CrashJava(Java.Lang.Thread.IUncaughtExceptionHandler? anterior) : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            try { RegistrarCrash("java", null, e.Class?.Name ?? "?", e.Message ?? "", Android.Util.Log.GetStackTraceString(e) ?? "", t.Name ?? "?"); } catch { }
            anterior?.UncaughtException(t, e);
        }
    }

    static void RegistrarCrash(string origem, Exception? e) =>
        RegistrarCrash(origem, e, e?.GetType().FullName ?? "?", e?.Message ?? "", e?.ToString() ?? "", Thread.CurrentThread.Name ?? $"#{Environment.CurrentManagedThreadId}");

    /// <summary>Uma vez por processo (o mesmo crash passa por mais de um gancho: o primeiro, com a exceção .NET original, é o que vale).</summary>
    static void RegistrarCrash(string origem, Exception? _, string tipo, string mensagem, string pilha, string thread)
    {
        if (Interlocked.Exchange(ref _crashRegistrado, 1) != 0) return;
        try
        {
            LogApp.Info($"CRASH ({origem}, thread {thread}): {tipo}: {mensagem}\n{pilha}");
            static string Corta(string s, int max) { s = Limpar(s); return s.Length > max ? s[..max] + "…" : s; }
            _atual?.Evento("crash", new JsonObject
            {
                ["origem"] = origem, ["tipo_excecao"] = tipo, ["mensagem"] = Corta(mensagem, 500), ["pilha"] = Corta(pilha, 6000), ["thread"] = thread,
                ["transmitindo"] = Transmissao.ServicoTransmissao.AoVivo,
            });
            Descarregar(new Dictionary<string, StringBuilder>());   // agora, nesta thread: depois disto o processo pode morrer a qualquer instante
        }
        catch { }
    }

    /// <summary>Uma linha num CSV da sessão (com o cabeçalho na primeira vez).</summary>
    public static void Serie(string arquivo, string cabecalho, string linha) => _atual?.Csv(arquivo, cabecalho, linha);

    /// <summary>O app está saindo de verdade (a tela inicial fechada pelo voltar): fecha a sessão e tenta uma última rodada curta.</summary>
    public static void Fechar()
    {
        var s = _atual;
        if (s == null || !s.Aberta) return;
        s.Evento("sessao_fim", null);
        s.Aberta = false;
        s.Salvar();
        Descarregar(new Dictionary<string, StringBuilder>());
        _ = Task.Run(() => { try { Rodada(); } catch { } });
    }

    // ================================================================================================ perfil do celular

    static void GravarPerfil(Context c)
    {
        try
        {
            if (_atual is not { } atual) return;
            var perfil = new JsonObject { ["estavel"] = Aparelho.PerfilEstavel(c), ["momento"] = Aparelho.PerfilMomento(c) };
            string hash = Hash(perfil["estavel"]!.ToJsonString());
            File.WriteAllText(System.IO.Path.Combine(atual.Dir, $"perfil-{hash}.json"), Limpar(perfil.ToJsonString()));
            string? antes = _prefs.GetString("perfil_hash", null);
            if (antes != hash) atual.Evento("perfil", new JsonObject { ["hash"] = hash, ["antes"] = antes });
            // o índice do servidor recebe o perfil no cabeçalho do pacote só quando ele muda (como o Windows)
            if (_prefs.GetString("perfil_hash_aceito", null) != hash) _perfilPendente = (perfil, hash);
            _prefs.Edit()!.PutString("perfil_hash", hash)!.Apply();
        }
        catch (Exception e) { LogApp.Info("[telemetria] não montei o perfil do celular: " + e.Message); }
    }
    static (JsonObject Perfil, string Hash)? _perfilPendente;

    /// <summary>Prioridade de fundo do Android (a mesma de tarefa em segundo plano): o escalonador dá a vez à tela, ao decodificador e ao som.</summary>
    static void Fundo()
    {
        try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background); } catch { }
    }

    // ================================================================================================ disco: em fila, gravado em lote

    // Quem loga (a rede, o player) só põe a linha numa fila; uma thread de fundo junta e grava a cada meio segundo, um arquivo aberto por vez.
    // Abrir e fechar o arquivo a cada linha custava uma ida ao disco por linha; em celular fraco, com armazenamento lento, isso aparece.
    static readonly System.Collections.Concurrent.ConcurrentQueue<(string Dir, string Arquivo, string Texto)> _fila = new();
    static readonly AutoResetEvent _temFila = new(false);

    static void Enfileirar(string dir, string arquivo, string texto)
    {
        if (_fila.Count > 20_000) return;   // disco travado: melhor perder linha do que encher a memória
        _fila.Enqueue((dir, arquivo, texto));
    }

    static void Gravador()
    {
        Fundo();
        var lote = new Dictionary<string, StringBuilder>();
        while (true)
        {
            _temFila.WaitOne(500);
            Descarregar(lote);
        }
    }

    static readonly object _disco = new();

    /// <summary>Grava o que está na fila. Sob trava: a thread de disco e o envio (que descarrega antes de ler) não podem gravar linhas fora de ordem.</summary>
    static void Descarregar(Dictionary<string, StringBuilder> lote)
    {
        lock (_disco) DescarregarJa(lote);
    }

    static void DescarregarJa(Dictionary<string, StringBuilder> lote)
    {
        lote.Clear();
        while (_fila.TryDequeue(out var x))
        {
            string f = System.IO.Path.Combine(x.Dir, x.Arquivo);
            if (!lote.TryGetValue(f, out var sb)) lote[f] = sb = new StringBuilder();
            sb.Append(x.Texto);
        }
        foreach (var (f, sb) in lote)
            try { File.AppendAllText(f, sb.ToString()); } catch { }
    }

    // ================================================================================================ envio

    static void Laco()
    {
        int falhas = 0;
        while (true)
        {
            if (!Ligado) { _acordar.WaitOne(); continue; }   // desligado: parada até alguém ligar
            if (_perfilAFazer) { _perfilAFazer = false; GravarPerfil(_app); LogApp.Info("[telemetria] perfil do celular pronto; envio ligado."); }
            int espera;
            try
            {
                bool ok = Rodada();
                falhas = ok ? 0 : falhas + 1;
                // no log: o primeiro envio confirmado, e falha no máximo a cada 10 min (sem internet o dia inteiro não pode encher o log)
                if (ok && UltimoOk != null && !_jaLogouOk) { _jaLogouOk = true; LogApp.Info($"[telemetria] o servidor confirmou o envio (dispositivo {Pc})."); }
                if (!ok && DateTime.UtcNow - _ultimoAvisoLog > TimeSpan.FromMinutes(10))
                {
                    _ultimoAvisoLog = DateTime.UtcNow;
                    LogApp.Info($"[telemetria] envio falhou ({UltimoErro}); fica guardado no celular e tento de novo sozinho.");
                }
                espera = ok ? (Ativo ? Math.Max(_ritmoAtivo, 10) : _ritmoParado) : new[] { 5, 15, 60, 300 }[Math.Min(falhas, 4) - 1];
            }
            catch (Exception e)
            {
                UltimoErro = e.GetBaseException().Message; falhas++; espera = 60;
                if (DateTime.UtcNow - _ultimoAvisoLog > TimeSpan.FromMinutes(10)) { _ultimoAvisoLog = DateTime.UtcNow; LogApp.Info("[telemetria] erro no envio: " + e); }
            }
            _acordar.WaitOne(TimeSpan.FromSeconds(espera));
        }
    }

    /// <summary>Uma rodada: manda pacotes até não sobrar nada (ou dar erro). Verdadeiro se não houve erro.</summary>
    static bool Rodada()
    {
        lock (_gate)
        {
            if (!Ligado || _atual == null) return true;
            Descarregar(new Dictionary<string, StringBuilder>());
            Arrumar();
            if (_servidor?.Invoke() is not { Length: > 0 } servidor) return true;   // sem conta: espera
            for (int i = 0; i < 20; i++)
            {
                if (!Ligado) return true;
                var (pecas, eventos, mais) = Montar();
                if (pecas.Count == 0 && eventos.Count == 0 && _perfilPendente == null && i > 0) return true;
                if (!Mandar(servidor, pecas, eventos)) return false;
                if (!mais) return true;
            }
            return true;
        }
    }

    sealed record Peca(Sessao S, string Nome, long Inicio, long Fim, byte[] Gz, bool Fechado);

    static (List<Peca>, List<(Sessao S, long Ate, JsonObject Ev)>, bool Mais) Montar()
    {
        var pecas = new List<Peca>();
        var eventos = new List<(Sessao, long, JsonObject)>();
        long total = 0; bool mais = false;
        foreach (var s in Sessoes())
        {
            foreach (var (nome, caminho) in s.Arquivos())
            {
                if (total >= PacoteMax) { mais = true; break; }
                long cur = s.Cursor(nome), tam;
                try { tam = new FileInfo(caminho).Length; } catch { continue; }
                if (tam <= cur) continue;
                var (bytes, usados) = Ler(caminho, cur, tam, aceitaSemQuebra: !s.Aberta || nome.StartsWith("perfil-"));
                if (usados == 0) continue;
                if (cur + usados < tam) mais = true;
                byte[] gz = Gzip(Encoding.UTF8.GetBytes(Limpar(Encoding.UTF8.GetString(bytes))));
                pecas.Add(new Peca(s, nome, cur, cur + usados, gz, nome.StartsWith("perfil-") || (!s.Aberta && cur + usados == tam)));
                total += gz.Length;
            }
            if (eventos.Count < EventosPorPacote) LerEventos(s, eventos);
        }
        if (eventos.Count >= EventosPorPacote) mais = true;
        return (pecas, eventos, mais);
    }

    static bool Mandar(string servidor, List<Peca> pecas, List<(Sessao S, long Ate, JsonObject Ev)> eventos)
    {
        string? token = null;
        try { token = _token!(false).GetAwaiter().GetResult(); } catch { }
        if (token == null) { UltimoErro = "sem a sessão da conta"; return false; }
        var perfil = _perfilPendente;
        var cab = new JsonObject
        {
            ["v"] = 1, ["pc"] = Pc, ["hw"] = null, ["sessao"] = _atual!.Id, ["install"] = Install(), ["apelido"] = _apelido?.Invoke(),
            ["versao"] = Program.Version, ["commit"] = "", ["canal"] = Canal, ["plataforma"] = "android",
            ["relogio"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["ativo"] = Ativo,
            ["perfil"] = perfil == null ? null : JsonNode.Parse(Limpar(perfil.Value.Perfil.ToJsonString())),
            ["pecas"] = new JsonArray(pecas.Select(x => (JsonNode)new JsonObject
            {
                ["sessao"] = x.S.Id, ["arquivo"] = x.Nome, ["inicio"] = x.Inicio, ["fim"] = x.Fim, ["tam"] = x.Gz.Length,
                ["versao"] = Program.Version, ["commit"] = "", ["canal"] = Canal, ["fechado"] = x.Fechado,
            }).ToArray()),
            ["eventos"] = new JsonArray(eventos.Select(e => e.Ev.DeepClone()).ToArray()),
        };
        byte[] json = Encoding.UTF8.GetBytes(cab.ToJsonString());
        var corpo = new MemoryStream();
        corpo.Write(new[] { (byte)(json.Length >> 24), (byte)(json.Length >> 16), (byte)(json.Length >> 8), (byte)json.Length });
        corpo.Write(json);
        foreach (var x in pecas) corpo.Write(x.Gz);

        using var req = new HttpRequestMessage(HttpMethod.Post, servidor.TrimEnd('/') + "/telemetria/enviar") { Content = new ByteArrayContent(corpo.ToArray()) };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.Add("x-streemu-pc", Pc);
        HttpResponseMessage resp;
        // o Android não tem o Send síncrono (PlatformNotSupported): a versão assíncrona, esperada aqui mesmo (a thread é só do envio)
        try { resp = _http.SendAsync(req).GetAwaiter().GetResult(); }
        catch (Exception e) { UltimoErro = e.GetBaseException().Message; return false; }
        using (resp)
        {
            string texto = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 401) try { _ = _token!(true).GetAwaiter().GetResult(); } catch { }
                UltimoErro = $"o servidor respondeu {(int)resp.StatusCode}: {texto[..Math.Min(120, texto.Length)]}";
                return false;
            }
            var r = JsonNode.Parse(texto) as JsonObject ?? throw new InvalidOperationException("resposta vazia");
            if (r["ritmo"] is JsonObject ritmo)
            {
                _ritmoAtivo = Math.Clamp(ritmo["ativo_s"]?.GetValue<int>() ?? 5, 2, 120);
                _ritmoParado = Math.Clamp(ritmo["parado_s"]?.GetValue<int>() ?? 60, 10, 3600);
            }
            // o cursor só anda com o número que o servidor confirmou (se ele disser outro, o próximo envio sai de lá)
            if (r["confirmados"] is JsonObject conf)
                foreach (var x in pecas)
                    if (conf[$"{x.S.Id}|{x.Nome}"] is JsonValue v && v.TryGetValue<long>(out long novo) && novo >= 0 && novo <= x.Fim) x.S.SetCursor(x.Nome, novo);
            if (r["recusados"] is JsonArray { Count: > 0 } rec)
                LogApp.Info($"[telemetria] o servidor recusou: {string.Join(", ", rec.Select(n => n?.ToString()))}");
            foreach (var g in eventos.GroupBy(e => e.S)) g.Key.Indexados = g.Max(e => e.Ate);
            if (perfil != null && _perfilPendente?.Hash == perfil.Value.Hash) { _prefs.Edit()!.PutString("perfil_hash_aceito", perfil.Value.Hash)!.Apply(); _perfilPendente = null; }
            foreach (var s in pecas.Select(p => p.S).Concat(eventos.Select(e => e.S)).Distinct()) s.Salvar();
        }
        UltimoErro = null;
        UltimoOk = DateTime.Now;
        return true;
    }

    static (byte[] Bytes, int Usados) Ler(string caminho, long de, long ate, bool aceitaSemQuebra)
    {
        int n = (int)Math.Min(PedacoMax, ate - de);
        var buf = new byte[n];
        using (var f = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            f.Seek(de, SeekOrigin.Begin);
            int lidos = 0;
            while (lidos < n) { int r = f.Read(buf, lidos, n - lidos); if (r <= 0) break; lidos += r; }
            n = lidos;
        }
        int corte = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, n - 1)) + 1;
        if (corte == 0 && (aceitaSemQuebra || n == PedacoMax)) corte = n;
        if (corte < n && aceitaSemQuebra && de + n == ate) corte = n;
        return (buf[..corte], corte);
    }

    static void LerEventos(Sessao s, List<(Sessao, long, JsonObject)> saida)
    {
        string f = System.IO.Path.Combine(s.Dir, "eventos.ndjson");
        long de = s.Indexados, tam;
        try { tam = new FileInfo(f).Length; } catch { return; }
        if (tam <= de) return;
        var (bytes, _) = Ler(f, de, tam, aceitaSemQuebra: false);
        long pos = de;
        foreach (var linha in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (saida.Count >= EventosPorPacote) return;
            pos += Encoding.UTF8.GetByteCount(linha) + 1;
            if (pos > de + bytes.Length) return;
            if (string.IsNullOrWhiteSpace(linha)) continue;
            try { if (JsonNode.Parse(Limpar(linha)) is JsonObject o) saida.Add((s, pos, o)); } catch { }
        }
    }

    static IEnumerable<Sessao> Sessoes() =>
        Directory.EnumerateDirectories(_raiz).Select(Sessao.Abrir).OfType<Sessao>().OrderBy(s => s.Id).Select(s => s.Id == _atual?.Id ? _atual! : s);

    /// <summary>Apaga sessões fechadas que já subiram inteiras, e as mais velhas se a pasta passar do limite (sem conta por muito tempo).</summary>
    static void Arrumar()
    {
        var todas = Sessoes().ToList();
        foreach (var s in todas.Where(s => !s.Aberta && s.TudoEnviado())) Apagar(s);
        long total = 0;
        foreach (var s in todas.Where(s => Directory.Exists(s.Dir)).OrderByDescending(s => s.Id))
        {
            total += s.Tamanho();
            if (total > PastaMaxBytes && s != _atual) Apagar(s);
        }
    }

    static void Apagar(Sessao s) { try { Directory.Delete(s.Dir, true); } catch { } }

    static string Install()
    {
        var id = _prefs.GetString("telemetria_install", null);
        if (id == null) { id = Guid.NewGuid().ToString("N")[..16]; _prefs.Edit()!.PutString("telemetria_install", id)!.Apply(); }
        return id;
    }

    // ================================================================================================ limpeza do que sai (o mesmo que o Higiene do Windows)

    static readonly Regex Ipv4 = new(@"(?<![\d.])((?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d))\.(?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.]|\.x)");
    static readonly Regex Segredos = new(@"(passphrase|senha|password|credential|token)=([^&\s""]+)", RegexOptions.IgnoreCase);
    static readonly Regex TokensDaConta = new(@"\b(?:sa1|st1)\.[A-Za-z0-9_.-]+");
    static readonly Regex Bearer = new(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase);
    static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*");
    static readonly Regex Mac = new(@"(?<![0-9A-Fa-f:-])[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}(?![0-9A-Fa-f:-])");

    /// <summary>IP inteiro vira a.b.x.x, segredo vira ***, MAC vira &lt;MAC&gt;. O log já mascara os IPs do túnel; isto é a segunda barreira.</summary>
    public static string Limpar(string t)
    {
        t = Ipv4.Replace(t, "$1.x.x");
        t = Segredos.Replace(t, "$1=***");
        t = TokensDaConta.Replace(t, "***");
        t = Bearer.Replace(t, "Bearer ***");
        t = Jwt.Replace(t, "***");
        return Mac.Replace(t, "<MAC>");
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    static byte[] Gzip(byte[] dados)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(dados);
        return ms.ToArray();
    }

    // ================================================================================================ uma sessão (uma abertura do app)

    sealed class Sessao
    {
        public string Id = "", Dir = "";
        public bool Aberta;
        public long Indexados;
        readonly Dictionary<string, long> _cursores = new();
        readonly object _escrita = new();
        long _eid;

        public static Sessao Nova(string raiz)
        {
            string id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
            var s = new Sessao { Id = id, Dir = System.IO.Path.Combine(raiz, id), Aberta = true };
            Directory.CreateDirectory(s.Dir);
            s.Salvar();
            return s;
        }

        public static Sessao? Abrir(string dir)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(dir, "sessao.json"))) is not JsonObject o) return null;
                var s = new Sessao { Id = System.IO.Path.GetFileName(dir), Dir = dir, Aberta = o["aberta"]?.GetValue<bool>() ?? false, Indexados = o["indexados"]?.GetValue<long>() ?? 0 };
                if (o["cursores"] is JsonObject c) foreach (var (k, v) in c) s._cursores[k] = v?.GetValue<long>() ?? 0;
                return s;
            }
            catch { return null; }
        }

        public void Salvar()
        {
            lock (_escrita)
            {
                var c = new JsonObject();
                foreach (var (k, v) in _cursores) c[k] = v;
                var o = new JsonObject { ["aberta"] = Aberta, ["indexados"] = Indexados, ["cursores"] = c };
                string f = System.IO.Path.Combine(Dir, "sessao.json");
                try { File.WriteAllText(f + ".tmp", o.ToJsonString()); File.Move(f + ".tmp", f, true); } catch { }
            }
        }

        public long Cursor(string nome) { lock (_escrita) return _cursores.GetValueOrDefault(nome); }
        public void SetCursor(string nome, long v) { lock (_escrita) _cursores[nome] = v; }

        public IEnumerable<(string Nome, string Caminho)> Arquivos()
        {
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                if (n == "sessao.json" || n.EndsWith(".tmp")) continue;
                yield return (n, f);
            }
        }

        public bool TudoEnviado()
        {
            foreach (var (n, f) in Arquivos()) if (Cursor(n) < new FileInfo(f).Length) return false;
            long ev = 0;
            try { ev = new FileInfo(System.IO.Path.Combine(Dir, "eventos.ndjson")).Length; } catch { }
            return Indexados >= ev;
        }

        public long Tamanho() { try { return Directory.EnumerateFiles(Dir).Sum(f => new FileInfo(f).Length); } catch { return 0; } }

        void Anexar(string arquivo, string texto) => Enfileirar(Dir, arquivo, texto);

        public void Linha(string l) => Anexar("log.txt", l + "\n");

        public void Evento(string tipo, JsonObject? campos)
        {
            var agora = DateTimeOffset.UtcNow;
            var o = new JsonObject { ["tipo"] = tipo, ["hora"] = agora.ToUnixTimeMilliseconds(), ["hora_utc"] = agora.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["sessao"] = Id, ["eid"] = $"{Id}-{Interlocked.Increment(ref _eid)}" };
            if (campos != null) foreach (var (k, v) in campos) o[k] = v?.DeepClone();
            Anexar("eventos.ndjson", o.ToJsonString() + "\n");
        }

        readonly HashSet<string> _comCabecalho = new();
        public void Csv(string arquivo, string cabecalho, string linha)
        {
            bool primeira;
            lock (_escrita) primeira = _comCabecalho.Add(arquivo);
            Anexar(arquivo, primeira ? cabecalho + "\n" + linha + "\n" : linha + "\n");
        }
    }
}
