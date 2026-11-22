using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Streemu.Net;
using Streemu.Util;

namespace Streemu.Filme;

/// <summary>
/// Transporte confiável do arquivo do modo filme sobre o túnel (spec 010, 4.2), no tipo de pacote <see cref="Tunnel.TArquivo"/>.
/// O túnel é UDP e o SRT de hoje é de vídeo ao vivo (descarta o que atrasa), por isso um transporte próprio:
/// quem baixa pede uma janela de pedaços e conta, a cada 20 ms, o que já tem de cada um (confirmação seletiva por mapa de bits);
/// quem manda reenvia o que falta depois de um tempo sem resposta. A taxa sobe enquanto não há perda e cai quando há (como o TCP),
/// para manter a transferência útil. Cada pacote vai cifrado e autenticado (AES-GCM) com a chave da ligação.
/// </summary>
static class Pacote
{
    public const int Pedaco = 1 << 20;
    /// <summary>Dados por pacote: com os cabeçalhos (túnel 7 + nosso 9 + pedaço 6 + tag 16) fica abaixo de 1280 (o mínimo do IPv6).</summary>
    public const int Frag = 1200;
    public const byte KDados = 1, KEstado = 2;
    /// <summary>Pedaços com este bit são do manifesto (a lista de hashes), não do arquivo.</summary>
    public const uint Manifesto = 0x8000_0000;
    const int Cab = 9, Tag = 16;
    public const int Max = Cab + 6 + Frag + Tag;

    public static int Frags(int tamanho) => (tamanho + Frag - 1) / Frag;

    // [tipo 1][sal 4][seq 4][cifrado][tag 16]. Nonce = os 9 bytes do cabeçalho + 3 zeros: o tipo separa os dois sentidos,
    // o sal (sorteado por transferência) separa transferências na mesma ligação e o seq nunca repete dentro dela.
    public static int Selar(AesGcm aes, byte tipo, uint sal, uint seq, ReadOnlySpan<byte> claro, Span<byte> saida)
    {
        saida[0] = tipo;
        BinaryPrimitives.WriteUInt32LittleEndian(saida[1..], sal);
        BinaryPrimitives.WriteUInt32LittleEndian(saida[5..], seq);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        saida[..Cab].CopyTo(nonce);
        aes.Encrypt(nonce, claro, saida.Slice(Cab, claro.Length), saida.Slice(Cab + claro.Length, Tag), saida[..Cab]);
        return Cab + claro.Length + Tag;
    }

    public static bool Abrir(AesGcm aes, ReadOnlySpan<byte> pkt, Span<byte> claro, out byte tipo, out uint seq, out int n)
    {
        tipo = 0; seq = 0; n = pkt.Length - Cab - Tag;
        if (n < 0 || n > claro.Length) return false;
        tipo = pkt[0];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt[5..]);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        pkt[..Cab].CopyTo(nonce);
        try { aes.Decrypt(nonce, pkt.Slice(Cab, n), pkt.Slice(Cab + n, Tag), claro[..n], pkt[..Cab]); return true; }
        catch (CryptographicException) { return false; }
    }

    public static uint Sal() => (uint)RandomNumberGenerator.GetInt32(int.MaxValue) ^ ((uint)RandomNumberGenerator.GetInt32(2) << 31);

    // Android/Linux: não há winmm (o relógio do sistema já acorda a tempo); só o Windows chama
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint WinBegin(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint WinEnd(uint ms);
    public static uint timeBeginPeriod(uint ms) => OperatingSystem.IsWindows() ? WinBegin(ms) : 0;
    public static uint timeEndPeriod(uint ms) => OperatingSystem.IsWindows() ? WinEnd(ms) : 0;
}

/// <summary>
/// Teste: imita uma rede ruim na chegada dos pacotes do arquivo (o loopback não perde nada e responde em 0,1 ms, e assim o reenvio
/// e o recuo da taxa nunca rodam). STREEMU_TEST_FILME_PERDA=% descarta ao acaso; STREEMU_TEST_FILME_ATRASO=ms segura cada pacote esse
/// tempo (vale nos dois sentidos: a ida e volta fica 2x). Só os pacotes do arquivo; o resto do túnel não muda.
/// STREEMU_TEST_FILME_ATRASO_EXTRA=ms (+ STREEMU_TEST_FILME_ATRASO_EXTRA_DEPOIS=s) imita a fila de OUTRO tráfego (caso do campo, 2026-11-20):
/// um atraso que não depende da nossa taxa, em ms de ida e volta (metade em cada sentido), que começa depois desses segundos e varia
/// (a cada 50-200 ms muda o nível, e em ~5% das vezes volta para perto da base, como as amostras de ~15 ms que ainda apareciam no campo).
/// </summary>
static class Simulador
{
    public static readonly double Perda = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_PERDA"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p / 100 : 0;
    public static readonly int AtrasoMs = int.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ATRASO"), out var a) ? a : 0;
    public static readonly double ExtraMs = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ATRASO_EXTRA"), System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0 ? x : 0;
    static readonly double ExtraDepoisS = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_TEST_FILME_ATRASO_EXTRA_DEPOIS"), System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0;
    public static bool Ligado => Perda > 0 || AtrasoMs > 0 || ExtraMs > 0;
    /// <summary>A ida e volta que o túnel não vê (o ping não passa pelo simulador).</summary>
    public static double IdaVoltaMs => 2 * AtrasoMs;

    /// <summary>Embrulha o tratador: descarta e/ou atrasa. Com atraso, tudo chega por uma thread só (o tratador não precisa de trava nova).</summary>
    public static Action<ReadOnlySpan<byte>> Embrulhar(Action<ReadOnlySpan<byte>> tratar, Func<bool> fim)
    {
        if (!Ligado) return tratar;
        if (AtrasoMs <= 0 && ExtraMs <= 0) return pkt => { if (Random.Shared.NextDouble() >= Perda) tratar(pkt); };
        var fila = new System.Collections.Concurrent.BlockingCollection<(long Ate, byte[] Pkt)>();
        long inicioExtra = 0, trocaEm = 0, ultimoAte = 0; // o relógio do extra parte do primeiro pacote (o laço do envio nasce bem antes de o download começar)
        double nivel = 0;
        // atraso extra deste instante: nível que muda a cada 50-200 ms; a ordem de chegada se mantém (cada pacote sai depois do anterior)
        long AteComExtra(long agora)
        {
            long ate = agora + AtrasoMs * Stopwatch.Frequency / 1000;
            if (ExtraMs <= 0) return ate;
            if (inicioExtra == 0) inicioExtra = agora + (long)(ExtraDepoisS * Stopwatch.Frequency);
            if (agora < inicioExtra) return ate;
            if (agora >= trocaEm)
            {
                trocaEm = agora + (long)(Random.Shared.Next(50, 200) * Stopwatch.Frequency / 1000);
                nivel = Random.Shared.NextDouble() < 0.05 ? 0 : ExtraMs / 2 * (0.7 + 0.6 * Random.Shared.NextDouble());
            }
            ate += (long)(nivel * Stopwatch.Frequency / 1000);
            return ultimoAte = Math.Max(ultimoAte, ate);
        }
        new Thread(() =>
        {
            Pacote.timeBeginPeriod(1);
            try
            {
                while (!fim())
                {
                    if (!fila.TryTake(out var it, 100)) continue;
                    while (Stopwatch.GetTimestamp() < it.Ate) Thread.Sleep(1);
                    tratar(it.Pkt);
                }
            }
            catch (Exception e) when (!fim()) { Log.Info($"[filme] simulador parou: {e.Message}"); }
            finally { Pacote.timeEndPeriod(1); }
        }) { IsBackground = true, Name = "filme-simulador" }.Start();
        return pkt => { if (Random.Shared.NextDouble() >= Perda) fila.Add((AteComExtra(Stopwatch.GetTimestamp()), pkt.ToArray())); };
    }
}

/// <summary>Quem manda (o anfitrião): responde ao estado de quem baixa mandando o que falta, no ritmo da taxa.</summary>
public sealed class Emissor : IDisposable
{
    const double TaxaMin = 128 * 1024;
    /// <summary>Teto opcional para teste (STREEMU_FILME_TETO em bytes/s).</summary>
    static readonly double? TetoTeste = double.TryParse(Environment.GetEnvironmentVariable("STREEMU_FILME_TETO"), out var t) && t > 0 ? t : null;

    readonly Tunnel _t;
    readonly AesGcm _sela, _abre;
    readonly uint _sal = Pacote.Sal();
    uint _seq;
    readonly Func<uint, int> _tamanho;
    readonly Action<uint, byte[], int> _ler;
    readonly Func<bool> _recuar;
    readonly bool _priorizarLigacaoDireta;
    readonly Thread _thread;
    readonly Action<ReadOnlySpan<byte>> _entrada;
    volatile bool _fim;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly object _gate = new();

    volatile (uint Peca, byte[]? Tem)[] _pedidos = Array.Empty<(uint, byte[]?)>();
    long _estadoMs = -1;
    /// <summary>Hora (ticks) em que cada seq saiu, para medir a ida e volta pelo eco de quem baixa.</summary>
    readonly long[] _saiuEm = new long[1 << 17];
    // controle de taxa (só na thread que recebe o estado, dentro do _gate)
    double _taxa = 1 << 20, _melhor, _perdaBase, _srtt = -1, _rttJanela = double.MaxValue;
    bool _inicio = true;
    long _jMs = -1, _quedaMs = -100_000, _subidaMs = -100_000, _jBytes;
    uint _jRx, _jMax;
    uint _salEstado, _seqEstado;
    int _janelasComPerda;
    bool _prioridadeDireta;
    long _bytes, _reenvios;
    /// <summary>Menor ida e volta de cada bloco de 10 s do último minuto: a ida e volta sem fila (a base do LEDBAT).</summary>
    readonly double[] _base = Enumerable.Repeat(double.MaxValue, 6).ToArray();
    long _baseBloco = -1;
    // fila que não é nossa (só sem live saindo): deslocamento somado à Base enquanto a fila continua com a taxa lá embaixo
    double _baseAjuste;
    long _filaDesdeMs = -1, _ultimaCheiaMs, _reancoradoMs = -100_000, _realBaixoDesde = -1;
    double _taxaNaFila;
    /// <summary>Menor ida e volta de cada janela de avaliação dos últimos 2 s (para a mediana usada ao reancorar).</summary>
    readonly (long Ms, double Rtt)[] _janelas = new (long, double)[32];
    int _janelaN;
    // log a cada 5 s (o S3 entre dois PCs sai daqui, pela telemetria)
    long _lMs = -1, _lBytes, _lReenvios;
    uint _lEnviados, _lChegaram;
    int _lQuedas;
    double _lFila;

    /// <summary>Ida e volta para o reenvio: a medida pelos pacotes do arquivo; antes da primeira, a do ping do túnel.</summary>
    double Rtt => _srtt > 0 ? _srtt : Math.Max(1, _t.RttMs) + Simulador.IdaVoltaMs;
    double Base => _base.Min();
    public double Taxa => _taxa;
    public long BytesEnviados => Interlocked.Read(ref _bytes);
    public long Reenvios => Interlocked.Read(ref _reenvios);

    /// <param name="ler">Lê o pedaço inteiro no buffer (tamanho de <paramref name="tamanho"/>).</param>
    /// <param name="recuar">Há live saindo deste PC: Cinema e relay cedem parte do upload.</param>
    /// <param name="priorizarLigacaoDireta">O pedido veio de Enviar arquivo e tem prioridade quando a ligação é direta.</param>
    public Emissor(Tunnel t, Func<uint, int> tamanho, Action<uint, byte[], int> ler, Func<bool> recuar, bool priorizarLigacaoDireta = false)
    {
        _t = t; _tamanho = tamanho; _ler = ler; _recuar = recuar;
        _priorizarLigacaoDireta = priorizarLigacaoDireta;
        _sela = new AesGcm(t.ChaveArquivo, 16);
        _abre = new AesGcm(t.ChaveArquivo, 16);
        t.Arquivo = _entrada = Simulador.Embrulhar(Receber, () => _fim);
        _thread = new Thread(Laco) { IsBackground = true, Name = "filme-envio" };
        _thread.Start();
    }

    void Receber(ReadOnlySpan<byte> pkt)
    {
        long chegou = Stopwatch.GetTimestamp();
        Span<byte> claro = stackalloc byte[Pacote.Max];
        if (!Pacote.Abrir(_abre, pkt, claro, out byte tipo, out uint seq, out int n) || tipo != Pacote.KEstado || n < 17) return;
        uint rx = BinaryPrimitives.ReadUInt32LittleEndian(claro), max = BinaryPrimitives.ReadUInt32LittleEndian(claro[4..]);
        uint eco = BinaryPrimitives.ReadUInt32LittleEndian(claro[8..]);
        long esperaUs = BinaryPrimitives.ReadUInt32LittleEndian(claro[12..]);
        int qtd = claro[16], o = 17;
        var lista = new (uint, byte[]?)[qtd];
        for (int i = 0; i < qtd; i++)
        {
            if (o + 6 > n) return;
            uint p = BinaryPrimitives.ReadUInt32LittleEndian(claro[o..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(claro[(o + 4)..]);
            o += 6;
            if (o + len > n) return;
            lista[i] = (p, len == 0 ? null : claro.Slice(o, len).ToArray());
            o += len;
        }
        long agora = _sw.ElapsedMilliseconds;
        lock (_gate)
        {
            if (_priorizarLigacaoDireta)
            {
                // Estado fora de ordem falsearia a perda (o delta do contador sem sinal viraria bilhões).
                // Quem baixa pode reabrir o Receptor, com sal e contadores novos.
                uint sal = BinaryPrimitives.ReadUInt32LittleEndian(pkt[1..]);
                if (sal == _salEstado && _seqEstado != 0 && (int)(seq - _seqEstado) <= 0) return;
                if (sal != _salEstado)
                {
                    _salEstado = sal;
                    _jMs = -1;
                    _janelasComPerda = 0;
                }
                _seqEstado = seq;
                AtualizarPrioridadeDireta();
            }
            _pedidos = lista;
            Volatile.Write(ref _estadoMs, agora);
            // ida e volta = agora - quando o seq do eco saiu - quanto ele esperou do outro lado até este estado sair
            if (eco != 0 && _seq - eco < _saiuEm.Length && Volatile.Read(ref _saiuEm[eco & (_saiuEm.Length - 1)]) is long saiu && saiu > 0)
            {
                double rtt = (chegou - saiu) * 1000.0 / Stopwatch.Frequency - esperaUs / 1000.0;
                if (rtt > 0 && rtt < 5000)
                {
                    _srtt = _srtt < 0 ? rtt : _srtt * 0.875 + rtt * 0.125;
                    _rttJanela = Math.Min(_rttJanela, rtt);
                    long bloco = agora / 10_000;
                    if (bloco != _baseBloco) { _baseBloco = bloco; _base[bloco % _base.Length] = double.MaxValue; }
                    _base[bloco % _base.Length] = Math.Min(_base[bloco % _base.Length], rtt);
                }
            }
            AjustarTaxa(agora, rx, max);
        }
    }

    void AtualizarPrioridadeDireta()
    {
        bool direta = _t.Target is { Kind: CandKind.Direct };
        if (direta == _prioridadeDireta) return;
        _prioridadeDireta = direta;
        // Num caminho novo, a taxa e os sinais de fila/perda do anterior deixam de valer.
        _taxa = 1 << 20;
        _melhor = _perdaBase = _baseAjuste = 0;
        _srtt = -1;
        _rttJanela = double.MaxValue;
        Array.Fill(_base, double.MaxValue);
        Array.Clear(_janelas);
        _filaDesdeMs = _realBaixoDesde = _jMs = -1;
        _quedaMs = _subidaMs = _reancoradoMs = -100_000;
        _janelasComPerda = 0;
        _inicio = true;
        if (direta)
            Log.Info("[arquivo] envio direto com prioridade de banda" + (_recuar() ? " (live saindo)." : "."));
    }

    void AjustarTaxa(long agora, uint rx, uint max)
    {
        long bytes = Interlocked.Read(ref _bytes);
        if (_jMs < 0) { _jMs = agora; _jRx = rx; _jMax = max; _jBytes = bytes; return; }
        long dt = agora - _jMs;
        uint enviados = max - _jMax, chegaram = rx - _jRx;
        double esperarSemPacotes = _prioridadeDireta ? Math.Max(1000, 2 * Rtt) : 1000;
        if (dt < (_prioridadeDireta ? 200 : 100) || (enviados < 30 && dt < esperarSemPacotes)) return;
        long usados = bytes - _jBytes;
        double rttJanela = _rttJanela;
        _jMs = agora; _jRx = rx; _jMax = max; _jBytes = bytes; _rttJanela = double.MaxValue;
        _lEnviados += enviados; _lChegaram += chegaram;
        bool recuar = !_prioridadeDireta && _recuar();
        double perda = enviados == 0 ? 0 : 1 - Math.Min(1, chegaram / (double)enviados);
        if (_prioridadeDireta)
        {
            AjustarTaxaDireta(agora, dt, enviados, chegaram, usados, perda);
            if (rttJanela < double.MaxValue && Base < double.MaxValue)
                _lFila = Math.Max(_lFila, rttJanela - Base); // só para o diagnóstico
        }
        else
        {
            AjustarTaxaComFila(agora, enviados, usados * 1000.0 / dt, perda, recuar, rttJanela);
        }
        _taxa = Math.Min(_taxa, TetoTeste ?? (_prioridadeDireta ? double.PositiveInfinity : 1e9));
        if (_lMs < 0) _lMs = agora;
        else if (agora - _lMs >= 5000)
        {
            long reenvios = Interlocked.Read(ref _reenvios);
            Log.Info($"[filme] envio: {(bytes - _lBytes) / 1e3 / (agora - _lMs):N1} MB/s, taxa {_taxa / 1e6:N1} MB/s, " +
                     $"perda {(_lEnviados == 0 ? 0 : 100.0 * (1 - Math.Min(1, _lChegaram / (double)_lEnviados))):N1}% (fundo {_perdaBase * 100:N1}%), " +
                     $"reenvios {reenvios - _lReenvios}, quedas {_lQuedas}, ida e volta {Rtt:N0} ms (base {(Base < double.MaxValue ? Base : -1):N0}, fila máx {_lFila:N0})" +
                     (_prioridadeDireta ? ", arquivo direto com prioridade" : recuar ? ", recuando (live saindo)" : "") +
                     (!_prioridadeDireta && !recuar && _baseAjuste > 0 ? $", referência +{_baseAjuste:N0} ms (fila de outro tráfego)" : "") + ".");
            _lMs = agora; _lBytes = bytes; _lReenvios = reenvios; _lEnviados = _lChegaram = 0; _lQuedas = 0; _lFila = 0;
        }
    }

    void AjustarTaxaDireta(long agora, long dt, uint enviados, uint chegaram, long usados, double perda)
    {
        double taxaEnviada = usados * 1000.0 / dt;
        double intervalo = Math.Max(200, Rtt);
        // A maior sequência só avança quando algum pacote chega. Se o caminho parou de receber
        // dados enquanto o emissor continua ocupado, o delta sozinho esconderia essa perda.
        bool semRetorno = chegaram == 0 && dt >= Math.Max(1000, 2 * Rtt) && taxaEnviada > _taxa * 0.6;
        // No piso, perda com alguma entrega pode ser da própria rede. Aprender esse fundo
        // permite voltar a sondar quando reduzir a taxa já não melhora a entrega.
        if (_taxa <= TaxaMin && chegaram > 0 && enviados >= 30)
            _perdaBase = Math.Min(0.2, _perdaBase * 0.95 + perda * 0.05);
        bool perdaAlta = enviados >= 30 && perda > _perdaBase + 0.05;
        if (semRetorno || perdaAlta)
        {
            _janelasComPerda++;
            // Reordenação e retornos agrupados podem abrir um buraco em uma única amostra.
            if ((semRetorno || _janelasComPerda >= 2) && agora - _quedaMs >= intervalo)
            {
                _taxa = Math.Max(TaxaMin, _taxa * (semRetorno || perda >= 0.2 ? 0.65 : 0.85));
                _quedaMs = agora;
                _inicio = false;
                _janelasComPerda = 0;
                _lQuedas++;
            }
            return;
        }

        if (enviados < 30) return;
        _janelasComPerda = 0;
        if (_taxa > TaxaMin) _perdaBase = Math.Min(0.2, _perdaBase * 0.95 + perda * 0.05);
        // Só sonda mais se os pacotes estão chegando e há trabalho suficiente para usar a taxa.
        // A taxa recebida usa os pacotes confirmados; o RTT dá tempo para a sonda aparecer nas confirmações.
        double taxaRecebida = chegaram * (double)Pacote.Max * 1000 / dt;
        if (Math.Min(taxaRecebida, taxaEnviada) > _taxa * 0.7 &&
            agora - _quedaMs >= intervalo && agora - _subidaMs >= intervalo)
        {
            _taxa *= _inicio ? 1.5 : 1.1;
            _subidaMs = agora;
        }
    }

    /// <summary>
    /// Controle de taxa do Cinema e do relay, por atraso como o LEDBAT: a fila no caminho é a ida e
    /// volta medida menos a menor do último minuto. Fila acima do alvo (25 ms; 10 ms com live saindo) = o upload encheu: cai 20%.
    /// É também o que protege o ping do jogo, porque a fila do roteador sobe antes de ele começar a descartar.
    /// Perda só conta como congestionamento acima da perda de fundo + 5 pontos: perda ao acaso (wi-fi, operadora) não é fila e só
    /// pede reenvio; tratar como fila derrubava a taxa para o mínimo (teste com 3% de perda, 2026-11-11).
    /// Fila que não é nossa (só sem live saindo; com live a live tem prioridade e nada disso vale): em 2026-11-20, uma transferência direta de 6,4 GB
    /// com ida e volta base de 15 ms ficou em 0,1 MB/s até ser cancelada. Havia uma fila de 45-75 ms que continuava igual com a taxa
    /// já no piso (outro tráfego, atraso variável), e como a Base ainda via amostras de ~15 ms de vez em quando, a fila calculada nunca caía
    /// abaixo do alvo e a subida nunca acontecia (o defeito clássico do LEDBAT: cede a vez para qualquer outro tráfego). Agora, se a fila
    /// continua acima do alvo há 2 s com a taxa em no máximo 1/4 da que tinha quando ela apareceu (ou no piso), a referência é reancorada
    /// na ida e volta típica recente (<see cref="_baseAjuste"/>, até 200 ms acima da Base, no máximo uma vez a cada 5 s). Depois disso a
    /// taxa sobe de novo e só cai se a NOSSA subida levantar a fila acima do alvo em relação à nova referência (ou pela perda). O
    /// deslocamento zera sozinho quando a fila real fica abaixo do alvo por 1 s (o outro tráfego acabou).
    /// Sem fila: dobra até a primeira queda, depois sobe 10% a cada ida e volta.
    /// </summary>
    void AjustarTaxaComFila(long agora, uint enviados, double medida, double perda, bool recuar, double rttJanela)
    {
        if (recuar) { _baseAjuste = 0; _filaDesdeMs = -1; _realBaixoDesde = -1; } // com live a referência é sempre a Base de verdade
        double filaReal = rttJanela < double.MaxValue && Base < double.MaxValue ? Math.Max(0, rttJanela - Base) : 0;
        if (rttJanela < double.MaxValue) { _janelas[_janelaN++ % _janelas.Length] = (agora, rttJanela); }
        double fila = rttJanela < double.MaxValue && Base < double.MaxValue ? Math.Max(0, rttJanela - Base - _baseAjuste) : 0;
        _lFila = Math.Max(_lFila, fila);
        bool cheia = fila > (recuar ? 10 : 25);
        if (!recuar) AcompanharFilaAlheia(agora, cheia, filaReal, rttJanela);
        bool perdaAlta = enviados >= 30 && perda > _perdaBase + 0.05;
        if (cheia || perdaAlta)
        {
            if (agora - _quedaMs > Rtt + 100) { _taxa = Math.Max(TaxaMin, _taxa * (perda > 0.2 ? 0.5 : 0.8)); _quedaMs = agora; _inicio = false; _lQuedas++; }
        }
        else
        {
            _perdaBase = Math.Min(0.2, _perdaBase * 0.95 + perda * 0.05);
            // a melhor taxa SEM live: com live a taxa já está presa ao teto e a medida não diz quanto o upload aguenta (ver o teto abaixo)
            if (!recuar && perda < _perdaBase + 0.01) _melhor = Math.Max(_melhor, medida);
            // só sobe se está usando a taxa (sem nada para mandar, subir só prepara uma rajada) e uma vez por ida e volta
            if (medida > _taxa * 0.6 && agora - _quedaMs > 300 && agora - _subidaMs >= Math.Max(100, Rtt))
            {
                _taxa = _inicio ? _taxa * 2 : _taxa * 1.1 + 64 * 1024;
                _subidaMs = agora;
            }
        }
        // Com live saindo, quem cede a vez é a fila (alvo de 10 ms, acima); o teto de 40% só vale com uma medida tirada SEM live.
        // Antes o teto era 40% da melhor medida, que com a live já no ar era tirada debaixo do próprio teto: 1 MB/s, depois 0,4, e preso
        // em ~0,3 MB/s com o upload sobrando (fila 1-2 ms, perda 0) — envio de 38 GB entre dois PCs do grupo em 2026-11-17.
        if (recuar)
        {
            _inicio = false; // sem dobrar: sobe 10% por ida e volta, para a fila avisar antes de a live sentir
            if (_melhor > 0) _taxa = Math.Min(_taxa, Math.Max(256 * 1024, 0.4 * _melhor));
        }
    }

    /// <summary>
    /// Detecta a fila que não é nossa e reancora a referência (ver <see cref="AjustarTaxaComFila"/>). Roda a cada avaliação (100 ms ou mais), sem alocar.
    /// A referência nova é a MEDIANA das menores ida e volta das janelas dos últimos 2 s, e não o mínimo: no campo ainda apareciam amostras
    /// de ~15 ms no meio da fila, e o mínimo de 2 s as pegaria e devolveria a mesma referência de antes.
    /// </summary>
    void AcompanharFilaAlheia(long agora, bool cheia, double filaReal, double rttJanela)
    {
        // o deslocamento só zera quando a fila REAL (contra a Base de 1 min) fica abaixo do alvo por 1 s seguido: um mergulho solto não conta
        if (_baseAjuste > 0 && rttJanela < double.MaxValue)
        {
            if (filaReal > 25) _realBaixoDesde = -1;
            else if (_realBaixoDesde < 0) _realBaixoDesde = agora;
            else if (agora - _realBaixoDesde >= 1000) { Log.Info($"[filme] envio: a fila de outro tráfego passou (referência +{_baseAjuste:N0} ms volta a zero)."); _baseAjuste = 0; _realBaixoDesde = -1; _filaDesdeMs = -1; }
        }
        if (cheia)
        {
            // o episódio de fila nasce na primeira janela cheia e guarda a taxa daquela hora; passados 10 s sem reancorar ele recomeça,
            // para a taxa guardada não ficar velha (um serrote normal com a taxa estável não pode parecer "caiu para 1/4")
            if (_filaDesdeMs < 0 || agora - _filaDesdeMs > 10_000) { _filaDesdeMs = agora; _taxaNaFila = _taxa; }
            _ultimaCheiaMs = agora;
            bool taxaBaixa = _taxa <= Math.Max(TaxaMin, _taxaNaFila / 4) * 1.001;
            if (agora - _filaDesdeMs >= 2000 && taxaBaixa && agora - _reancoradoMs >= 5000) Reancorar(agora);
        }
        else if (_filaDesdeMs >= 0 && agora - _ultimaCheiaMs > 1000) _filaDesdeMs = -1;
    }

    void Reancorar(long agora)
    {
        Span<double> v = stackalloc double[_janelas.Length];
        int n = 0;
        foreach (var (ms, r) in _janelas) if (ms > 0 && agora - ms <= 2000) v[n++] = r;
        _filaDesdeMs = -1; _reancoradoMs = agora;
        if (n < 3 || Base >= double.MaxValue) return;
        v = v[..n]; v.Sort();
        double mediana = v[n / 2];
        double novo = Math.Min(200, Math.Max(0, mediana - Base));
        if (novo <= _baseAjuste) return;
        _baseAjuste = novo; _realBaixoDesde = -1;
        // sem voltar ao modo "início" (_inicio já caiu na primeira queda): dobrar a cada ida e volta, com a ida e volta inflada pela fila alheia,
        // sairia da taxa do piso direto para cima da capacidade; a subida de 10% por ida e volta basta (do piso a 3 MB/s em ~3 s)
        Log.Info($"[filme] envio: fila de outro tráfego (continua em {mediana - Base:N0} ms com a taxa em {_taxa / 1e6:N2} MB/s); referência +{_baseAjuste:N0} ms.");
    }

    /// <summary>
    /// Quanto da taxa o balde guarda: 4 ms normalmente, mas se o sistema demorou para acordar o laço, o balde cobre esse atraso (até 20 ms) em vez de jogar fora o
    /// que a taxa já deu direito. Sem isso a vazão virava "balde ÷ intervalo do relógio" (~1 MB/s com a janela minimizada), qualquer que fosse a taxa.
    /// </summary>
    static double BucketSeconds(double gapSeconds) => Math.Clamp(gapSeconds * 1.5, 0.004, 0.02);

    void Laco()
    {
        Pacote.timeBeginPeriod(1);
        using var boost = TransferBoost.Request(); // com a janela minimizada o Windows acordaria este laço só a cada ~15,6 ms (ver TransferBoost)
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        var envioEm = new Dictionary<uint, long[]>();
        var cache = new Dictionary<uint, byte[]>();
        try
        {
            var claro = new byte[6 + Pacote.Frag];
            var pkt = new byte[Pacote.Max];
            double fichas = 0;
            long antes = _sw.ElapsedTicks;
            (uint, byte[]?)[]? visto = null;
            while (!_fim)
            {
                long ticks = _sw.ElapsedTicks, agora = _sw.ElapsedMilliseconds;
                double taxa = _taxa;
                fichas = Math.Min(fichas + (ticks - antes) * taxa / Stopwatch.Frequency, Math.Max(16 * 1024, taxa * BucketSeconds((ticks - antes) / (double)Stopwatch.Frequency)));
                antes = ticks;
                var pedidos = _pedidos;
                long estado = Volatile.Read(ref _estadoMs);
                // sem notícia de quem baixa há 2 s (fechou o app, a ligação caiu): para de mandar
                if (estado < 0 || agora - estado > 2000 || pedidos.Length == 0 || _t.State != TunnelState.Connected) { fichas = 0; Thread.Sleep(5); continue; }
                if (!ReferenceEquals(pedidos, visto))
                {
                    foreach (var k in envioEm.Keys.Where(k => !pedidos.Any(p => p.Item1 == k)).ToList()) envioEm.Remove(k);
                    foreach (var k in cache.Keys.Where(k => !pedidos.Any(p => p.Item1 == k)).ToList()) { ArrayPool<byte>.Shared.Return(cache[k]); cache.Remove(k); }
                    visto = pedidos;
                }
                long rto = (long)(Rtt * 1.5) + 40;
                bool mandou = false;
                foreach (var (peca, tem) in pedidos)
                {
                    if (fichas < Pacote.Frag) break;
                    int tam = _tamanho(peca);
                    if (tam <= 0) continue;
                    int nf = Pacote.Frags(tam);
                    if (!envioEm.TryGetValue(peca, out var env)) envioEm[peca] = env = new long[nf];
                    byte[]? dados = null;
                    for (int f = 0; f < nf && fichas >= Pacote.Frag; f++)
                    {
                        if (tem != null && (f >> 3) < tem.Length && (tem[f >> 3] & (1 << (f & 7))) != 0) continue;
                        if (env[f] != 0 && agora + 1 - env[f] < rto) continue;
                        if (env[f] != 0) Interlocked.Increment(ref _reenvios);
                        if (dados == null && !cache.TryGetValue(peca, out dados))
                        {
                            dados = ArrayPool<byte>.Shared.Rent(Pacote.Pedaco);
                            _ler(peca, dados, tam);
                            cache[peca] = dados;
                        }
                        int len = Math.Min(Pacote.Frag, tam - f * Pacote.Frag);
                        BinaryPrimitives.WriteUInt32LittleEndian(claro, peca);
                        BinaryPrimitives.WriteUInt16LittleEndian(claro.AsSpan(4), (ushort)f);
                        Buffer.BlockCopy(dados, f * Pacote.Frag, claro, 6, len);
                        uint seq = ++_seq;
                        if (seq == 0) seq = ++_seq; // 0 é "sem eco"
                        int n = Pacote.Selar(_sela, Pacote.KDados, _sal, seq, claro.AsSpan(0, 6 + len), pkt);
                        Volatile.Write(ref _saiuEm[seq & (_saiuEm.Length - 1)], Stopwatch.GetTimestamp());
                        _t.SendArquivo(pkt.AsSpan(0, n));
                        env[f] = agora + 1;
                        fichas -= n;
                        mandou = true;
                        Interlocked.Add(ref _bytes, n);
                    }
                }
                if (!mandou || fichas < Pacote.Frag) Thread.Sleep(1);
            }
        }
        catch (Exception e) when (!_fim) { Log.Info($"[filme] o envio do arquivo parou: {e.Message}"); }
        finally
        {
            Pacote.timeEndPeriod(1);
            foreach (var b in cache.Values) ArrayPool<byte>.Shared.Return(b);
        }
    }

    public void Dispose()
    {
        _fim = true;
        if (_t.Arquivo == _entrada) _t.Arquivo = null;
        _thread.Join(500);
        _sela.Dispose(); _abre.Dispose();
    }
}

/// <summary>Quem baixa: pede os pedaços numa janela, junta os pacotes e entrega cada pedaço inteiro em <see cref="Prontos"/>.</summary>
public sealed class Receptor : IDisposable
{
    /// <summary>Pedaços em curso (10 MB): os mapas de bits de todos cabem num pacote de estado.</summary>
    const int Janela = 10;

    sealed class Peca { public uint Id; public byte[] Buf = null!; public byte[] Tem = null!; public int Nf, Faltam, Tam; }

    readonly Tunnel _t;
    readonly AesGcm _sela, _abre;
    readonly uint _sal = Pacote.Sal();
    uint _seq;
    readonly Func<uint, int> _tamanho;
    readonly object _gate = new();
    readonly SortedDictionary<uint, Peca> _emCurso = new();
    readonly SortedSet<uint> _fila = new();
    int _entregues;
    uint _rx, _max, _eco;
    long _ecoEm;
    bool _temMax;
    readonly byte[] _claro = new byte[Pacote.Max];
    readonly Channel<(uint Peca, byte[] Buf, int Tam)> _prontos = Channel.CreateUnbounded<(uint, byte[], int)>();
    readonly Thread _thread;
    readonly Action<ReadOnlySpan<byte>> _entrada;
    volatile bool _fim;
    long _bytes;

    public ChannelReader<(uint Peca, byte[] Buf, int Tam)> Prontos => _prontos.Reader;
    public long BytesRecebidos => Interlocked.Read(ref _bytes);

    public Receptor(Tunnel t, Func<uint, int> tamanho)
    {
        _t = t; _tamanho = tamanho;
        _sela = new AesGcm(t.ChaveArquivo, 16);
        _abre = new AesGcm(t.ChaveArquivo, 16);
        t.Arquivo = _entrada = Simulador.Embrulhar(Receber, () => _fim);
        _thread = new Thread(Laco) { IsBackground = true, Name = "filme-recebe" };
        _thread.Start();
    }

    public void Pedir(IEnumerable<uint> pecas) { lock (_gate) foreach (var p in pecas) _fila.Add(p); }

    /// <summary>O consumidor terminou com o pedaço: devolve o buffer e abre espaço na janela.</summary>
    public void Liberar(byte[] buf) { ArrayPool<byte>.Shared.Return(buf); lock (_gate) _entregues--; }

    void Receber(ReadOnlySpan<byte> pkt) // thread do túnel
    {
        if (!Pacote.Abrir(_abre, pkt, _claro, out byte tipo, out uint seq, out int n) || tipo != Pacote.KDados || n < 6) return;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(_claro);
        int f = BinaryPrimitives.ReadUInt16LittleEndian(_claro.AsSpan(4)), len = n - 6;
        Peca? pronta = null;
        lock (_gate)
        {
            _rx++;
            if (!_temMax || (int)(seq - _max) > 0) { _max = seq; _temMax = true; }
            _eco = seq; _ecoEm = Stopwatch.GetTimestamp();
            if (!_emCurso.TryGetValue(id, out var p) || f >= p.Nf || (p.Tem[f >> 3] & (1 << (f & 7))) != 0) return;
            if (len != Math.Min(Pacote.Frag, p.Tam - f * Pacote.Frag)) return;
            Buffer.BlockCopy(_claro, 6, p.Buf, f * Pacote.Frag, len);
            p.Tem[f >> 3] |= (byte)(1 << (f & 7));
            _bytes += len;
            if (--p.Faltam == 0) { _emCurso.Remove(id); _entregues++; pronta = p; }
        }
        if (pronta != null) _prontos.Writer.TryWrite((pronta.Id, pronta.Buf, pronta.Tam));
    }

    void Laco()
    {
        Pacote.timeBeginPeriod(1); // o pedido de pedaços dorme 20 ms: com o relógio em 15,6 ms (janela minimizada) viraria ~31 ms e o envio ficaria sem notícia
        using var boost = TransferBoost.Request();
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        var claro = new byte[Pacote.Max - 25];
        var pkt = new byte[Pacote.Max];
        try
        {
            while (!_fim)
            {
                int n;
                lock (_gate)
                {
                    while (_emCurso.Count + _entregues < Janela && _fila.Count > 0)
                    {
                        uint id = _fila.Min;
                        _fila.Remove(id);
                        if (_emCurso.ContainsKey(id)) continue;
                        int tam = _tamanho(id), nf = Pacote.Frags(tam);
                        _emCurso[id] = new Peca { Id = id, Tam = tam, Nf = nf, Faltam = nf, Tem = new byte[(nf + 7) / 8], Buf = ArrayPool<byte>.Shared.Rent(Pacote.Pedaco) };
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(claro, _rx);
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(4), _max);
                    // eco: o último seq que chegou e há quanto tempo (µs), para quem manda medir a ida e volta sem a espera daqui
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(8), _eco);
                    BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(12), _eco == 0 ? 0u : (uint)Math.Min(uint.MaxValue, (Stopwatch.GetTimestamp() - _ecoEm) * 1_000_000 / Stopwatch.Frequency));
                    int o = 17, q = 0;
                    foreach (var p in _emCurso.Values)
                    {
                        int len = p.Faltam < p.Nf ? p.Tem.Length : 0;
                        if (o + 6 + len > claro.Length || q == 255) break;
                        BinaryPrimitives.WriteUInt32LittleEndian(claro.AsSpan(o), p.Id);
                        BinaryPrimitives.WriteUInt16LittleEndian(claro.AsSpan(o + 4), (ushort)len);
                        o += 6;
                        if (len > 0) { p.Tem.CopyTo(claro, o); o += len; }
                        q++;
                    }
                    claro[16] = (byte)q;
                    n = o;
                }
                int m = Pacote.Selar(_sela, Pacote.KEstado, _sal, ++_seq, claro.AsSpan(0, n), pkt);
                _t.SendArquivo(pkt.AsSpan(0, m));
                Thread.Sleep(20);
            }
        }
        catch (Exception e) when (!_fim) { Log.Info($"[filme] o pedido de pedaços parou: {e.Message}"); }
        finally { Pacote.timeEndPeriod(1); }
    }

    public void Dispose()
    {
        _fim = true;
        if (_t.Arquivo == _entrada) _t.Arquivo = null;
        _thread.Join(500);
        lock (_gate) { foreach (var p in _emCurso.Values) ArrayPool<byte>.Shared.Return(p.Buf); _emCurso.Clear(); }
        _sela.Dispose(); _abre.Dispose();
    }
}
