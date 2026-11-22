namespace Streemu.Proto.Filme;

/// <summary>
/// O cache de uma época do filme pela transmissão (spec 028, 5.1): o que a thread do SRT escreve e o DataSource do ExoPlayer lê. É o MPEG-TS do anfitrião, byte a byte.
/// O SRT em modo live descarta o que não é lido a tempo e o anfitrião manda até 30 s à frente: por isso uma thread lê sem parar e o player lê daqui, no ritmo dele.
/// <para>
/// Guarda em RAM (blocos de 1 MiB) até <see cref="LimiteRam"/> (~64 MB); passou disso, tudo vai para um arquivo no cache do app, que é um anel de
/// <see cref="CapArquivo"/> bytes (o que ficou mais para trás que isso se perde, como o back-buffer do mpv). Os bytes têm posição absoluta desde o começo da época.
/// </para>
/// <para>
/// Enquanto escreve, olha os pacotes TS (só o cabeçalho): guarda a última PAT e PMT, o carimbo (PTS) de cada quadro-chave do vídeo com a posição dele no cache
/// (o índice que deixa o player começar de um ponto do filme sem pedir época nova) e até onde o vídeo chegou. Os carimbos saem do anfitrião no tempo do filme
/// (<c>-output_ts_offset</c>), então o PTS é a posição do filme em segundos.
/// </para>
/// </summary>
public sealed class CacheTs : IDisposable
{
    public const int Ts = 188;
    const int Bloco = 1 << 20;

    public long LimiteRam { get; }
    public long CapArquivo { get; }

    readonly object _l = new();
    List<byte[]>? _ram = new();
    FileStream? _arq;
    string? _caminho;
    readonly string _pasta;
    long _cabeca;      // bytes escritos (posição absoluta do próximo)
    bool _fim, _fechado;
    string? _erro;

    // varredura dos pacotes TS
    readonly byte[] _pend = new byte[Ts];
    int _pendN;
    long _posPacote;          // posição absoluta do pacote que está em _pend (ou do próximo a começar)
    int _pmtPid = -1;
    byte[]? _pat, _pmt;
    readonly List<(double Pts, long Pos)> _chaves = new();
    double _ptsMax = double.NaN, _primeiroPts = double.NaN;
    long _pacotes, _foraDeSincronia;

    public CacheTs(string pastaTemporaria, long limiteRam = 64L << 20, long capArquivo = 512L << 20)
    {
        _pasta = pastaTemporaria; LimiteRam = limiteRam; CapArquivo = capArquivo;
    }

    // ------------------------------------------------------------------ escrever

    /// <summary>Acrescenta o que veio do SRT (qualquer tamanho; os pacotes TS são achados dentro).</summary>
    public void Escrever(byte[] dados, int n)
    {
        lock (_l)
        {
            if (_fim || _fechado) return;
            Guardar(dados, n);
            Varrer(dados, n);
            _cabeca += n;
            Monitor.PulseAll(_l);
        }
    }

    /// <summary>Acabou (o SRT fechou ou deu erro): quem lê recebe o fim quando chegar ao que tem.</summary>
    public void Terminar(string? erro = null)
    {
        lock (_l) { if (_fim) return; _fim = true; _erro = erro; Monitor.PulseAll(_l); }
    }

    public bool Terminou { get { lock (_l) return _fim; } }
    public string? Erro { get { lock (_l) return _erro; } }
    public long Bytes { get { lock (_l) return _cabeca; } }
    public bool EmArquivo { get { lock (_l) return _arq != null; } }
    public long PacotesTs { get { lock (_l) return _pacotes; } }
    public long ForaDeSincronia { get { lock (_l) return _foraDeSincronia; } }

    void Guardar(byte[] dados, int n)
    {
        if (_ram != null && _cabeca + n > LimiteRam) PassarParaArquivo();
        if (_ram != null)
        {
            int ini = 0;
            while (ini < n)
            {
                int bi = (int)((_cabeca + ini) / Bloco), off = (int)((_cabeca + ini) % Bloco);
                while (_ram.Count <= bi) _ram.Add(new byte[Bloco]);
                int c = Math.Min(n - ini, Bloco - off);
                Buffer.BlockCopy(dados, ini, _ram[bi], off, c);
                ini += c;
            }
            return;
        }
        EscreverAnel(_cabeca, dados, n);
    }

    void PassarParaArquivo()
    {
        Directory.CreateDirectory(_pasta);
        _caminho = Path.Combine(_pasta, "filme-" + Guid.NewGuid().ToString("N") + ".ts");
        _arq = new FileStream(_caminho, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        long resta = _cabeca;
        for (int bi = 0; resta > 0; bi++)
        {
            int c = (int)Math.Min(resta, Bloco);
            // os primeiros bytes só cabem se o anel for maior que a RAM (é, por construção): sem a cópia do que já passou do anel
            EscreverAnel(bi * (long)Bloco, _ram![bi], c);
            resta -= c;
        }
        _ram = null;
    }

    void EscreverAnel(long pos, byte[] dados, int n)
    {
        int ini = 0;
        while (ini < n)
        {
            long dentro = (pos + ini) % CapArquivo;
            int c = (int)Math.Min(n - ini, CapArquivo - dentro);
            _arq!.Position = dentro;
            _arq.Write(dados, ini, c);
            ini += c;
        }
    }

    // ------------------------------------------------------------------ ler

    /// <summary>Primeira posição ainda guardada.</summary>
    public long Inicio { get { lock (_l) return _ram != null ? 0 : Math.Max(0, _cabeca - CapArquivo); } }

    /// <summary>
    /// Lê até <paramref name="n"/> bytes a partir da posição absoluta <paramref name="pos"/>, esperando se ainda não chegaram. Devolve 0 no fim da época (ou se
    /// <paramref name="parar"/> ficou verdadeiro) e -1 se a posição já saiu do anel.
    /// </summary>
    public int Ler(long pos, byte[] destino, int off, int n, Func<bool>? parar = null)
    {
        lock (_l)
        {
            while (true)
            {
                if (_fechado || parar?.Invoke() == true) return 0;
                if (pos < (_ram != null ? 0 : Math.Max(0, _cabeca - CapArquivo))) return -1;
                if (pos < _cabeca) break;
                if (_fim) return 0;
                Monitor.Wait(_l, 200);
            }
            int c = (int)Math.Min(n, _cabeca - pos);
            if (_ram != null)
            {
                int feito = 0;
                while (feito < c)
                {
                    int bi = (int)((pos + feito) / Bloco), o = (int)((pos + feito) % Bloco);
                    int k = Math.Min(c - feito, Bloco - o);
                    Buffer.BlockCopy(_ram[bi], o, destino, off + feito, k);
                    feito += k;
                }
                return c;
            }
            int lido = 0;
            while (lido < c)
            {
                long dentro = (pos + lido) % CapArquivo;
                int k = (int)Math.Min(c - lido, CapArquivo - dentro);
                _arq!.Position = dentro;
                int r = _arq.Read(destino, off + lido, k);
                if (r <= 0) break;
                lido += r;
            }
            return lido;
        }
    }

    /// <summary>Acorda quem está esperando dados (o DataSource fechou).</summary>
    public void Acordar() { lock (_l) Monitor.PulseAll(_l); }

    // ------------------------------------------------------------------ o que o cache sabe do filme

    /// <summary>A PAT e a PMT mais recentes (2 pacotes), para o player começar do meio do cache. Nulo até chegarem.</summary>
    public byte[]? PatPmt()
    {
        lock (_l)
        {
            if (_pat == null || _pmt == null) return null;
            var r = new byte[Ts * 2];
            Buffer.BlockCopy(_pat, 0, r, 0, Ts); Buffer.BlockCopy(_pmt, 0, r, Ts, Ts);
            return r;
        }
    }

    /// <summary>Até onde o vídeo chegou, em s do filme (NaN antes do primeiro quadro).</summary>
    public double PtsMax { get { lock (_l) return _ptsMax; } }

    /// <summary>Carimbo do primeiro quadro-chave guardado (NaN se ainda não há).</summary>
    public double PrimeiraChave { get { lock (_l) { var i = PrimeiraValida(); return i < 0 ? double.NaN : _chaves[i].Pts; } } }

    public int Chaves { get { lock (_l) return _chaves.Count; } }

    int PrimeiraValida()
    {
        long ini = _ram != null ? 0 : Math.Max(0, _cabeca - CapArquivo);
        for (int i = 0; i < _chaves.Count; i++) if (_chaves[i].Pos >= ini) return i;
        return -1;
    }

    public enum Lugar { Pronto, Esperar, Antes }

    /// <summary>
    /// O ponto <paramref name="s"/> (s do filme) dá para tocar daqui? <see cref="Lugar.Pronto"/>: há um quadro-chave até ele e o vídeo já chegou nele; devolve a posição do
    /// quadro-chave e o carimbo dele. <see cref="Lugar.Esperar"/>: ainda vai chegar. <see cref="Lugar.Antes"/>: ficou para trás do que o cache tem.
    /// </summary>
    public Lugar Procurar(double s, out long pos, out double pts)
    {
        lock (_l)
        {
            pos = 0; pts = double.NaN;
            int i0 = PrimeiraValida();
            if (i0 < 0) return Lugar.Esperar;
            if (s < _chaves[i0].Pts - 0.05) return Lugar.Antes;
            if (double.IsNaN(_ptsMax) || s > _ptsMax) return Lugar.Esperar;
            int escolhida = i0;
            for (int i = i0; i < _chaves.Count && _chaves[i].Pts <= s + 0.001; i++) escolhida = i;
            pos = _chaves[escolhida].Pos; pts = _chaves[escolhida].Pts;
            return Lugar.Pronto;
        }
    }

    // ------------------------------------------------------------------ varredura dos pacotes TS (só cabeçalhos)

    void Varrer(byte[] d, int n)
    {
        int i = 0;
        while (i < n)
        {
            if (_pendN > 0)
            {
                int c = Math.Min(Ts - _pendN, n - i);
                Buffer.BlockCopy(d, i, _pend, _pendN, c);
                _pendN += c; i += c;
                if (_pendN == Ts) { Pacote(_pend, _posPacote); _posPacote += Ts; _pendN = 0; }
                continue;
            }
            // começo de pacote: o byte de sincronia; se não for, anda um byte (o fluxo perdeu o alinhamento)
            if (d[i] != 0x47) { _foraDeSincronia++; i++; _posPacote++; continue; }
            if (n - i >= Ts) { Pacote(d.AsSpan(i, Ts), _posPacote); i += Ts; _posPacote += Ts; }
            else { Buffer.BlockCopy(d, i, _pend, 0, n - i); _pendN = n - i; i = n; }
        }
    }

    void Pacote(ReadOnlySpan<byte> p, long pos)
    {
        _pacotes++;
        int pid = ((p[1] & 0x1F) << 8) | p[2];
        bool inicio = (p[1] & 0x40) != 0;
        int afc = (p[3] >> 4) & 3;
        int carga = 4;
        bool rai = false;
        if ((afc & 2) != 0)
        {
            int afLen = p[4];
            if (afLen > 0 && 5 < Ts) rai = (p[5] & 0x40) != 0;
            carga = 5 + afLen;
        }
        if ((afc & 1) == 0 || carga >= Ts) return;
        if (pid == 0 && inicio) { _pat = p.ToArray(); LerPat(p, carga); return; }
        if (_pmtPid >= 0 && pid == _pmtPid && inicio) { _pmt = p.ToArray(); return; }
        if (!inicio || Ts - carga < 14) return;
        if (p[carga] != 0 || p[carga + 1] != 0 || p[carga + 2] != 1) return;
        int sid = p[carga + 3];
        bool video = sid is >= 0xE0 and <= 0xEF, audio = sid is >= 0xC0 and <= 0xDF || sid == 0xBD;
        if (!video && !audio) return;
        if ((p[carga + 7] >> 6) < 2) return;   // sem PTS
        long pts = ((long)(p[carga + 9] & 0x0E) << 29) | ((long)p[carga + 10] << 22) | ((long)(p[carga + 11] & 0xFE) << 14) | ((long)p[carga + 12] << 7) | (long)(p[carga + 13] >> 1);
        double s = pts / 90000.0;
        if (double.IsNaN(_primeiroPts)) _primeiroPts = s;
        if (!video) return;
        if (double.IsNaN(_ptsMax) || s > _ptsMax) _ptsMax = s;
        if (rai && (_chaves.Count == 0 || _chaves[^1].Pos < pos)) _chaves.Add((s, pos));
    }

    void LerPat(ReadOnlySpan<byte> p, int carga)
    {
        // pointer_field, depois a seção: table_id(1) comprimento(2) id(2) versão(1) seção(1) último(1) e os programas de 4 bytes até o CRC
        int o = carga + 1 + p[carga];
        if (o + 8 > Ts || p[o] != 0) return;
        int comp = ((p[o + 1] & 0x0F) << 8) | p[o + 2];
        int fim = Math.Min(o + 3 + comp - 4, Ts);
        for (int k = o + 8; k + 4 <= fim; k += 4)
        {
            int prog = (p[k] << 8) | p[k + 1], pid = ((p[k + 2] & 0x1F) << 8) | p[k + 3];
            if (prog != 0) { _pmtPid = pid; return; }
        }
    }

    public void Dispose()
    {
        lock (_l)
        {
            _fechado = true; _fim = true;
            Monitor.PulseAll(_l);
            try { _arq?.Dispose(); } catch { }
            _arq = null; _ram = null;
        }
    }
}
