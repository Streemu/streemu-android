using System.Collections.Concurrent;
using Android.Media;

namespace Streemu.Proto;

/// <summary>
/// Áudio da live: Opus (o que o app Windows manda: 48 kHz, estéreo, pacotes de 10 ms) -> MediaCodec -> AudioTrack em baixa latência.
/// Como o vídeo, toca assim que chega, sem esperar o PTS: quem segura o ritmo é o SRT. Para o som não ir ficando para trás (o relógio do celular e o
/// do PC andam um pouco diferente, e um soluço da rede enche o buffer), o que passar de <see cref="MaxNaFilaMs"/> na fila do AudioTrack é jogado fora.
/// </summary>
sealed class Som : IDisposable
{
    const int Taxa = 48000;
    /// <summary>Quanto de som pode esperar na fila do AudioTrack. Acima disso, o pedaço decodificado é descartado (um estalo curto em vez de atraso que só cresce).</summary>
    const int MaxNaFilaMs = 150;
    /// <summary>Onde a fila deve ficar: folga para o jitter da rede sem atrasar o som em relação ao vídeo.</summary>
    const int AlvoNaFilaMs = 50;
    long _janelaDesde;
    double _minimoJanela = double.MaxValue;
    /// <summary>O menor valor da fila na última janela de 2 s: a folga de verdade, o que o som atrasa a mais que o necessário.</summary>
    public double MinimoNaFilaMs { get; private set; }

    readonly int _canais;
    readonly BlockingCollection<byte[]> _fila = new(new ConcurrentQueue<byte[]>(), 200);
    readonly Thread _thread;
    volatile bool _parar;
    long _quadrosEscritos;
    AudioTrack? _track;

    /// <summary>Vezes que o AudioTrack ficou sem som para tocar (cada uma é um estalo).</summary>
    public int Faltas => _track?.UnderrunCount ?? 0;
    /// <summary>PES de áudio recebidos e o maior intervalo entre dois seguidos na última janela, em ms (diagnóstico das rajadas).</summary>
    public long PesRecebidos;
    public double MaiorIntervaloMs { get; private set; }
    long _ultimoPes, _janelaIntervalo; double _maiorJanela;
    public long Pacotes, Tocados, DescartadosAtraso, DescartadosAjuste, DescartadosFila, Erros;
    /// <summary>Som esperando na fila do AudioTrack agora, em ms.</summary>
    public double NaFilaMs { get; private set; }
    public string Nome { get; private set; } = "";
    volatile bool _mudo;
    /// <summary>Sem som: o AudioTrack continua tocando (no volume zero), para o som voltar sincronizado ao desligar.</summary>
    public bool Mudo { get => _mudo; set { _mudo = value; try { _track?.SetVolume(value ? 0f : 1f); } catch { } } }

    public Som(int canais)
    {
        _canais = canais;
        _thread = new Thread(Laco) { IsBackground = true, Name = "som" };
        _thread.Start();
    }

    /// <summary>Um PES de Opus do TS: um ou mais pacotes, cada um atrás do cabeçalho de controle (ETSI TS 102 366 / o muxer do ffmpeg).</summary>
    public void Entregar(byte[] pes, long pts90k)
    {
        long agoraUs = Streemu.Net.Tunnel.NowUs;
        if (_ultimoPes != 0) _maiorJanela = Math.Max(_maiorJanela, (agoraUs - _ultimoPes) / 1000.0);
        _ultimoPes = agoraUs;
        if (agoraUs - _janelaIntervalo > 2_000_000) { MaiorIntervaloMs = _maiorJanela; _maiorJanela = 0; _janelaIntervalo = agoraUs; }
        PesRecebidos++;
        int i = 0;
        while (i + 2 <= pes.Length)
        {
            if (pes[i] != 0x7F || (pes[i + 1] & 0xE0) != 0xE0) { Interlocked.Increment(ref Erros); return; }
            byte flags = pes[i + 1];
            i += 2;
            int tamanho = 0;
            while (i < pes.Length) { byte b = pes[i++]; tamanho += b; if (b != 0xFF) break; }
            if ((flags & 0x10) != 0) i += 2;   // start_trim
            if ((flags & 0x08) != 0) i += 2;   // end_trim
            if ((flags & 0x04) != 0 && i < pes.Length) i += 1 + pes[i];   // control_extension
            if (tamanho <= 0 || i + tamanho > pes.Length) { Interlocked.Increment(ref Erros); return; }
            Interlocked.Increment(ref Pacotes);
            if (!_fila.TryAdd(pes[i..(i + tamanho)])) Interlocked.Increment(ref DescartadosFila);
            i += tamanho;
        }
    }

    static byte[] OpusHead(int canais)
    {
        var h = new byte[19];
        "OpusHead"u8.CopyTo(h);
        h[8] = 1; h[9] = (byte)canais;
        BitConverter.TryWriteBytes(h.AsSpan(10), (ushort)312);   // pré-salto: só corta o começo, não muda o atraso
        BitConverter.TryWriteBytes(h.AsSpan(12), (uint)Taxa);
        return h;
    }

    void Laco()
    {
        MediaCodec? codec = null;
        try
        {
            var fmt = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioOpus, Taxa, _canais);
            fmt.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(OpusHead(_canais)));
            fmt.SetByteBuffer("csd-1", Java.Nio.ByteBuffer.Wrap(BitConverter.GetBytes(312L * 1_000_000_000 / Taxa)));
            fmt.SetByteBuffer("csd-2", Java.Nio.ByteBuffer.Wrap(BitConverter.GetBytes(80_000_000L)));
            codec = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeAudioOpus);
            Nome = codec.Name;
            codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            var mascara = _canais == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            int minimo = AudioTrack.GetMinBufferSize(Taxa, mascara, Encoding.Pcm16bit);
            _track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Movie)!.Build()!)!
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.Pcm16bit)!.SetSampleRate(Taxa)!.SetChannelMask(mascara)!.Build()!)!
                .SetBufferSizeInBytes(Math.Max(minimo, Taxa * 2 * _canais / 5))!   // 200 ms de espaço; o limite de verdade é o MaxNaFilaMs
                .SetPerformanceMode(AudioTrackPerformanceMode.LowLatency)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;
            _track.SetVolume(_mudo ? 0f : 1f);
            _track.Play();
            Streemu.Util.Log.Info($"Som: {Nome}, {_canais} canal(is), buffer mínimo {minimo} bytes.");

            var info = new MediaCodec.BufferInfo();
            long ptsUs = 0;
            while (!_parar)
            {
                if (_fila.TryTake(out var pacote, 10))
                {
                    int ib = codec.DequeueInputBuffer(10_000);
                    if (ib >= 0)
                    {
                        var buf = codec.GetInputBuffer(ib)!;
                        buf.Clear();
                        buf.Put(pacote);
                        codec.QueueInputBuffer(ib, 0, pacote.Length, ptsUs, MediaCodecBufferFlags.None);
                        ptsUs += 10_000;
                    }
                }
                int ob;
                while ((ob = codec.DequeueOutputBuffer(info, 0)) >= 0)
                {
                    if (info.Size > 0)
                    {
                        var saida = codec.GetOutputBuffer(ob)!;
                        var pcm = new byte[info.Size];
                        saida.Position(info.Offset);
                        saida.Get(pcm);
                        Tocar(pcm);
                    }
                    codec.ReleaseOutputBuffer(ob, false);
                }
            }
        }
        catch (Exception e) { Streemu.Util.Log.Info("AVISO: " + "Som parou: " + e.Message); }
        finally
        {
            try { codec?.Stop(); codec?.Release(); } catch { }
            try { _track?.Stop(); _track?.Release(); } catch { }
        }
    }

    void Tocar(byte[] pcm)
    {
        var t = _track!;
        int bytesPorQuadro = 2 * _canais;
        long naFila = _quadrosEscritos - (t.PlaybackHeadPosition & 0xFFFFFFFFL);
        NaFilaMs = naFila * 1000.0 / Taxa;
        if (NaFilaMs > MaxNaFilaMs) { Interlocked.Increment(ref DescartadosAtraso); return; }
        // A cada 2 s: se até o MENOR valor da fila na janela ficou acima do alvo, sobra folga parada, e um pedaço é cortado. Sem isso, a rajada do
        // começo (ou um soluço da rede) deixa a fila cheia para sempre, porque o som chega no mesmo ritmo em que toca, e o som fica atrás do vídeo
        // (medido: ~80 ms). O valor instantâneo não serve: o som chega em rajadas e a fila oscila ~40 ms dentro da mesma janela.
        long agora = Environment.TickCount64;
        _minimoJanela = Math.Min(_minimoJanela, NaFilaMs);
        if (_janelaDesde == 0) _janelaDesde = agora;
        else if (agora - _janelaDesde >= 2000)
        {
            MinimoNaFilaMs = _minimoJanela;
            bool cortar = _minimoJanela > AlvoNaFilaMs;
            _janelaDesde = agora; _minimoJanela = double.MaxValue;
            if (cortar) { Interlocked.Increment(ref DescartadosAjuste); return; }
        }
        int n = t.Write(pcm, 0, pcm.Length, WriteMode.NonBlocking);
        if (n > 0) { _quadrosEscritos += n / bytesPorQuadro; Interlocked.Increment(ref Tocados); }
    }

    public void Dispose()
    {
        _parar = true;
        _thread.Join(500);
    }
}
