#pragma warning disable CS0618   // MediaExtractorCompat vem marcado como obsoleto no binding ("obsoleted in this android platform"), mas é o extrator que enxerga o E-AC-3 de um MKV (o MediaExtractor do Android não lista); o aviso é esperado
using Android.Content;
using Android.Media;
using AndroidX.Media3.ExoPlayer;
using Java.Nio;
using Streemu.Filme;
using Streemu.Proto.Transmissao;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// A fonte do envio em cópia direta no celular (spec 028, 5.2): o MediaExtractor lê as amostras do arquivo do filme, o <see cref="FilmeTs"/> as põe no formato do mpegts (Annex B com SPS/PPS
/// em cada quadro-chave, AAC com ADTS, DTS pela ordem de decodificação) e o <see cref="TsMux"/> monta o fluxo no tempo do filme, sem recomprimir nada. Uma instância por época.
/// O extrator entrega as amostras na ordem do arquivo (vídeo e áudio intercalados): é o que o mux espera.
/// </summary>
sealed class FonteExtrator : IFonteTs
{
    const int AmostrasParaProfundidade = 150, BufferInicial = 2 << 20;
    const int SeekToPreviousSync = 0;   // MediaExtractor.SEEK_TO_PREVIOUS_SYNC: o extrator do Media3 pede o modo como inteiro
    /// <summary>A profundidade de reordenação dos quadros B é do arquivo: medida na 1ª época, as outras não repetem a leitura e o segundo seek (campo 22/11: abrir levou até 6,4 s com o player tocando junto).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Profundidades = new();

    readonly Context _contexto;
    readonly Android.Net.Uri _arquivo;
    readonly FaixasDoFilme _f;
    readonly Queue<BlocoTs> _prontos = new();
    MediaExtractorCompat? _ex;
    Android.OS.ParcelFileDescriptor? _descritor;   // arquivo do seletor (content://): vive até o Dispose
    TsMux? _mux;
    FilmeTs.ConversorVideo? _conversor;
    FilmeTs.OrdemDts? _ordem;
    ByteBuffer _buffer = ByteBuffer.AllocateDirect(BufferInicial)!;
    byte[] _bytes = new byte[BufferInicial];
    bool _comDts;
    double _ptsAtualS;
    bool _videoAtual;

    public FonteExtrator(Context contexto, Android.Net.Uri arquivo, FaixasDoFilme faixas)
    {
        _contexto = contexto; _arquivo = arquivo; _f = faixas;
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) Crescer(8 << 20);   // sem o tamanho da amostra antes de ler (Android 8): um buffer que cabe um IDR de 4K
    }

    public void Abrir(double posS)
    {
        AbrirDados();
        _ex!.SelectTrack(_f.Video);
        if (_f.TrilhaAudio >= 0) _ex.SelectTrack(_f.TrilhaAudio);
        long pos = (long)(Math.Max(0, posS) * 1e6);
        _ex.SeekTo(pos, SeekToPreviousSync);
        if (_ex.SampleTrackIndex < 0) throw new InvalidOperationException($"o extrator não achou nenhuma amostra depois do seek para {posS:0.#} s ({_arquivo.Scheme}); esperado ao menos uma amostra de vídeo");
        int profundidade = Profundidades.GetOrAdd(_arquivo.ToString() ?? "", _ => ProfundidadeDeReordenacao(pos));
        _comDts = profundidade > 0;
        _ordem = new FilmeTs.OrdemDts(profundidade, _f.QuadroUs);
        _conversor = new FilmeTs.ConversorVideo(_f.Hevc, _f.ParametrosVideo);
        _mux = new TsMux(new OpcoesTs(_f.Hevc ? CodecVideoTs.Hevc : CodecVideoTs.H264, _f.CodecAudio, _f.Canais, _f.AscAac, Absoluto: true));
        _mux.Saida += (dados, chave) => _prontos.Enqueue(new BlocoTs(dados, chave, _ptsAtualS, _videoAtual));
        Logcat.Info("Streemu", $"FILME fonte aberta em {posS:0.#} s ({(_f.Hevc ? "hevc" : "h264")}, reordenação {profundidade}).");
    }

    /// <summary>Só a bancada de teste: abre também os arquivos comuns pelo descritor, para exercitar no aparelho o caminho do seletor sem o seletor.</summary>
    public static bool ForcarDescritor { get; set; }

    /// <summary>
    /// Campo de 22/11: com o arquivo do seletor (<c>content://</c>) o extrator do Media3 abria e não entregava nenhuma amostra (a bancada, com caminho de arquivo, lia bem), e abrir por
    /// <c>/proc/self/fd/N</c> deu EACCES (o Android não deixa abrir por caminho o descritor de outro app). Aqui o descritor vai direto ao extrator (<c>setDataSource(FileDescriptor, 0, tamanho)</c>).
    /// </summary>
    void AbrirDados()
    {
        _ex = new MediaExtractorCompat(_contexto);
        if (_arquivo.Scheme != "content" && !ForcarDescritor) { _ex.SetDataSource(_contexto, _arquivo, null); return; }
        _descritor = _contexto.ContentResolver!.OpenFileDescriptor(_arquivo, "r") ?? throw new IOException($"o arquivo {_arquivo} não abriu; esperado um descritor de leitura");
        _ex.SetDataSource(_descritor.FileDescriptor!, 0, _descritor.StatSize);
    }

    /// <summary>
    /// O MediaExtractor só dá o PTS; o DTS vem da profundidade de reordenação dos quadros B (<see cref="FilmeTs.OrdemDts"/>). Mede nas primeiras amostras de vídeo do ponto e volta ao ponto:
    /// o seek para o mesmo lugar cai no mesmo quadro-chave.
    /// </summary>
    int ProfundidadeDeReordenacao(long posUs)
    {
        var pts = new List<long>(AmostrasParaProfundidade);
        while (pts.Count < AmostrasParaProfundidade && _ex!.SampleTrackIndex >= 0)
        {
            if (_ex.SampleTrackIndex == _f.Video) pts.Add(_ex.SampleTime);
            if (!_ex.Advance()) break;
        }
        _ex!.SeekTo(posUs, SeekToPreviousSync);
        return FilmeTs.OrdemDts.Profundidade(pts);
    }

    public BlocoTs? Proximo()
    {
        while (_prontos.Count == 0)
            if (!LerAmostra()) return null;
        return _prontos.Dequeue();
    }

    /// <summary>Uma amostra do arquivo para o mux. Falso no fim do arquivo.</summary>
    bool LerAmostra()
    {
        int faixa = _ex!.SampleTrackIndex;
        if (faixa < 0) return false;
        long ptsUs = _ex.SampleTime;
        bool chave = ((int)_ex.SampleFlags & 1) != 0;   // SAMPLE_FLAG_SYNC
        int n = Ler();
        if (n < 0) return false;
        _ex.Advance();
        if (faixa == _f.Video) MandarVideo(n, ptsUs, chave);
        else if (faixa == _f.TrilhaAudio) MandarAudio(n, ptsUs);
        return true;
    }

    /// <summary>Lê a amostra atual para <c>_bytes</c>. Antes, cresce o buffer se ela não cabe (um IDR de 4K passa de 2 MB; o tamanho só se sabe antes de ler no Android 9+).</summary>
    int Ler()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            long tamanho = _ex!.SampleSize;
            if (tamanho > _buffer.Capacity()) Crescer((int)Math.Min(int.MaxValue / 2, tamanho * 2));
        }
        int n = _ex!.ReadSampleData(_buffer, 0);
        if (n < 0) return n;
        if (n >= _buffer.Capacity()) throw new InvalidOperationException($"amostra de {n} bytes encheu o buffer de {_buffer.Capacity()}; esperado um buffer maior que a amostra");
        _buffer.Position(0);
        _buffer.Get(_bytes, 0, n);
        return n;
    }

    void Crescer(int capacidade)
    {
        _buffer = ByteBuffer.AllocateDirect(capacidade)!;
        _bytes = new byte[capacidade];
    }

    void MandarVideo(int n, long ptsUs, bool chave)
    {
        var annexB = _conversor!.Converter(_bytes.AsSpan(0, n), chave);
        _ptsAtualS = ptsUs / 1e6; _videoAtual = true;
        _mux!.Video(annexB, ptsUs, _ordem!.Proximo(ptsUs), chave, _comDts);
    }

    void MandarAudio(int n, long ptsUs)
    {
        _ptsAtualS = ptsUs / 1e6; _videoAtual = false;
        _mux!.Audio(_bytes.AsSpan(0, n), ptsUs);
    }

    public void Dispose()
    {
        try { _ex?.Release(); } catch (Exception e) { Logcat.Warn("Streemu", "FILME fonte: " + e.Message); }
        _ex = null;
        try { _descritor?.Close(); } catch (Exception e) { Logcat.Warn("Streemu", "FILME descritor da fonte: " + e.Message); }
        _descritor = null;
    }
}
