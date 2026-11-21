namespace Streemu.Proto.Transmissao;

/// <summary>
/// Multiplexador mpegts mínimo para o celular transmitir, no mesmo formato que o ffmpeg do app Windows manda
/// (um programa, vídeo H.264 tipo 0x1B, áudio Opus tipo 0x06 com descritor de registro "Opus" e de extensão 0x7F/0x80 com os canais,
/// PCR no PID do vídeo, PES de vídeo COM tamanho quando cabe, cada pacote Opus com o cabeçalho de controle do TS).
/// Sem dependência do Android: compila em net10.0 puro para ser testado no PC.
///
/// Uma instância só para a transmissão inteira; cada espectador recebe os mesmos bytes a partir de um bloco com <c>inicioDeChave</c>.
/// Thread-safe: vídeo e áudio chegam de threads diferentes. O evento <see cref="Saida"/> sai de dentro do lock (a ordem dos blocos é a ordem
/// dos contadores de continuidade), então o assinante tem que ser rápido e não pode chamar o mux de volta.
/// </summary>
sealed class TsMux
{
    const int Tam = 188;
    const int PidPmt = 0x1000, PidVideo = 0x100, PidAudio = 0x101;
    /// <summary>Folga do primeiro PTS (100 ms) para o PCR, que vem antes, nunca ficar negativo.</summary>
    const long FolgaInicial = 9000;
    /// <summary>O PCR vem 2 ms antes do PTS do quadro (como o ffmpeg com muxdelay 0: o relógio quase não adianta o quadro).</summary>
    const long AdiantoPcr = 180;
    const long RepetePsiUs = 100_000;
    const long Mascara33 = (1L << 33) - 1;

    readonly bool _comAudio;
    readonly int _canais;
    readonly object _lock = new();
    readonly int[] _cc = new int[2];      // contadores de continuidade: vídeo e áudio
    readonly int[] _ccPsi = new int[2];   // PAT e PMT
    byte[] _pes = new byte[1 << 16];
    bool _temBase, _primeiro = true;
    long _baseUs, _ultimoPsiUs;

    /// <summary>Quantos blocos e bytes saíram (para diagnóstico).</summary>
    public long Blocos, Bytes;

    /// <param name="comAudio">Anuncia a faixa Opus na PMT.</param>
    /// <param name="canaisAudio">1 ou 2.</param>
    public TsMux(bool comAudio, int canaisAudio = 2)
    {
        _comAudio = comAudio;
        _canais = canaisAudio == 1 ? 1 : 2;
    }

    /// <summary>
    /// Saída: bytes TS (múltiplo de 188) de UMA unidade de acesso. <c>inicioDeChave</c> = o bloco começa com PAT+PMT e um IDR com SPS/PPS:
    /// quem entra agora pode começar daqui. O array é novo a cada chamada (quem recebe pode guardar).
    /// </summary>
    public event Action<byte[], bool>? Saida;

    /// <summary>Um quadro em Annex-B (com 00 00 00 01). <paramref name="ptsUs"/> no relógio comum (µs, monotônico). Quadro-chave já vem com SPS/PPS.</summary>
    public void Video(ReadOnlySpan<byte> annexB, long ptsUs, bool chave)
    {
        if (annexB.IsEmpty) return;
        lock (_lock)
        {
            long pts = Pts90k(ptsUs);
            bool temAud = TemAud(annexB);
            int carga = (temAud ? 0 : 6) + annexB.Length;
            int pesLen = 3 + 5 + carga;

            // PES: 00 00 01 E0 | tamanho | 80 80 05 | PTS | [AUD] quadro
            int total = 14 + carga;
            Garantir(total);
            var p = _pes;
            p[0] = 0; p[1] = 0; p[2] = 1; p[3] = 0xE0;
            int declarado = pesLen <= 0xFFFF ? pesLen : 0;
            p[4] = (byte)(declarado >> 8); p[5] = (byte)declarado;
            p[6] = 0x80; p[7] = 0x80; p[8] = 5;
            EscreveTs(p.AsSpan(9), 2, pts);
            int o = 14;
            if (!temAud) { p[o++] = 0; p[o++] = 0; p[o++] = 0; p[o++] = 1; p[o++] = 0x09; p[o++] = 0xF0; }
            annexB.CopyTo(p.AsSpan(o));

            bool psi = chave || _primeiro || ptsUs - _ultimoPsiUs >= RepetePsiUs;
            Emitir(PidVideo, total, psi, chave, (pts - AdiantoPcr) & Mascara33, ptsUs);
        }
    }

    /// <summary>Um pacote Opus cru (sem cabeçalho), 10 ou 20 ms, no mesmo relógio do vídeo.</summary>
    public void Audio(ReadOnlySpan<byte> opus, long ptsUs)
    {
        if (!_comAudio || opus.IsEmpty) return;
        lock (_lock)
        {
            long pts = Pts90k(ptsUs);
            // cabeçalho de controle do Opus em TS: 0x7F 0xE0 e o tamanho em bytes 0xFF... + resto (como o ffmpeg)
            int carga = 2 + (opus.Length / 255 + 1) + opus.Length;
            int pesLen = 3 + 5 + carga;
            int total = 14 + carga;
            Garantir(total);
            var p = _pes;
            p[0] = 0; p[1] = 0; p[2] = 1; p[3] = 0xBD;
            p[4] = (byte)(pesLen >> 8); p[5] = (byte)pesLen;   // um pacote Opus nunca passa de 64 KB
            p[6] = 0x80; p[7] = 0x80; p[8] = 5;
            EscreveTs(p.AsSpan(9), 2, pts);
            int o = 14;
            p[o++] = 0x7F; p[o++] = 0xE0;
            for (int r = opus.Length; ; r -= 255) { p[o++] = (byte)Math.Min(r, 255); if (r < 255) break; }
            opus.CopyTo(p.AsSpan(o));

            bool psi = _primeiro || ptsUs - _ultimoPsiUs >= RepetePsiUs;
            Emitir(PidAudio, total, psi, false, 0, ptsUs);
        }
    }

    void Garantir(int n) { if (_pes.Length < n) _pes = new byte[Math.Max(n, _pes.Length * 2)]; }

    long Pts90k(long ptsUs)
    {
        if (!_temBase) { _temBase = true; _baseUs = ptsUs; }
        return (((ptsUs - _baseUs) * 9 / 100) + FolgaInicial) & Mascara33;
    }

    /// <summary>Corta o PES (em _pes[0..total]) em pacotes TS, com PAT/PMT na frente se pedido, e entrega. Só o vídeo leva PCR.</summary>
    void Emitir(int pid, int total, bool psi, bool chave, long pcr, long ptsUs)
    {
        bool comPcr = pid == PidVideo;
        int cap0 = comPcr ? Tam - 4 - 8 : Tam - 4;
        int pacotes = 1 + (total > cap0 ? (total - cap0 + 183) / 184 : 0);
        var saida = new byte[((psi ? 2 : 0) + pacotes) * Tam];
        int w = 0;
        if (psi) { w = EscrevePsi(saida); _ultimoPsiUs = ptsUs; _primeiro = false; }

        int idx = comPcr ? 0 : 1;
        int pos = 0;
        for (int k = 0; k < pacotes; k++)
        {
            bool primeiro = k == 0;
            bool pcrAqui = primeiro && comPcr;
            int n = Math.Min(total - pos, pcrAqui ? cap0 : Tam - 4);
            saida[w++] = 0x47;
            saida[w++] = (byte)((primeiro ? 0x40 : 0) | (pid >> 8));
            saida[w++] = (byte)pid;
            int cc = _cc[idx]; _cc[idx] = (cc + 1) & 0x0F;
            bool af = pcrAqui || n < Tam - 4;
            saida[w++] = (byte)((af ? 0x30 : 0x10) | cc);
            if (af)
            {
                // enchimento no adaptation field (não pacote nulo): o último pacote do PES fica do tamanho certo
                int afLen = Tam - 4 - 1 - n;   // bytes depois do campo de tamanho
                saida[w++] = (byte)afLen;
                if (afLen > 0)
                {
                    int fim = w + afLen;
                    saida[w++] = (byte)((pcrAqui ? 0x10 : 0) | (pcrAqui && chave ? 0x40 : 0));
                    if (pcrAqui)
                    {
                        saida[w++] = (byte)(pcr >> 25); saida[w++] = (byte)(pcr >> 17); saida[w++] = (byte)(pcr >> 9);
                        saida[w++] = (byte)(pcr >> 1); saida[w++] = (byte)(((pcr & 1) << 7) | 0x7E); saida[w++] = 0;
                    }
                    while (w < fim) saida[w++] = 0xFF;
                }
            }
            Buffer.BlockCopy(_pes, pos, saida, w, n);
            w += n; pos += n;
        }
        Blocos++; Bytes += saida.Length;
        Saida?.Invoke(saida, chave);
    }

    int EscrevePsi(byte[] d)
    {
        Span<byte> s = stackalloc byte[184];
        // PAT
        int n = 0;
        s[n++] = 0x00; s[n++] = 0xB0; s[n++] = 13;   // table_id 0, tamanho 13
        s[n++] = 0; s[n++] = 1;                       // transport_stream_id
        s[n++] = 0xC1; s[n++] = 0; s[n++] = 0;        // versão 0, current
        s[n++] = 0; s[n++] = 1;                       // programa 1
        s[n++] = (byte)(0xE0 | (PidPmt >> 8)); s[n++] = (byte)(PidPmt & 0xFF);
        n = Crc(s, n);
        int w = Pacote(d, 0, 0, 0, s[..n]);

        // PMT
        n = 0;
        s[n++] = 0x02; s[n++] = 0xB0; s[n++] = 0;     // tamanho preenchido no fim
        s[n++] = 0; s[n++] = 1;
        s[n++] = 0xC1; s[n++] = 0; s[n++] = 0;
        s[n++] = (byte)(0xE0 | (PidVideo >> 8)); s[n++] = (byte)(PidVideo & 0xFF);   // PID do PCR
        s[n++] = 0xF0; s[n++] = 0;                    // sem descritores do programa
        s[n++] = 0x1B; s[n++] = (byte)(0xE0 | (PidVideo >> 8)); s[n++] = (byte)(PidVideo & 0xFF); s[n++] = 0xF0; s[n++] = 0;
        if (_comAudio)
        {
            s[n++] = 0x06; s[n++] = (byte)(0xE0 | (PidAudio >> 8)); s[n++] = (byte)(PidAudio & 0xFF); s[n++] = 0xF0; s[n++] = 10;
            s[n++] = 0x05; s[n++] = 4; s[n++] = (byte)'O'; s[n++] = (byte)'p'; s[n++] = (byte)'u'; s[n++] = (byte)'s';
            s[n++] = 0x7F; s[n++] = 2; s[n++] = 0x80; s[n++] = (byte)_canais;
        }
        int len = n + 4 - 3;
        s[1] = (byte)(0xB0 | (len >> 8)); s[2] = (byte)len;
        n = Crc(s, n);
        return Pacote(d, w, PidPmt, 1, s[..n]);
    }

    /// <summary>Um pacote só com uma seção (ponteiro 0), enchido com 0xFF como o ffmpeg faz.</summary>
    int Pacote(byte[] d, int w, int pid, int idx, ReadOnlySpan<byte> secao)
    {
        int ini = w;
        d[w++] = 0x47; d[w++] = (byte)(0x40 | (pid >> 8)); d[w++] = (byte)pid;
        int cc = _ccPsi[idx]; _ccPsi[idx] = (cc + 1) & 0x0F;
        d[w++] = (byte)(0x10 | cc);
        d[w++] = 0;
        secao.CopyTo(d.AsSpan(w));
        w += secao.Length;
        while (w < ini + Tam) d[w++] = 0xFF;
        return w;
    }

    /// <summary>CRC32 MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, sem reflexão) de s[0..n], acrescentado ao fim.</summary>
    static int Crc(Span<byte> s, int n)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < n; i++)
        {
            crc ^= (uint)s[i] << 24;
            for (int b = 0; b < 8; b++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        s[n++] = (byte)(crc >> 24); s[n++] = (byte)(crc >> 16); s[n++] = (byte)(crc >> 8); s[n++] = (byte)crc;
        return n;
    }

    /// <summary>PTS de 33 bits no formato de 5 bytes do PES; <paramref name="marca"/> = 2 (só PTS).</summary>
    static void EscreveTs(Span<byte> d, int marca, long v)
    {
        d[0] = (byte)((marca << 4) | (int)((v >> 29) & 0x0E) | 1);
        d[1] = (byte)(v >> 22);
        d[2] = (byte)((v >> 14) | 1);
        d[3] = (byte)(v >> 7);
        d[4] = (byte)((v << 1) | 1);
    }

    /// <summary>O quadro já começa com AUD (tipo 9)? Olha só o primeiro NAL.</summary>
    static bool TemAud(ReadOnlySpan<byte> d)
    {
        int i = 0;
        while (i < d.Length && d[i] == 0) i++;
        return i >= 2 && i + 1 < d.Length && d[i] == 1 && (d[i + 1] & 0x1F) == 9;
    }
}
