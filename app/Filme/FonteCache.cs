using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.TS;

namespace Streemu.Proto.Filme;

/// <summary>
/// DataSource do Media3 que lê do <see cref="CacheTs"/> (spec 028, 5.1). Cada fonte começa num ponto do cache (<c>inicio</c>, o começo de um quadro-chave do índice) e antes dele
/// entrega a PAT e a PMT guardadas, para o extrator do ExoPlayer saber os fluxos mesmo começando do meio. Sem tamanho conhecido (o fluxo é ao vivo): o ExoPlayer trata como
/// não procurável e, se precisar voltar, abre a fonte de novo, que recomeça do mesmo ponto.
/// </summary>
public sealed class FonteCache : BaseDataSource
{
    readonly CacheTs _cache;
    readonly long _inicio;
    readonly byte[] _prefixo;
    long _lido;
    volatile bool _fechada;
    bool _aberta;
    readonly Android.Net.Uri _uri = Android.Net.Uri.Parse("streemu-cache://filme.ts")!;

    public FonteCache(CacheTs cache, long inicio, byte[]? patPmt) : base(false)
    {
        _cache = cache; _inicio = inicio; _prefixo = patPmt ?? Array.Empty<byte>();
    }

    public override long Open(DataSpec dataSpec)
    {
        TransferInitializing(dataSpec);
        _fechada = false; _lido = dataSpec.Position;
        _aberta = true;
        TransferStarted(dataSpec);
        return C.LengthUnset;
    }

    public override int Read(byte[] buffer, int offset, int length)
    {
        if (length == 0) return 0;
        int n;
        if (_lido < _prefixo.Length)
        {
            n = (int)Math.Min(length, _prefixo.Length - _lido);
            Buffer.BlockCopy(_prefixo, (int)_lido, buffer, offset, n);
        }
        else
        {
            n = _cache.Ler(_inicio + (_lido - _prefixo.Length), buffer, offset, length, () => _fechada);
            if (n <= 0) return C.ResultEndOfInput;
        }
        _lido += n;
        BytesTransferred(n);
        return n;
    }

    public override Android.Net.Uri? Uri => _uri;

    public override void Close()
    {
        _fechada = true;
        _cache.Acordar();
        if (_aberta) { _aberta = false; TransferEnded(); }
    }
}

/// <summary>Quem cria as <see cref="FonteCache"/> (uma por leitura que o ExoPlayer abre) e a fonte de mídia que o ExoPlayer toca.</summary>
public sealed class FabricaFonteCache : Java.Lang.Object, IDataSourceFactory
{
    readonly CacheTs _cache;
    readonly long _inicio;
    readonly byte[]? _patPmt;

    public FabricaFonteCache(CacheTs cache, long inicio, byte[]? patPmt) { _cache = cache; _inicio = inicio; _patPmt = patPmt; }

    public IDataSource CreateDataSource() => new FonteCache(_cache, _inicio, _patPmt);

    /// <summary>A fonte de mídia (ProgressiveMediaSource com o extrator de TS) que começa em <paramref name="inicio"/> do cache.</summary>
    public static IMediaSource Criar(CacheTs cache, long inicio)
    {
        var fabrica = new FabricaFonteCache(cache, inicio, cache.PatPmt());
        return new ProgressiveMediaSource.Factory(fabrica, new FabricaExtratoresTs()).CreateMediaSource(MediaItem.FromUri("streemu-cache://filme.ts")!);
    }
}
