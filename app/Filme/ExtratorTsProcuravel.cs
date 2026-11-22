using AndroidX.Media3.Common;
using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.TS;

namespace Streemu.Proto.Filme;

/// <summary>
/// O extrator de TS do Media3 numa fonte sem tamanho (o cache ao vivo) diz que não dá para procurar, e o ExoPlayer então trata qualquer seek como "volte ao zero" (ProgressiveMediaPeriod:
/// "treat all seeks into non-seekable media as being to t=0"): o <c>SetMediaSource(fonte, inicio)</c> do <see cref="PlayerStream"/> começava sempre no quadro-chave, não no ponto pedido
/// (teste no S26, 22/11). Este embrulho deixa o mapa de procura <b>procurável</b> com um único ponto, o começo da fonte: procurar para qualquer tempo relê a fonte do começo (que já é
/// o quadro-chave logo antes do ponto, ver <see cref="PlayerStream"/>) e o ExoPlayer marca os quadros antes do ponto como só-decodificar, que é o seek exato dele.
/// O <c>seek</c> do extrator de baixo recebe tempo 0: o carimbo do primeiro quadro-chave continua sendo o 0 da linha do tempo do player (a base do PlayerStream).
/// </summary>
public sealed class ExtratorTsProcuravel : Java.Lang.Object, IExtractor
{
    readonly IExtractor _tras;

    public ExtratorTsProcuravel(IExtractor tras) { _tras = tras; }

    public bool Sniff(IExtractorInput? input) => _tras.Sniff(input!);

    public void Init(IExtractorOutput? output) => _tras.Init(new SaidaProcuravel(output!));

    public int Read(IExtractorInput? input, PositionHolder? seekPosition) => _tras.Read(input!, seekPosition!);

    public void Seek(long position, long timeUs) => _tras.Seek(0, 0);

    public void Release() => _tras.Release();

    public IExtractor? UnderlyingImplementation => _tras.UnderlyingImplementation;

    sealed class SaidaProcuravel : Java.Lang.Object, IExtractorOutput
    {
        readonly IExtractorOutput _s;
        public SaidaProcuravel(IExtractorOutput s) { _s = s; }
        public ITrackOutput? Track(int id, int type) => _s.Track(id, type);
        public void EndTracks() => _s.EndTracks();
        public void SeekMap(ISeekMap? seekMap) => _s.SeekMap(new MapaProcuravel());
    }

    sealed class MapaProcuravel : Java.Lang.Object, ISeekMap
    {
        public bool IsSeekable => true;
        public long DurationUs => C.TimeUnset;
        public SeekMapSeekPoints? GetSeekPoints(long timeUs) => new SeekMapSeekPoints(new SeekPoint(timeUs, 0));
    }
}

/// <summary>As fontes do ExoPlayer para o cache: o extrator de TS procurável (e só ele).</summary>
public sealed class FabricaExtratoresTs : Java.Lang.Object, IExtractorsFactory
{
    public IExtractor[] CreateExtractors()
    {
        var padrao = new DefaultExtractorsFactory();
        padrao.SetTsExtractorFlags(DefaultTsPayloadReaderFactory.FlagAllowNonIdrKeyframes | DefaultTsPayloadReaderFactory.FlagDetectAccessUnits);
        var ts = padrao.CreateExtractors().OfType<TsExtractor>().First();
        return new IExtractor[] { new ExtratorTsProcuravel(ts) };
    }

    public IExtractor[] CreateExtractors(Android.Net.Uri? uri, IDictionary<string, IList<string>>? responseHeaders) => CreateExtractors();
}
