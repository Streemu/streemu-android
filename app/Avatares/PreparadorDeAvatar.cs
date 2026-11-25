using Android.Content;
using Android.Graphics;
using Android.Media;

namespace Streemu.Proto.Avatares;

/// <summary>
/// De uma imagem escolhida na galeria ao <see cref="AvatarPronto"/>: recorta o centro em quadrado e reduz para 64 px. Parada vira uma miniatura PNG; um GIF
/// animado, de qualquer tamanho, vira a <see cref="TiraDeQuadros"/>. Pesado: chamar fora da thread da tela.
/// Limites do app Windows: arquivo até 25 MB, foto até 24 milhões de pixels, e 120 milhões de pixels-quadro para animar (acima, usa o primeiro quadro).
/// Uso: <c>var pronto = await Task.Run(() => PreparadorDeAvatar.Preparar(this, uri));</c>
/// </summary>
#pragma warning disable CA1422 // Movie é obsoleta desde o Android 9, mas continua sendo o único jeito de pedir um quadro de um GIF em um instante (o ImageDecoder só anima, sem buscar)
static class PreparadorDeAvatar
{
    const long MaxArquivo = 25L * 1024 * 1024, MaxPixels = 24_000_000, MaxPixelsQuadro = 120_000_000;
    const int MsPorQuadro = 50;

    /// <exception cref="AvatarInvalidoException">Não abriu, é grande demais ou não é imagem.</exception>
    public static AvatarPronto Preparar(Context c, Android.Net.Uri uri)
    {
        var bytes = LerArquivo(c, uri);
        bool gif = bytes.Length > 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F';
        Streemu.Util.Log.Info($"AVATAR preparando: {bytes.Length} bytes, tipo do arquivo '{c.ContentResolver?.GetType(uri)}', {(gif ? "é GIF" : "não é GIF")}");
        return gif ? PrepararGif(bytes) : PrepararParado(bytes, aviso: null);
    }

    static byte[] LerArquivo(Context c, Android.Net.Uri uri)
    {
        using var entrada = c.ContentResolver?.OpenInputStream(uri)
            ?? throw new AvatarInvalidoException($"Não consegui abrir o arquivo escolhido (origem '{uri.Scheme}'). Escolha a imagem de novo.");
        using var memoria = new MemoryStream();
        var parte = new byte[81920];
        int n;
        while ((n = entrada.Read(parte, 0, parte.Length)) > 0)
        {
            memoria.Write(parte, 0, n);
            if (memoria.Length > MaxArquivo) throw new AvatarInvalidoException($"A imagem passa de {MaxArquivo / 1024 / 1024} MB; o máximo é {MaxArquivo / 1024 / 1024} MB. Escolha uma menor.");
        }
        return memoria.ToArray();
    }

    static AvatarPronto PrepararGif(byte[] bytes)
    {
        using var filme = Movie.DecodeByteArray(bytes, 0, bytes.Length);
        if (filme == null)
        {
            Streemu.Util.Log.Info($"AVATAR o decodificador de GIF do Android não abriu o arquivo ({bytes.Length} bytes)");
            return PrepararParado(bytes, "Não consegui animar esse GIF. Usei o primeiro quadro.");
        }
        int duracao = filme.Duration(), w = filme.Width(), h = filme.Height();
        Streemu.Util.Log.Info($"AVATAR gif aberto: {w}x{h} px, duração {duracao} ms");
        if (duracao <= 0 || w <= 0 || h <= 0) return PrepararParado(bytes, null);   // GIF de um quadro só
        int n = Math.Clamp(duracao / MsPorQuadro, 2, TiraDeQuadros.MaxQuadros);
        if ((long)w * h > MaxPixels || (long)w * h * n > MaxPixelsQuadro)
            return PrepararParado(bytes, $"O GIF tem {w}x{h} px e {n} quadros: grande demais para animar (máximo {MaxPixelsQuadro / 1_000_000} milhões de pixels-quadro). Usei o primeiro quadro.");
        return Animar(filme, w, h, n, duracao);
    }

    static AvatarPronto Animar(Movie filme, int w, int h, int n, int duracao)
    {
        var quadros = Amostrar(filme, w, h, n, duracao);
        try
        {
            var tira = TiraDeQuadros.Montar(quadros, Enumerable.Repeat(duracao / n, n).ToList());
            var animacao = TiraDeQuadros.Ler(tira, out var motivo)
                ?? throw new InvalidOperationException($"a tira recém-montada não abriu ({tira.Length} bytes, {n} quadros): {motivo}");
            Streemu.Util.Log.Info($"AVATAR tira pronta: {tira.Length} bytes, {animacao.Quadros.Length} quadros");
            return new AvatarPronto(tira, FormatoDoAvatar.Tira, animacao.Quadros[0], animacao, null);
        }
        finally { foreach (var q in quadros) q.Dispose(); }
    }

    /// <summary>Desenha o filme nos instantes igualmente espaçados e guarda cada quadro já em 64 x 64 (um só bitmap grande é reaproveitado).</summary>
    static List<Bitmap> Amostrar(Movie filme, int w, int h, int n, int duracao)
    {
        using var inteiro = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
        var tela = new Canvas(inteiro);
        var quadros = new List<Bitmap>(n);
        for (int i = 0; i < n; i++)
        {
            inteiro.EraseColor(Color.Transparent);
            filme.SetTime((int)((long)duracao * i / n));
            filme.Draw(tela, 0, 0);
            quadros.Add(Quadrado(inteiro));
        }
        return quadros;
    }

    static AvatarPronto PrepararParado(byte[] bytes, string? aviso)
    {
        using var origem = Abrir(bytes);
        var quadrado = Quadrado(origem);
        using var png = new MemoryStream();
        quadrado.Compress(Bitmap.CompressFormat.Png!, 100, png);
        return new AvatarPronto(png.ToArray(), FormatoDoAvatar.Png, quadrado, null, aviso);
    }

    static Bitmap Abrir(byte[] bytes)
    {
        var medidas = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, medidas);
        if (medidas.OutWidth <= 0 || medidas.OutHeight <= 0)
            throw new AvatarInvalidoException($"Não consegui abrir essa imagem ({TamanhoLegivel(bytes.Length)}). Escolha um JPEG, PNG, GIF ou WebP.");
        if ((long)medidas.OutWidth * medidas.OutHeight > MaxPixels)
            throw new AvatarInvalidoException($"A imagem tem {medidas.OutWidth}x{medidas.OutHeight} px; o máximo é {MaxPixels / 1_000_000} milhões de pixels. Escolha uma menor.");
        var opcoes = new BitmapFactory.Options { InSampleSize = Amostragem(Math.Min(medidas.OutWidth, medidas.OutHeight)) };
        var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, opcoes)
            ?? throw new AvatarInvalidoException($"Não consegui abrir essa imagem ({TamanhoLegivel(bytes.Length)}). Escolha um JPEG, PNG, GIF ou WebP.");
        return Girar(bmp, GrausDaFoto(bytes));
    }

    /// <summary>Potência de 2 que ainda deixa o lado menor com pelo menos 256 px (sobra para reduzir bem até 64).</summary>
    static int Amostragem(int ladoMenor)
    {
        int amostra = 1;
        while (ladoMenor / (amostra * 2) >= 256) amostra *= 2;
        return amostra;
    }

    /// <summary>A câmera grava a foto deitada e anota o giro no EXIF; sem isso o avatar sairia de lado.</summary>
    static int GrausDaFoto(byte[] bytes)
    {
        try
        {
            using var exif = new ExifInterface(new MemoryStream(bytes));
            return exif.GetAttributeInt(ExifInterface.TagOrientation, 1) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception e) when (e is IOException or Java.IO.IOException or ArgumentException) { return 0; }
    }

    static Bitmap Girar(Bitmap bmp, int graus)
    {
        if (graus == 0) return bmp;
        var matriz = new Matrix();
        matriz.PostRotate(graus);
        var girado = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, matriz, true)!;
        if (!ReferenceEquals(girado, bmp)) bmp.Dispose();
        return girado;
    }

    /// <summary>O centro em quadrado, reduzido pela metade até chegar perto de 64 px (a redução de uma vez só serrilha) e então a 64 x 64. Nunca mexe na origem.</summary>
    static Bitmap Quadrado(Bitmap origem)
    {
        int lado = Math.Min(origem.Width, origem.Height);
        var atual = Bitmap.CreateBitmap(lado, lado, Bitmap.Config.Argb8888!);
        var fonte = new Rect((origem.Width - lado) / 2, (origem.Height - lado) / 2, (origem.Width + lado) / 2, (origem.Height + lado) / 2);
        new Canvas(atual).DrawBitmap(origem, fonte, new Rect(0, 0, lado, lado), null);
        while (atual.Width >= TiraDeQuadros.Lado * 2)
        {
            var metade = Bitmap.CreateScaledBitmap(atual, atual.Width / 2, atual.Height / 2, true)!;
            atual.Dispose();
            atual = metade;
        }
        if (atual.Width == TiraDeQuadros.Lado) return atual;
        var pronto = Bitmap.CreateScaledBitmap(atual, TiraDeQuadros.Lado, TiraDeQuadros.Lado, true)!;
        atual.Dispose();
        return pronto;
    }

    static string TamanhoLegivel(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
