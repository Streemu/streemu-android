using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Anfitrião: serve o arquivo a um amigo pela ligação do filme (spec 010, 4.2). Responde ao filme_baixar com o filme_arquivo
/// (tamanho, nome e o hash do manifesto) e manda os pedaços pelo <see cref="Emissor"/>. O manifesto (SHA-256 de cada pedaço de 1 MB)
/// é calculado uma vez por arquivo e serve todos os amigos.
/// </summary>
public sealed class Envio : IDisposable
{
    sealed class Manifesto { public byte[]? Hashes; public double Feito; public string? Erro; }
    static readonly Dictionary<string, Manifesto> Manifestos = new();

    readonly Tunnel _t;
    readonly string _caminho, _filme, _nome;
    readonly Func<bool> _recuar;
    readonly bool _priorizarLigacaoDireta;
    Microsoft.Win32.SafeHandles.SafeFileHandle? _h;
    // Android: o arquivo do seletor do sistema não tem caminho, só um descritor; quem chama dá uma função que abre um Stream com seek (cada Envio abre o dele)
    readonly Func<Stream>? _abrir;
    readonly string _ext = "";
    Stream? _s;
    readonly object _gateS = new();
    long _tam;
    byte[]? _hashes;
    Emissor? _emissor;

    public Tunnel Tunel => _t;
    public Emissor? Emissor => _emissor;

    public Envio(Tunnel t, string caminho, string filme, string nome, Func<bool> recuar, bool priorizarLigacaoDireta = false)
    {
        _t = t; _caminho = caminho; _filme = filme; _nome = nome; _recuar = recuar;
        _priorizarLigacaoDireta = priorizarLigacaoDireta;
    }

    /// <summary>Envio de um arquivo que só se abre por <paramref name="abrir"/> (no Android, o ContentResolver). <paramref name="ext"/> vai ao amigo com o ponto (".mkv").</summary>
    public Envio(Tunnel t, Func<Stream> abrir, string ext, string filme, string nome, Func<bool> recuar, bool priorizarLigacaoDireta = false)
        : this(t, "", filme, nome, recuar, priorizarLigacaoDireta)
    {
        _abrir = abrir; _ext = ext;
    }

    static Manifesto ManifestoDe(string caminho, string filme, Func<Stream>? abrir = null)
    {
        lock (Manifestos)
        {
            if (Manifestos.TryGetValue(filme, out var m) && m.Erro == null) return m;
            m = Manifestos[filme] = new Manifesto();
            _ = Task.Run(() =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var h = abrir == null ? File.OpenHandle(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
                    using var st = abrir?.Invoke();
                    long n = st != null ? st.Length : RandomAccess.GetLength(h!);
                    int np = (int)((n + Pacote.Pedaco - 1) / Pacote.Pedaco);
                    var hashes = new byte[np * 32];
                    var buf = new byte[Pacote.Pedaco];
                    for (int i = 0; i < np; i++)
                    {
                        int tamPeca = (int)Math.Min(Pacote.Pedaco, n - (long)i * Pacote.Pedaco);
                        int lido = st != null ? LerTudo(st, buf, (long)i * Pacote.Pedaco, tamPeca) : LerTudo(h!, buf, (long)i * Pacote.Pedaco, tamPeca);
                        SHA256.HashData(buf.AsSpan(0, lido), hashes.AsSpan(i * 32, 32));
                        m.Feito = (i + 1) / (double)np;
                    }
                    m.Hashes = hashes;
                    Log.Info($"[filme] manifesto pronto: {np} pedaços de 1 MB em {sw.Elapsed.TotalSeconds:N1} s.");
                }
                // a mensagem do Windows traz o caminho do arquivo: nem ao log (telemetria) nem ao amigo (spec 020, critério 15)
                catch (Exception e) { m.Erro = $"o arquivo não pôde ser lido ({e.GetType().Name})"; Log.Info($"[filme] não consegui preparar o arquivo para mandar ({e.GetType().Name})."); }
            });
            return m;
        }
    }

    internal static int LerTudo(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, long pos, int tam)
    {
        int lido = 0, r;
        while (lido < tam && (r = RandomAccess.Read(h, buf.AsSpan(lido, tam - lido), pos + lido)) > 0) lido += r;
        return lido;
    }

    internal static int LerTudo(Stream s, byte[] buf, long pos, int tam)
    {
        s.Position = pos;
        int lido = 0, r;
        while (lido < tam && (r = s.Read(buf, lido, tam - lido)) > 0) lido += r;
        return lido;
    }

    /// <summary>Resposta ao filme_baixar (que se repete até chegar a resposta com o manifesto).</summary>
    public void Responder()
    {
        var m = ManifestoDe(_caminho, _filme, _abrir);
        var r = new JsonObject { ["type"] = "filme_arquivo", ["filme"] = _filme, ["nome"] = _nome };
        if (m.Erro != null) r["erro"] = m.Erro;
        else if (m.Hashes == null) r["preparando"] = Math.Round(m.Feito * 100);
        else
        {
            try
            {
                if (_emissor == null)
                {
                    if (_abrir != null) { _s = _abrir(); _tam = _s.Length; }
                    else { _h = File.OpenHandle(_caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); _tam = RandomAccess.GetLength(_h); }
                    _hashes = m.Hashes;
                    _emissor = new Emissor(_t, Tamanho, Ler, _recuar, _priorizarLigacaoDireta);
                    Log.Info($"[filme] mandando o arquivo ({_tam / 1e6:N0} MB).");
                }
                r["tamanho"] = _tam; r["ext"] = _abrir != null ? _ext : Path.GetExtension(_caminho); r["pedaco"] = Pacote.Pedaco;
                r["manifesto_sha"] = Convert.ToHexString(SHA256.HashData(m.Hashes)); r["manifesto_tamanho"] = m.Hashes.Length;
            }
            catch (Exception e) { r["erro"] = $"o arquivo não pôde ser aberto ({e.GetType().Name})"; } // sem e.Message: traz o caminho
        }
        _t.SendControl(r);
    }

    int Tamanho(uint p)
    {
        if ((p & Pacote.Manifesto) != 0) return (int)Math.Clamp(_hashes!.Length - (long)(p & ~Pacote.Manifesto) * Pacote.Pedaco, 0, Pacote.Pedaco);
        return (int)Math.Clamp(_tam - (long)p * Pacote.Pedaco, 0, Pacote.Pedaco);
    }

    void Ler(uint p, byte[] buf, int tam)
    {
        if ((p & Pacote.Manifesto) != 0) Buffer.BlockCopy(_hashes!, (int)(p & ~Pacote.Manifesto) * Pacote.Pedaco, buf, 0, tam);
        else if (_s != null) { lock (_gateS) LerTudo(_s, buf, (long)p * Pacote.Pedaco, tam); }   // o Stream tem posição só dele: uma leitura por vez
        else LerTudo(_h!, buf, (long)p * Pacote.Pedaco, tam);
    }

    public void Dispose()
    {
        if (_emissor is { } e)
            Log.Info($"[filme] envio do arquivo encerrado: {e.BytesEnviados / 1e6:N0} MB mandados, {e.Reenvios} pacotes reenviados, taxa final {e.Taxa / 1e6:N1} MB/s.");
        _emissor?.Dispose();
        _h?.Dispose();
        _s?.Dispose();
    }
}

/// <summary>
/// Como o download fala com a pessoa e no log, para servir o Cinema e o envio de arquivo (spec 020) com o mesmo código.
/// <paramref name="Quem"/> e <paramref name="OQue"/> vão no meio da frase, em minúscula ("o anfitrião", "o filme").
/// Ex.: <c>new TextosDownload("a Ana", "o arquivo", "arquivo", "[arquivo]")</c>.
/// </summary>
public sealed record TextosDownload(string Quem, string OQue, string NomePadrao, string Tag)
{
    public static readonly TextosDownload Cinema = new("o anfitrião", "o filme", "filme", "[filme]");

    public string QuemNoComeco => char.ToUpperInvariant(Quem[0]) + Quem[1..];
}

/// <summary>
/// Quem baixa: pede o arquivo ao anfitrião e grava em &lt;pasta&gt;\&lt;filme&gt;.streemuparcial, com o estado ao lado (.streemuparcial.json:
/// manifesto e pedaços feitos). Fechou o app no meio: na próxima vez continua de onde parou. Cada pedaço é conferido pelo hash antes
/// de gravar (errado é pedido de novo) e, no fim, o arquivo inteiro é conferido de novo antes de virar o filme.
/// </summary>
public sealed class Download
{
    const long Folga = 200L << 20;
    static readonly bool Teste = Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") != null || Environment.GetEnvironmentVariable("STREEMU_NOSTATE") == "1";
    static string ArquivoPasta => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "filme-pasta.txt");

    readonly Tunnel _t;
    readonly string _filme, _pasta;
    readonly TextosDownload _x;
    readonly TaskCompletionSource<JsonObject> _info = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile JsonObject? _ultima;
    bool _estragar = Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ESTRAGAR") == "1";
    long _retomado, _baixado;
    int _refeitos;
    double _segundos;

    public Progresso Atual { get; private set; } = new("conectando", 0, 0, 0);

    public Download(Tunnel t, string filme, string pasta, TextosDownload? textos = null)
    {
        _t = t; _filme = filme; _pasta = pasta; _x = textos ?? TextosDownload.Cinema;
    }

    /// <summary>Tamanho que está na identidade do filme ("tamanho-hash").</summary>
    public static long TamanhoDe(string filme) => Identidade.Tamanho(filme);

    /// <summary>A identidade vem do outro PC: fora do formato, nunca vira caminho (spec 020, T1).</summary>
    static string Parcial(string pasta, string filme)
    {
        if (!Identidade.Valida(filme))
            throw new IOException($"Identidade de arquivo inválida: \"{filme}\" (esperado \"tamanho-32 dígitos hexadecimais\"). Nada foi gravado.");
        return Path.Combine(pasta, filme + ".streemuparcial");
    }

    /// <summary>Bytes já baixados de um download parado nessa pasta (nulo = não há).</summary>
    public static long? JaBaixado(string pasta, string filme)
    {
        try
        {
            string est = Parcial(pasta, filme) + ".json";
            if (!File.Exists(est) || JsonNode.Parse(File.ReadAllText(est)) is not JsonObject j) return null;
            return j["feito"]?.GetValue<long>() ?? 0;
        }
        catch { return null; }
    }

    /// <summary>A última pasta escolhida (fica de padrão); em teste não grava nada.</summary>
    public static string PastaPadrao
    {
        get
        {
            try { if (!Teste && File.Exists(ArquivoPasta) && File.ReadAllText(ArquivoPasta).Trim() is { Length: > 0 } p && Directory.Exists(p)) return p; } catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
        set
        {
            if (Teste) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(ArquivoPasta)!); File.WriteAllText(ArquivoPasta, value); } catch { }
        }
    }

    /// <summary>Quem mede o espaço livre da pasta quando o DriveInfo não serve (o app Android liga; nulo = usa o DriveInfo).</summary>
    public static Func<string, long?>? EspacoLivre { get; set; }

    /// <summary>Por que não cabe (nulo = cabe). Sem como medir (pasta de rede), deixa seguir.</summary>
    public static string? FaltaEspaco(string pasta, long precisa, string oQue = "o filme")
    {
        try
        {
            // Android: o app mede com StatFs (DriveInfo não serve lá)
            if (EspacoLivre?.Invoke(pasta) is long livre)
                return livre >= precisa + Folga ? null : $"Não cabe no celular: {oQue} precisa de {Gb(precisa)} e há {Gb(livre)} livres. Libere espaço e tente de novo.";
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pasta))!);
            if (d.AvailableFreeSpace >= precisa + Folga) return null;
            return $"Não cabe em {d.Name.TrimEnd('\\')}: {oQue} precisa de {Gb(precisa)} e há {Gb(d.AvailableFreeSpace)} livres. Libere espaço ou escolha outra pasta.";
        }
        catch { return null; }
    }

    static string Gb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):N1} GB" : $"{b / (double)(1 << 20):N0} MB";

    void OnControle(JsonObject m)
    {
        if (m["type"]?.GetValue<string>() != "filme_arquivo" || m["filme"]?.GetValue<string>() != _filme) return;
        _ultima = m;
        if (m["manifesto_sha"] != null) _info.TrySetResult(m);
    }

    /// <summary>Baixa e confere; devolve o caminho do filme pronto. Cancelado ou com erro, o que já veio fica para a próxima vez.</summary>
    public async Task<string> RodarAsync(Action<Progresso> progresso, CancellationToken ct)
    {
        _t.Control += OnControle;
        try { return await RodarInternoAsync(progresso, ct); }
        finally { _t.Control -= OnControle; }
    }

    /// <summary>1. o pedido: o anfitrião responde com o manifesto (ou com quanto falta para ele ficar pronto).</summary>
    async Task<JsonObject> PedirInfoAsync(Action<Progresso> mostrar, CancellationToken ct)
    {
        var espera = Stopwatch.StartNew();
        while (true)
        {
            if (_t.State != TunnelState.Connected) throw new IOException($"A ligação com {_x.Quem} caiu.");
            _t.SendControl(new JsonObject { ["type"] = "filme_baixar", ["filme"] = _filme });
            await Task.WhenAny(_info.Task, Task.Delay(1500, ct));
            ct.ThrowIfCancellationRequested();
            if (_info.Task.IsCompleted) return _info.Task.Result;
            var u = _ultima;
            if (u?["erro"] is JsonNode e) throw new IOException($"{_x.QuemNoComeco} não conseguiu ler {_x.OQue} ({e}).");
            if (u?["preparando"] is JsonNode pr) mostrar(new("preparando", (long)pr.GetValue<double>(), 100, 0));
            else if (espera.Elapsed.TotalSeconds > 20) throw new IOException($"{_x.QuemNoComeco} não respondeu ao pedido do arquivo. Precisa da versão nova do app.");
        }
    }

    /// <summary>
    /// 4.1: quem já tem o arquivo fica "pronto" na hora, mas o app confere o arquivo inteiro contra o manifesto do anfitrião em
    /// segundo plano, para pegar um arquivo corrompido antes do filme começar (a identidade só lê 12 MB). Devolve quantos pedaços
    /// não batem (0 = arquivo bom).
    /// </summary>
    public async Task<int> ConferirAsync(string caminho, Action<Progresso> progresso, CancellationToken ct)
    {
        void Mostrar(Progresso p) { Atual = p; progresso(p); }
        _t.Control += OnControle;
        try
        {
            var info = await PedirInfoAsync(Mostrar, ct);
            long tam = info["tamanho"]!.GetValue<long>();
            int mtam = info["manifesto_tamanho"]!.GetValue<int>();
            int np = (int)((tam + Pacote.Pedaco - 1) / Pacote.Pedaco);
            if (mtam != np * 32) throw new IOException($"{_x.QuemNoComeco} mandou uma lista de pedaços inválida.");
            Mostrar(new("manifesto", 0, tam, 0));
            var hashes = new byte[mtam];
            using (var rec = new Receptor(_t, id => (int)Math.Clamp(mtam - (long)(id & ~Pacote.Manifesto) * Pacote.Pedaco, 0, Pacote.Pedaco)))
            {
                int nm = (mtam + Pacote.Pedaco - 1) / Pacote.Pedaco;
                rec.Pedir(Enumerable.Range(0, nm).Select(i => Pacote.Manifesto | (uint)i));
                var ultimo = Stopwatch.StartNew();
                for (int k = 0; k < nm;)
                {
                    if (rec.Prontos.TryRead(out var it))
                    {
                        Buffer.BlockCopy(it.Buf, 0, hashes, (int)(it.Peca & ~Pacote.Manifesto) * Pacote.Pedaco, it.Tam);
                        rec.Liberar(it.Buf);
                        k++;
                        continue;
                    }
                    if (_t.State != TunnelState.Connected || ultimo.Elapsed.TotalSeconds > 30) throw new IOException("A lista de pedaços não chegou.");
                    await Task.WhenAny(rec.Prontos.WaitToReadAsync(ct).AsTask(), Task.Delay(500, ct));
                    ct.ThrowIfCancellationRequested();
                }
            }
            if (Convert.ToHexString(SHA256.HashData(hashes)) != info["manifesto_sha"]!.GetValue<string>()) throw new IOException("A lista de pedaços chegou diferente.");
            int ruins = 0;
            var buf = new byte[Pacote.Pedaco];
            using var h = File.OpenHandle(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            for (int i = 0; i < np; i++)
            {
                ct.ThrowIfCancellationRequested();
                int n = Envio.LerTudo(h, buf, (long)i * Pacote.Pedaco, TamPeca(tam, i));
                if (!SHA256.HashData(buf.AsSpan(0, n)).AsSpan().SequenceEqual(hashes.AsSpan(i * 32, 32))) ruins++;
                if (i % 16 == 0) Mostrar(new("conferindo", (long)i * Pacote.Pedaco, tam, 0));
            }
            Mostrar(new(ruins == 0 ? "pronto" : "defeito", tam, tam, 0));
            Log.Info(ruins == 0 ? $"{_x.Tag} arquivo conferido: igual ao que {_x.Quem} tem." : $"{_x.Tag} arquivo conferido: {ruins} pedaço(s) diferentes do que {_x.Quem} tem.");
            return ruins;
        }
        finally { _t.Control -= OnControle; }
    }

    async Task<string> RodarInternoAsync(Action<Progresso> progresso, CancellationToken ct)
    {
        void Mostrar(Progresso p) { Atual = p; progresso(p); }

        var info = await PedirInfoAsync(Mostrar, ct);

        long tam = info["tamanho"]!.GetValue<long>();
        int mtam = info["manifesto_tamanho"]!.GetValue<int>();
        string msha = info["manifesto_sha"]!.GetValue<string>();
        string nome = info["nome"]?.GetValue<string>() ?? _x.NomePadrao, ext = info["ext"]?.GetValue<string>() ?? "";
        int np = (int)((tam + Pacote.Pedaco - 1) / Pacote.Pedaco);
        if (tam != TamanhoDe(_filme) || mtam != np * 32) throw new IOException($"{_x.QuemNoComeco} mandou um arquivo de outro tamanho. Trocou {_x.OQue}?");

        // 2. retomada: o estado ao lado do parcial, se for do mesmo manifesto
        string parcial = Parcial(_pasta, _filme), est = parcial + ".json";
        byte[]? hashes = null;
        var feitos = new byte[(np + 7) / 8];
        try
        {
            if (File.Exists(est) && File.Exists(parcial) && JsonNode.Parse(File.ReadAllText(est)) is JsonObject j && j["manifesto_sha"]?.GetValue<string>() == msha)
            {
                hashes = Convert.FromBase64String(j["manifesto"]!.GetValue<string>());
                var f = Convert.FromBase64String(j["feitos"]!.GetValue<string>());
                if (hashes.Length == mtam && f.Length == feitos.Length) f.CopyTo(feitos, 0); else hashes = null;
                _refeitos = j["refeitos"]?.GetValue<int>() ?? 0;
            }
        }
        catch (Exception e) { Log.Info($"{_x.Tag} estado do download ilegível, começo do zero: {e.Message}"); hashes = null; }
        long Feito() { long s = 0; for (int i = 0; i < np; i++) if (Tem(feitos, i)) s += TamPeca(tam, i); return s; }
        _retomado = hashes == null ? 0 : Feito();
        if (hashes == null) Array.Clear(feitos);
        if (FaltaEspaco(_pasta, tam - _retomado, _x.OQue) is string semEspaco) throw new IOException(semEspaco);
        if (_retomado > 0) Log.Info($"{_x.Tag} continuando o download de onde parou: {_retomado / 1e6:N0} de {tam / 1e6:N0} MB.");

        using var fs = new FileStream(parcial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.RandomAccess);
        if (fs.Length != tam) fs.SetLength(tam);
        void Salvar()
        {
            if (hashes == null) return;
            fs.Flush(true); // os dados no disco antes de marcar os pedaços como feitos
            var j = new JsonObject
            {
                ["filme"] = _filme, ["nome"] = nome, ["ext"] = ext, ["tamanho"] = tam, ["feito"] = Feito(),
                ["manifesto_sha"] = msha, ["manifesto"] = Convert.ToBase64String(hashes), ["feitos"] = Convert.ToBase64String(feitos),
                ["refeitos"] = _refeitos,
            };
            File.WriteAllText(est + ".tmp", j.ToJsonString());
            File.Move(est + ".tmp", est, true);
        }

        using var rec = new Receptor(_t, id => (id & Pacote.Manifesto) != 0
            ? (int)Math.Clamp(mtam - (long)(id & ~Pacote.Manifesto) * Pacote.Pedaco, 0, Pacote.Pedaco)
            : TamPeca(tam, (int)id));

        async Task<(uint Peca, byte[] Buf, int Tam)> Proximo()
        {
            var ultimo = Stopwatch.StartNew();
            while (true)
            {
                if (rec.Prontos.TryRead(out var item)) return item;
                ct.ThrowIfCancellationRequested();
                if (_t.State != TunnelState.Connected) throw new IOException($"A ligação com {_x.Quem} caiu. O que já veio fica guardado.");
                if (ultimo.Elapsed.TotalSeconds > 30) throw new IOException($"{_x.QuemNoComeco} parou de mandar o arquivo há 30 s. O que já veio fica guardado.");
                await Task.WhenAny(rec.Prontos.WaitToReadAsync(ct).AsTask(), Task.Delay(500, ct));
            }
        }

        try
        {
            // 3. o manifesto (pela mesma via confiável: num filme de 60 GB ele tem 2 MB, grande demais para uma mensagem)
            if (hashes == null)
            {
                Mostrar(new("manifesto", 0, tam, 0));
                var hs = new byte[mtam];
                int nm = (mtam + Pacote.Pedaco - 1) / Pacote.Pedaco;
                rec.Pedir(Enumerable.Range(0, nm).Select(i => Pacote.Manifesto | (uint)i));
                for (int k = 0; k < nm; k++)
                {
                    var (id, buf, n) = await Proximo();
                    Buffer.BlockCopy(buf, 0, hs, (int)(id & ~Pacote.Manifesto) * Pacote.Pedaco, n);
                    rec.Liberar(buf);
                }
                if (Convert.ToHexString(SHA256.HashData(hs)) != msha) throw new IOException("A lista de pedaços chegou diferente. Tente de novo.");
                hashes = hs;
                Salvar();
            }

            // 4. os pedaços; no fim confere tudo e pede de novo o que não bate (até 3 voltas)
            var sw = Stopwatch.StartNew();
            var amostras = new Queue<(double S, long B)>();
            long feito = _retomado, salvoEm = 0;
            int semSalvar = 0;
            double logEm = 0;
            if (Simulador.Ligado) Log.Info($"{_x.Tag} teste com rede ruim: perda {Simulador.Perda * 100:N1}%, atraso {Simulador.AtrasoMs} ms em cada sentido.");
            for (int volta = 0; ; volta++)
            {
                var faltam = Enumerable.Range(0, np).Where(i => !Tem(feitos, i)).ToList();
                rec.Pedir(faltam.Select(i => (uint)i));
                for (int restam = faltam.Count; restam > 0;)
                {
                    var (id, buf, n) = await Proximo();
                    int i = (int)id;
                    if (_estragar) { buf[0] ^= 0xFF; _estragar = false; } // teste: um pedaço chega errado
                    if (!SHA256.HashData(buf.AsSpan(0, n)).AsSpan().SequenceEqual(hashes.AsSpan(i * 32, 32)))
                    {
                        _refeitos++;
                        Log.Info($"{_x.Tag} pedaço {i} chegou diferente do manifesto; pedi de novo.");
                        rec.Liberar(buf);
                        rec.Pedir(new[] { id });
                        continue;
                    }
                    RandomAccess.Write(fs.SafeFileHandle, buf.AsSpan(0, n), (long)i * Pacote.Pedaco);
                    rec.Liberar(buf);
                    feitos[i >> 3] |= (byte)(1 << (i & 7));
                    restam--; feito += n; _baixado += n;
                    double s = sw.Elapsed.TotalSeconds;
                    amostras.Enqueue((s, feito));
                    while (amostras.Count > 2 && s - amostras.Peek().S > 5) amostras.Dequeue();
                    var a0 = amostras.Peek();
                    double taxa = s - a0.S > 0.2 ? (feito - a0.B) / (s - a0.S) : 0;
                    Mostrar(new("baixando", feito, tam, taxa));
                    if (s - logEm >= 5)
                    {
                        logEm = s;
                        Log.Info($"{_x.Tag} download: {Atual.Pct}%, {taxa / 1e6:N1} MB/s, {rec.BytesRecebidos / 1e6:N0} MB recebidos nesta ligação, {_refeitos} pedaço(s) refeitos.");
                    }
                    // a cada 2 s ou 32 MB: fechar o app perde no máximo isso (numa rede rápida, 32 MB passam em menos de 2 s)
                    if (sw.ElapsedMilliseconds - salvoEm > 2000 || ++semSalvar >= 32) { Salvar(); salvoEm = sw.ElapsedMilliseconds; semSalvar = 0; }
                }
                _segundos += sw.Elapsed.TotalSeconds; sw.Restart();
                Salvar();

                // conferência do arquivo inteiro: pega pedaço estragado no disco ou de uma retomada no meio de uma gravação
                var buf2 = new byte[Pacote.Pedaco];
                var ruins = new List<int>();
                for (int i = 0; i < np; i++)
                {
                    int n = Envio.LerTudo(fs.SafeFileHandle, buf2, (long)i * Pacote.Pedaco, TamPeca(tam, i));
                    if (!SHA256.HashData(buf2.AsSpan(0, n)).AsSpan().SequenceEqual(hashes.AsSpan(i * 32, 32))) ruins.Add(i);
                    if (i % 16 == 0) Mostrar(new("conferindo", (long)i * Pacote.Pedaco, tam, 0));
                }
                if (ruins.Count == 0) break;
                if (volta >= 2) throw new IOException($"{ruins.Count} pedaço(s) continuam diferentes depois de baixar de novo. Confira o disco.");
                Log.Info($"{_x.Tag} conferência: {ruins.Count} pedaço(s) diferentes; baixando de novo.");
                foreach (int i in ruins) { feitos[i >> 3] &= (byte)~(1 << (i & 7)); feito -= TamPeca(tam, i); }
                Salvar();
            }
        }
        catch
        {
            try { Salvar(); } catch { }
            throw;
        }

        fs.Dispose();
        if (Identidade.De(parcial) != _filme) throw new IOException($"O arquivo baixado não bate com o que {_x.Quem} tem. Trocou {_x.OQue}?");
        string final = Arquivos.NomeSeguro.Destino(_pasta, nome, ext, _x.NomePadrao);
        File.Move(parcial, final);
        File.Delete(est);
        Mostrar(new("pronto", tam, tam, 0));
        // sem o nome nem o caminho: o log sobe com a telemetria (spec 020, critério 15)
        Log.Info($"{_x.Tag} download pronto e conferido: {tam / 1e6:N0} MB ({Relatorio().ToJsonString()})");
        return final;
    }

    static bool Tem(byte[] b, int i) => (b[i >> 3] & (1 << (i & 7))) != 0;
    static int TamPeca(long tam, int i) => (int)Math.Clamp(tam - (long)i * Pacote.Pedaco, 0, Pacote.Pedaco);

    public JsonObject Relatorio() => new()
    {
        ["retomado_de_mb"] = Math.Round(_retomado / 1e6, 1), ["baixado_mb"] = Math.Round(_baixado / 1e6, 1), ["segundos"] = Math.Round(_segundos, 2),
        ["vazao_mb_s"] = _segundos > 0 ? Math.Round(_baixado / 1e6 / _segundos, 1) : 0, ["pedacos_refeitos"] = _refeitos,
    };
}
