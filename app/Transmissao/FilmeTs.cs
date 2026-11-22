namespace Streemu.Proto.Transmissao;

/// <summary>
/// O que falta entre as amostras do MediaExtractor e o mpegts do filme em cópia direta (spec 028, 5.2): vídeo com tamanho na frente de cada NAL (MP4, MKV) → Annex B com SPS/PPS/VPS
/// antes de cada quadro-chave, AAC cru → ADTS, DTS a partir da ordem de decodificação, bits por canal pelo SPS. Sem nada do Android: compila em net10.0 puro para o teste.
/// </summary>
static class FilmeTs
{
    // ------------------------------------------------------------------ áudio

    static readonly int[] TaxasAac = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    /// <summary>
    /// Do AudioSpecificConfig (csd-0 do AAC): objeto (tipo do AAC), índice da taxa e canais, o que o cabeçalho ADTS pede. Com SBR/PS explícito (tipo 5 ou 29) o objeto do núcleo
    /// vem depois da taxa da extensão, e a taxa do ADTS é a do núcleo (a primeira).
    /// </summary>
    public static (int Objeto, int Freq, int Canais) LerAsc(byte[] asc)
    {
        var b = new Bits(asc);
        int objeto = b.U(5);
        if (objeto == 31) objeto = 32 + b.U(6);
        int freq = b.U(4);
        int taxa = freq;
        if (freq == 15) { int hz = b.U(24); taxa = Array.IndexOf(TaxasAac, hz); if (taxa < 0) throw new InvalidDataException("taxa de amostragem do AAC fora da tabela do ADTS: " + hz); }
        int canais = b.U(4);
        if (objeto is 5 or 29)
        {
            int ext = b.U(4); if (ext == 15) b.U(24);
            objeto = b.U(5); if (objeto == 31) objeto = 32 + b.U(6);
        }
        if (objeto < 1 || objeto > 4) objeto = 2;   // o ADTS só tem 2 bits de perfil (Main, LC, SSR, LTP)
        if (taxa > 12) throw new InvalidDataException("taxa de amostragem do AAC inválida");
        return (objeto, taxa, canais == 0 ? 2 : canais);
    }

    /// <summary>Escreve o cabeçalho ADTS de 7 bytes (sem CRC) de um quadro AAC de <paramref name="tamanho"/> bytes.</summary>
    public static void EscreveAdts(Span<byte> d, (int Objeto, int Freq, int Canais) c, int tamanho)
    {
        int total = tamanho + 7;
        d[0] = 0xFF;
        d[1] = 0xF1;   // sincronia, MPEG-4, camada 0, sem CRC
        d[2] = (byte)(((c.Objeto - 1) << 6) | (c.Freq << 2) | ((c.Canais >> 2) & 1));
        d[3] = (byte)(((c.Canais & 3) << 6) | ((total >> 11) & 3));
        d[4] = (byte)(total >> 3);
        d[5] = (byte)(((total & 7) << 5) | 0x1F);
        d[6] = 0xFC;
    }

    // ------------------------------------------------------------------ vídeo

    /// <summary>
    /// Converte quadros do MediaExtractor para Annex B e põe os conjuntos de parâmetros (VPS/SPS/PPS, o csd do arquivo) na frente de cada quadro-chave que não os traz.
    /// Amostra que já vem em Annex B (arquivo .ts) passa como está. Uma instância por faixa; não é thread-safe.
    /// </summary>
    public sealed class ConversorVideo
    {
        readonly bool _hevc;
        readonly byte[] _parametros;
        int _tamLen = 4;
        byte[] _buf = new byte[1 << 16];
        public ConversorVideo(bool hevc, byte[]? parametrosAnnexB) { _hevc = hevc; _parametros = parametrosAnnexB ?? Array.Empty<byte>(); }

        /// <summary>O quadro em Annex B. O span vale até a próxima chamada.</summary>
        public ReadOnlySpan<byte> Converter(ReadOnlySpan<byte> amostra, bool chave)
        {
            int minimo = amostra.Length + amostra.Length / 2 + _parametros.Length + 64;
            if (_buf.Length < minimo) _buf = new byte[minimo];
            int w = 0;
            bool jaAnnexB = amostra.Length >= 4 && amostra[0] == 0 && amostra[1] == 0 && (amostra[2] == 1 || (amostra[2] == 0 && amostra[3] == 1));
            if (chave && _parametros.Length > 0 && !TemParametros(amostra, jaAnnexB)) { _parametros.CopyTo(_buf, 0); w = _parametros.Length; }
            if (jaAnnexB) { amostra.CopyTo(_buf.AsSpan(w)); w += amostra.Length; return _buf.AsSpan(0, w); }
            int tam = TamanhoDoLen(amostra);
            if (tam == 0) throw new InvalidDataException("amostra de vídeo sem o tamanho dos NAL no formato esperado");
            for (int i = 0; i < amostra.Length;)
            {
                int n = 0;
                for (int k = 0; k < tam; k++) n = (n << 8) | amostra[i + k];
                i += tam;
                _buf[w++] = 0; _buf[w++] = 0; _buf[w++] = 0; _buf[w++] = 1;
                amostra.Slice(i, n).CopyTo(_buf.AsSpan(w));
                w += n; i += n;
            }
            return _buf.AsSpan(0, w);
        }

        /// <summary>O quadro já traz SPS no começo? Olha os NAL da amostra, sem passar do primeiro slice.</summary>
        bool TemParametros(ReadOnlySpan<byte> a, bool annexB)
        {
            int sps = _hevc ? 33 : 7;
            if (annexB)
            {
                for (int i = 0; i + 3 < a.Length; i++)
                    if (a[i] == 0 && a[i + 1] == 0 && a[i + 2] == 1)
                    {
                        int t = Tipo(a[i + 3]);
                        if (t == sps) return true;
                        if (EhSlice(t)) return false;
                        i += 3;
                    }
                return false;
            }
            int tam = TamanhoDoLen(a);
            for (int i = 0; tam > 0 && i + tam < a.Length;)
            {
                int n = 0;
                for (int k = 0; k < tam; k++) n = (n << 8) | a[i + k];
                i += tam;
                if (i >= a.Length) break;
                int t = Tipo(a[i]);
                if (t == sps) return true;
                if (EhSlice(t)) return false;
                i += n;
            }
            return false;
        }

        int Tipo(byte primeiro) => _hevc ? (primeiro >> 1) & 0x3F : primeiro & 0x1F;
        bool EhSlice(int t) => _hevc ? t < 32 : t is >= 1 and <= 5;

        /// <summary>Quantos bytes tem o tamanho de cada NAL (4, 3, 2 ou 1): o que fecha a amostra exatamente. 0 se nenhum fecha.</summary>
        int TamanhoDoLen(ReadOnlySpan<byte> a)
        {
            if (Fecha(a, _tamLen)) return _tamLen;
            foreach (int t in new[] { 4, 2, 3, 1 }) if (Fecha(a, t)) { _tamLen = t; return t; }
            return 0;
        }

        static bool Fecha(ReadOnlySpan<byte> a, int tam)
        {
            int i = 0;
            if (a.Length < tam + 1) return false;
            while (i < a.Length)
            {
                if (i + tam >= a.Length) return false;
                long n = 0;
                for (int k = 0; k < tam; k++) n = (n << 8) | a[i + k];
                i += tam;
                if (n <= 0 || i + n > a.Length || (a[i] & 0x80) != 0) return false;
                i += (int)n;
            }
            return i == a.Length;
        }
    }

    // ------------------------------------------------------------------ DTS

    /// <summary>
    /// O DTS de cada quadro, que o MediaExtractor não dá (só o PTS, em ordem de decodificação). Com quadros B o PTS não cresce na ordem de decodificação; o DTS é o PTS ordenado,
    /// atrasado <c>d</c> posições (a profundidade de reordenação do vídeo): nunca passa do PTS e cresce sempre. Sem quadros B (d = 0) o DTS é o PTS.
    /// Os <c>d</c> primeiros quadros (antes de haver PTS ordenado para tirar) recuam do primeiro PTS de <c>quadroUs</c> em <c>quadroUs</c>.
    /// </summary>
    public sealed class OrdemDts
    {
        readonly int _d;
        readonly long _quadroUs;
        const long PassoMinimoUs = 12;
        readonly PriorityQueue<long, long> _fila = new();
        int _k;
        long _primeiro, _ultimo = long.MinValue;
        public OrdemDts(int profundidade, long quadroUs) { _d = Math.Max(0, profundidade); _quadroUs = Math.Max(1, quadroUs); }

        public long Proximo(long ptsUs)
        {
            if (_d == 0) return ptsUs;
            if (_k == 0) _primeiro = ptsUs;
            _fila.Enqueue(ptsUs, ptsUs);
            long dts = _k < _d ? _primeiro - (_d - _k) * _quadroUs : _fila.Dequeue();
            _k++;
            dts = Math.Max(0, dts);
            // O começo do filme (tudo recuado até 0) e PTS repetido: o DTS tem que crescer. No PES ele vai em 90 kHz (1 tique = 11,1 µs), então o passo mínimo é de 12 µs:
            // com 1 µs os primeiros quadros caíam no mesmo tique e o ffmpeg via DTS repetido.
            if (dts < _ultimo + PassoMinimoUs) dts = _ultimo + PassoMinimoUs;
            return _ultimo = dts;
        }

        /// <summary>A profundidade de reordenação: o maior número de quadros decodificados antes e mostrados depois de um quadro. 0 = sem quadros B.</summary>
        public static int Profundidade(IReadOnlyList<long> ptsEmOrdemDeDecodificacao)
        {
            int max = 0;
            var v = ptsEmOrdemDeDecodificacao;
            for (int j = 0; j < v.Count; j++)
            {
                int maiores = 0;
                for (int i = Math.Max(0, j - 32); i < j; i++) if (v[i] > v[j]) maiores++;
                if (maiores > max) max = maiores;
            }
            return max;
        }
    }

    // ------------------------------------------------------------------ bits por canal (o SPS do csd)

    /// <summary>Bits por canal do vídeo pelo SPS que está no csd (Annex B): 8, 10 ou 12. 8 se não der para ler.</summary>
    public static int BitsDoCsd(byte[]? csd, bool hevc)
    {
        if (csd == null) return 8;
        try
        {
            int alvo = hevc ? 33 : 7;
            for (int i = 0; i + 4 < csd.Length; i++)
            {
                if (csd[i] != 0 || csd[i + 1] != 0 || csd[i + 2] != 1) continue;
                int t = hevc ? (csd[i + 3] >> 1) & 0x3F : csd[i + 3] & 0x1F;
                if (t != alvo) continue;
                int ini = i + 3 + (hevc ? 2 : 1);
                int fim = csd.Length;
                for (int j = ini; j + 2 < csd.Length; j++) if (csd[j] == 0 && csd[j + 1] == 0 && (csd[j + 2] == 1 || csd[j + 2] == 0)) { fim = j; break; }
                var rbsp = SemEmulacao(csd.AsSpan(ini, fim - ini));
                return hevc ? BitsHevc(rbsp) : BitsAvc(rbsp);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IndexOutOfRangeException) { }
        return 8;
    }

    static byte[] SemEmulacao(ReadOnlySpan<byte> d)
    {
        var r = new List<byte>(d.Length);
        for (int i = 0; i < d.Length; i++)
        {
            if (i >= 2 && d[i] == 3 && d[i - 1] == 0 && d[i - 2] == 0) continue;
            r.Add(d[i]);
        }
        return r.ToArray();
    }

    static int BitsAvc(byte[] r)
    {
        var b = new Bits(r);
        int perfil = b.U(8); b.U(16);
        b.Ue();   // seq_parameter_set_id
        if (perfil is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (b.Ue() == 3) b.U(1);
            return 8 + b.Ue();
        }
        return 8;
    }

    static int BitsHevc(byte[] r)
    {
        var b = new Bits(r);
        b.U(4);
        int subcamadas = b.U(3);
        b.U(1);
        b.U(8); b.U(32); b.U(32); b.U(16); b.U(8);   // perfil (espaço, nível, tipo), compatibilidades, 48 bits de restrições, nível
        var perfilSub = new bool[8]; var nivelSub = new bool[8];
        for (int i = 0; i < subcamadas; i++) { perfilSub[i] = b.U(1) == 1; nivelSub[i] = b.U(1) == 1; }
        if (subcamadas > 0) for (int i = subcamadas; i < 8; i++) b.U(2);
        for (int i = 0; i < subcamadas; i++)
        {
            if (perfilSub[i]) { b.U(8); b.U(32); b.U(32); b.U(16); }
            if (nivelSub[i]) b.U(8);
        }
        b.Ue();   // sps_seq_parameter_set_id
        if (b.Ue() == 3) b.U(1);   // chroma_format_idc
        b.Ue(); b.Ue();            // largura e altura
        if (b.U(1) == 1) { b.Ue(); b.Ue(); b.Ue(); b.Ue(); }   // janela de recorte
        return 8 + b.Ue();
    }

    /// <summary>Leitor de bits (big endian) com Exp-Golomb.</summary>
    sealed class Bits
    {
        readonly byte[] _d; int _p;
        public Bits(byte[] d) { _d = d; }
        public int U(int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
            {
                if ((_p >> 3) >= _d.Length) throw new InvalidDataException("fim dos dados");
                v = (v << 1) | (uint)((_d[_p >> 3] >> (7 - (_p & 7))) & 1);
                _p++;
            }
            return (int)v;
        }
        public int Ue()
        {
            int zeros = 0;
            while (U(1) == 0) { if (++zeros > 31) throw new InvalidDataException("Exp-Golomb inválido"); }
            return zeros == 0 ? 0 : (int)((1L << zeros) - 1 + U(zeros));
        }
    }
}
