namespace Streemu.Proto;

/// <summary>
/// Demultiplexador mínimo de mpegts: acha pela PAT/PMT o vídeo H.264 (tipo 0x1B) e o áudio Opus (tipo 0x06 com o descritor de registro "Opus"),
/// e entrega cada PES inteiro com o PTS. Só o que o ffmpeg do Streemu manda: um programa.
///
/// Quando um PES termina: se ele diz o tamanho, assim que completa; se não diz (o ffmpeg omite o tamanho no vídeo por padrão), quando o
/// próximo PES começa. Neste segundo caso o quadro sai com o atraso de um pacote do quadro seguinte, igual ao demultiplexador do ffmpeg/ffplay no PC.
/// No áudio o ffmpeg sempre informa o tamanho.
/// </summary>
sealed class TsDemux
{
    const int Tam = 188;
    int _pmtPid = -1;
    readonly Fluxo _video = new(), _audio = new();
    readonly byte[] _resto = new byte[Tam];
    int _restoLen;

    /// <summary>Um quadro de vídeo: bytes em Annex-B e PTS em unidades de 90 kHz.</summary>
    public Action<byte[], long>? Quadro { get => _video.Pronto; set => _video.Pronto = value; }
    /// <summary>Um PES de áudio Opus (um ou mais pacotes, cada um com o cabeçalho de controle do TS) e o PTS em 90 kHz.</summary>
    public Action<byte[], long>? Audio { get => _audio.Pronto; set => _audio.Pronto = value; }
    /// <summary>Canais do Opus, como a PMT informou (2 se não informou).</summary>
    public int CanaisAudio { get; private set; } = 2;
    public bool TemAudio => _audio.Pid >= 0;
    public long PacotesTs, ForaDeSincronia;
    public long Descontinuidades => _video.Descontinuidades;
    public long QuadrosDescartados => _video.Descartados;
    public long DescontinuidadesAudio => _audio.Descontinuidades;

    /// <summary>
    /// Uma ligação SRT nova começa do zero: esquece o resto de pacote, o contador de continuidade e o PES em montagem dos dois fluxos, para o primeiro
    /// pacote da conexão nova não virar "buraco". Mantém os PIDs (a PMT repete a cada instante) e os contadores acumulados. Chamar da mesma thread que alimenta.
    /// </summary>
    public void Reiniciar()
    {
        _restoLen = 0;
        _video.Reiniciar();
        _audio.Reiniciar();
    }

    public void Alimentar(ReadOnlySpan<byte> dados)
    {
        // o SRT entrega 1316 bytes (7 pacotes); se vier quebrado, junta o pedaço com o resto do anterior
        if (_restoLen > 0)
        {
            int falta = Tam - _restoLen;
            if (dados.Length < falta) { dados.CopyTo(_resto.AsSpan(_restoLen)); _restoLen += dados.Length; return; }
            dados[..falta].CopyTo(_resto.AsSpan(_restoLen));
            Pacote(_resto);
            dados = dados[falta..];
            _restoLen = 0;
        }
        while (dados.Length >= Tam)
        {
            if (dados[0] != 0x47)
            {
                ForaDeSincronia++;
                int i = dados[1..].IndexOf((byte)0x47);
                if (i < 0) return;
                dados = dados[(i + 1)..];
                continue;
            }
            Pacote(dados[..Tam]);
            dados = dados[Tam..];
        }
        if (dados.Length > 0) { dados.CopyTo(_resto); _restoLen = dados.Length; }
    }

    void Pacote(ReadOnlySpan<byte> p)
    {
        PacotesTs++;
        bool inicio = (p[1] & 0x40) != 0;
        int pid = ((p[1] & 0x1F) << 8) | p[2];
        int afc = (p[3] >> 4) & 3, cc = p[3] & 0x0F;
        if ((afc & 1) == 0) return;   // sem dados
        int off = 4;
        if (afc == 3) off += 1 + p[4];
        if (off >= Tam) return;
        var carga = p[off..];

        if (pid == 0 && inicio) { Pat(carga); return; }
        if (pid == _pmtPid && inicio) { Pmt(carga); return; }
        if (pid == _video.Pid) _video.Pacote(carga, inicio, cc);
        else if (pid == _audio.Pid) _audio.Pacote(carga, inicio, cc);
    }

    /// <summary>Monta os PES de um PID.</summary>
    sealed class Fluxo
    {
        public int Pid = -1;
        public Action<byte[], long>? Pronto;
        public long Descontinuidades, Descartados;
        int _cc = -1;
        readonly MemoryStream _pes = new();
        int _esperado;   // bytes de dados do PES (0 = não informado)
        long _pts = -1;
        bool _quebrado;

        public void Reiniciar() { _cc = -1; _pes.SetLength(0); _esperado = 0; _pts = -1; _quebrado = false; }

        public void Pacote(ReadOnlySpan<byte> carga, bool inicio, int cc)
        {
            // contador de continuidade: um buraco = pacote TS perdido = o PES atual chegou incompleto
            if (_cc >= 0 && cc != ((_cc + 1) & 0x0F) && cc != _cc) { Descontinuidades++; _quebrado = true; }
            _cc = cc;

            if (inicio)
            {
                Entregar();
                if (carga.Length < 9 || carga[0] != 0 || carga[1] != 0 || carga[2] != 1) { _quebrado = true; return; }
                int tamPes = (carga[4] << 8) | carga[5];
                int hdl = carga[8];
                _pts = (carga[7] & 0x80) != 0 && carga.Length >= 14 ? Pts(carga[9..]) : -1;
                _esperado = tamPes == 0 ? 0 : tamPes - 3 - hdl;
                _quebrado = false;
                _pes.SetLength(0);
                if (9 + hdl <= carga.Length) _pes.Write(carga[(9 + hdl)..]);
            }
            else if (_pes.Length > 0 || _esperado > 0) _pes.Write(carga);
            // um fluxo sem início de PES nunca fecharia o quadro e a memória cresceria até o Android matar o app
            if (_pes.Length > 8 << 20) { _pes.SetLength(0); _esperado = 0; _quebrado = false; Descartados++; return; }

            if (_esperado > 0 && _pes.Length >= _esperado) Entregar();
        }

        void Entregar()
        {
            if (_pes.Length == 0) return;
            if (_quebrado) Descartados++;
            else Pronto?.Invoke(_pes.ToArray(), _pts);
            _pes.SetLength(0);
            _esperado = 0;
            _quebrado = false;
        }
    }

    static long Pts(ReadOnlySpan<byte> b) =>
        ((long)(b[0] & 0x0E) << 29) | ((long)b[1] << 22) | ((long)(b[2] & 0xFE) << 14) | ((long)b[3] << 7) | ((long)b[4] >> 1);

    /// <summary>A seção da PAT/PMT. Malformada (curta demais para o cabeçalho), devolve vazia: antes lançava e o SRT reabria a cada pacote ruim.</summary>
    static ReadOnlySpan<byte> Secao(ReadOnlySpan<byte> carga, out int fim)
    {
        fim = 0;
        if (carga.Length < 1) return default;
        int ponteiro = carga[0];
        if (1 + ponteiro + 3 > carga.Length) return default;
        var s = carga[(1 + ponteiro)..];
        int len = ((s[1] & 0x0F) << 8) | s[2];
        fim = Math.Clamp(3 + len - 4, 0, s.Length);   // sem o CRC
        return s;
    }

    void Pat(ReadOnlySpan<byte> carga)
    {
        var s = Secao(carga, out int fim);
        for (int i = 8; i + 4 <= fim; i += 4)
        {
            int programa = (s[i] << 8) | s[i + 1];
            if (programa != 0) { _pmtPid = ((s[i + 2] & 0x1F) << 8) | s[i + 3]; return; }
        }
    }

    void Pmt(ReadOnlySpan<byte> carga)
    {
        var s = Secao(carga, out int fim);
        if (s.Length < 12) return;
        int i = 12 + (((s[10] & 0x0F) << 8) | s[11]);
        while (i + 5 <= fim)
        {
            int tipo = s[i], pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2], esil = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
            var desc = s[(i + 5)..Math.Min(i + 5 + esil, fim)];
            if (tipo == 0x1B) _video.Pid = pid;
            else if (tipo == 0x06 && EhOpus(desc, out int canais)) { _audio.Pid = pid; CanaisAudio = canais; }
            i += 5 + esil;
        }
    }

    /// <summary>Descritor de registro (0x05) "Opus"; o de extensão (0x7F, 0x80) traz a configuração de canais (1 = mono, 2 = estéreo).</summary>
    static bool EhOpus(ReadOnlySpan<byte> d, out int canais)
    {
        bool opus = false; canais = 2;
        for (int i = 0; i + 2 <= d.Length;)
        {
            int tag = d[i], len = d[i + 1];
            if (i + 2 + len > d.Length) break;
            var v = d.Slice(i + 2, len);
            if (tag == 0x05 && len >= 4 && v[0] == 'O' && v[1] == 'p' && v[2] == 'u' && v[3] == 's') opus = true;
            if (tag == 0x7F && len >= 2 && v[0] == 0x80 && v[1] is 1 or 2) canais = v[1];
            i += 2 + len;
        }
        return opus;
    }
}
