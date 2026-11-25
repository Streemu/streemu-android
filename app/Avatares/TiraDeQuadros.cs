using System.Buffers.Binary;
using Android.Graphics;

namespace Streemu.Proto.Avatares;

/// <summary>Um avatar animado já aberto: os quadros quadrados (64 px) e quanto tempo cada um fica na tela.</summary>
sealed record Animacao(Bitmap[] Quadros, int[] TemposMs)
{
    public int DuracaoMs => TemposMs.Sum();
}

/// <summary>
/// A "tira": o avatar animado como os quadros de 64 x 64 empilhados numa imagem JPEG, com o tempo de cada quadro num cabeçalho de poucos bytes. É o que vai
/// para os amigos (e o que eles guardam): fica em 30 a 130 KB qualquer que seja o GIF. O círculo NÃO vai na tira (JPEG não tem transparência): quem recebe
/// aplica a máscara. Byte a byte igual ao <c>AvatarFilmstrip</c> do app Windows (spec 005 do PC), para os dois lados se entenderem.
/// Formato: "LXT1" + nº de quadros (1 byte, 2 a 120) + tempos (uint16 little-endian em ms, um por quadro) + JPEG de 64 x (64 * quadros).
/// Uso: <c>var bytes = TiraDeQuadros.Montar(quadros, tempos); var animacao = TiraDeQuadros.Ler(bytes, out var motivo);</c>
/// </summary>
static class TiraDeQuadros
{
    public const int Lado = 64, MaxQuadros = 40, MaxQuadrosLidos = 120, MaxBytes = 128 * 1024;
    const int Cabecalho = 5;
    static readonly byte[] Magia = "LXT1"u8.ToArray();
    static readonly int[] Qualidades = { 80, 65, 50, 35 };

    /// <summary>
    /// Monta a tira. Se passar de <see cref="MaxBytes"/>, baixa a qualidade e, em último caso, tira quadros alternados somando os tempos
    /// (a animação fica com menos quadros, mas dura o mesmo). Os quadros têm que ser de <see cref="Lado"/> x <see cref="Lado"/>; não são descartados aqui.
    /// </summary>
    public static byte[] Montar(IReadOnlyList<Bitmap> quadros, IReadOnlyList<int> temposMs)
    {
        if (quadros.Count < 2 || quadros.Count != temposMs.Count)
            throw new ArgumentException($"a tira pede 2 ou mais quadros com um tempo cada; recebi {quadros.Count} quadros e {temposMs.Count} tempos");
        var q = quadros.ToList();
        var t = temposMs.ToList();
        while (true)
        {
            foreach (int qualidade in Qualidades)
                if (Empacotar(q, t, qualidade) is { } bytes && bytes.Length <= MaxBytes) return bytes;
            if (q.Count <= 2) return Empacotar(q, t, Qualidades[^1]);
            (q, t) = UmSimUmNao(q, t);
        }
    }

    /// <summary>Lê uma tira (de um amigo ou do disco). Nunca lança: se não servir, devolve null e o <paramref name="motivo"/> em português.</summary>
    public static Animacao? Ler(byte[] dados, out string? motivo)
    {
        motivo = Conferir(dados, out int n);
        if (motivo != null) return null;
        using var faixa = BitmapFactory.DecodeByteArray(dados, Cabecalho + n * 2, dados.Length - Cabecalho - n * 2);
        if (faixa == null || faixa.Width != Lado || faixa.Height != Lado * n)
        {
            motivo = $"A tira do avatar não abriu ({dados.Length} bytes, {n} quadros; esperado uma imagem de {Lado}x{Lado * n}, veio {faixa?.Width}x{faixa?.Height}).";
            return null;
        }
        return new Animacao(Cortar(faixa, n), LerTempos(dados, n));
    }

    static string? Conferir(byte[] dados, out int n)
    {
        n = dados.Length > 4 ? dados[4] : 0;
        if (dados.Length < Cabecalho || !dados.AsSpan(0, 4).SequenceEqual(Magia))
            return $"A tira do avatar tem um cabeçalho inválido ({dados.Length} bytes; esperado começar com LXT1).";
        if (n < 2 || n > MaxQuadrosLidos) return $"A tira do avatar diz ter {n} quadros; o esperado é de 2 a {MaxQuadrosLidos}.";
        return dados.Length > Cabecalho + n * 2 + 100 ? null : $"A tira do avatar está curta demais ({dados.Length} bytes para {n} quadros).";
    }

    static int[] LerTempos(byte[] dados, int n)
    {
        var tempos = new int[n];
        for (int i = 0; i < n; i++)
        {
            int ms = BinaryPrimitives.ReadUInt16LittleEndian(dados.AsSpan(Cabecalho + i * 2));
            tempos[i] = ms < 20 ? 100 : Math.Min(ms, 10_000);   // como nos GIFs: 0 a 1 centésimo = 100 ms
        }
        return tempos;
    }

    static Bitmap[] Cortar(Bitmap faixa, int n)
    {
        var quadros = new Bitmap[n];
        for (int i = 0; i < n; i++) quadros[i] = Bitmap.CreateBitmap(faixa, 0, i * Lado, Lado, Lado);
        return quadros;
    }

    static byte[] Empacotar(IReadOnlyList<Bitmap> quadros, IReadOnlyList<int> tempos, int qualidade)
    {
        using var faixa = Bitmap.CreateBitmap(Lado, Lado * quadros.Count, Bitmap.Config.Argb8888!);
        var tela = new Canvas(faixa);
        for (int i = 0; i < quadros.Count; i++) tela.DrawBitmap(quadros[i], 0, i * Lado, null);
        using var jpeg = new MemoryStream();
        faixa.Compress(Bitmap.CompressFormat.Jpeg!, qualidade, jpeg);
        using var saida = new MemoryStream();
        saida.Write(Magia);
        saida.WriteByte((byte)quadros.Count);
        foreach (int ms in tempos) saida.Write(BitConverter.GetBytes((ushort)Math.Clamp(ms, 1, ushort.MaxValue)));
        saida.Write(jpeg.GetBuffer(), 0, (int)jpeg.Length);
        return saida.ToArray();
    }

    static (List<Bitmap>, List<int>) UmSimUmNao(List<Bitmap> quadros, List<int> tempos)
    {
        var fica = new List<Bitmap>();
        var tempoFica = new List<int>();
        for (int i = 0; i < quadros.Count; i += 2)
        {
            fica.Add(quadros[i]);
            tempoFica.Add(tempos[i] + (i + 1 < quadros.Count ? tempos[i + 1] : 0));
        }
        return (fica, tempoFica);
    }
}
