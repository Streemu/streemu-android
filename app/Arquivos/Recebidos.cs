using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using AndroidX.Core.Content;
using Streemu.Arquivos;
using Streemu.Filme;
using AndroidUri = Android.Net.Uri;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Onde um arquivo recebido ficou, para o histórico e para o Abrir. <see cref="Onde"/> é o texto que a pessoa lê ("Downloads/Streemu").
/// <see cref="Caminho"/> é o arquivo no disco (pasta do app, ou Downloads/Streemu no Android 8 e 9); <see cref="UriConteudo"/>, o endereço do MediaStore ou da pasta escolhida.
/// <see cref="Aviso"/> só vem quando o app teve de usar outro lugar que o esperado (ex.: a pasta escolhida deixou de existir).
/// </summary>
public sealed record ArquivoPublicado(string Nome, string Onde, string? UriConteudo, string? Caminho, string? Aviso = null);

/// <summary>
/// A pasta de recebidos (spec 030, seção 4). O arquivo chega e é conferido na pasta própria do app (sem permissão nenhuma) e, depois de
/// conferido, é publicado onde a pessoa acha, segundo a <see cref="PoliticaDeDestino"/>: <c>Downloads/Streemu</c> pelo MediaStore no
/// Android 10+, a pasta que ela escolheu pelo seletor de pastas (qualquer versão), ou, no Android 8 e 9 sem pasta escolhida, a pasta do app.
/// Ex.: <c>var p = await Recebidos.PublicarAsync(ctx, caminhoConferido, pastaEscolhida: null, ct);</c>
/// </summary>
static class Recebidos
{
    const string RelativaPublica = "Download/Streemu/", TextoPublico = "Downloads/Streemu", Autoridade = ".arquivos";
    const int Bloco = 1 << 20;

#if DEBUG
    /// <summary>Só Debug: finge outra versão do Android para exercitar o caminho do Android 8 e 9 neste aparelho (adb ... --ei sdk 28).</summary>
    public static int? SdkForcado { get; set; }
#endif

    static int Sdk
    {
        get
        {
#if DEBUG
            if (SdkForcado is int forcado) return forcado;
#endif
            return (int)Build.VERSION.SdkInt;
        }
    }

#if DEBUG
    /// <summary>Só Debug: finge ter (ou não ter) a permissão de escrita do Android 8 e 9 (adb ... --ez permissao false).</summary>
    public static bool? PermissaoForcada { get; set; }
#endif

    static bool TemPermissaoDeEscrita(Context c)
    {
#if DEBUG
        if (PermissaoForcada is bool forcada) return forcada;
#endif
        return PermissaoDeArmazenamento.Tem(c);
    }

    /// <summary>A tela precisa pedir a permissão de escrita antes de receber? (só Android 8 e 9; ver <see cref="PoliticaDeDestino.PrecisaPedirPermissao"/>).</summary>
    public static bool PrecisaPedirPermissao(Context c, string? pastaEscolhida) =>
        PoliticaDeDestino.PrecisaPedirPermissao(Sdk, EhPastaEscolhida(pastaEscolhida) && PastaEscolhida.AindaVale(c, pastaEscolhida!), TemPermissaoDeEscrita(c));

    /// <summary>Onde o arquivo vai parar, dado o que a pessoa escolheu (e se a escolha ainda vale).</summary>
    public static DestinoDeRecebidos DestinoPara(Context c, string? pastaEscolhida) =>
        PoliticaDeDestino.Decidir(Sdk, EhPastaEscolhida(pastaEscolhida) && PastaEscolhida.AindaVale(c, pastaEscolhida!), TemPermissaoDeEscrita(c));

    /// <summary>A pasta do app onde o <see cref="Download"/> grava e confere (Android/data/pacote/files/Recebidos). Também liga a medida de espaço do Android.</summary>
    public static string PastaDoApp(Context c)
    {
        string p = (c.GetExternalFilesDir("Recebidos") ?? new Java.IO.File(c.FilesDir, "Recebidos"))!.AbsolutePath;
        Directory.CreateDirectory(p);
        Download.EspacoLivre ??= pasta => { try { return new StatFs(pasta).AvailableBytes; } catch { return null; } };
        return p;
    }

    /// <summary>O texto "onde ficam" da tela: a pasta escolhida, "Downloads/Streemu" (10+) ou a pasta do app (8 e 9).</summary>
    public static string TextoDoDestino(Context c, string? pastaEscolhida) => DestinoPara(c, pastaEscolhida) switch
    {
        DestinoDeRecebidos.PastaEscolhida => NomeDaPastaEscolhida(pastaEscolhida!),
        DestinoDeRecebidos.Downloads or DestinoDeRecebidos.DownloadsDireto => TextoPublico,
        _ => "Pasta do Streemu no celular (Android/data)",
    };

    /// <summary>
    /// Por que não cabe (nulo = cabe), antes de aceitar. Quando o arquivo ainda é copiado para fora da pasta do app, o pior caso é o arquivo
    /// duas vezes (o conferido e a cópia publicada); por isso conta o dobro do que falta baixar.
    /// </summary>
    public static string? FaltaEspaco(Context c, long total, long jaBaixado, string? pastaEscolhida)
    {
        long falta = Math.Max(0, total - jaBaixado);
        long precisa = PoliticaDeDestino.CopiaNoFim(DestinoPara(c, pastaEscolhida)) ? falta + total : falta;
        return Download.FaltaEspaco(PastaDoApp(c), precisa, "o arquivo");
    }

    public static bool EhPastaEscolhida(string? pasta) => pasta != null && pasta.StartsWith("content://", StringComparison.Ordinal);

    /// <summary>
    /// Leva o arquivo conferido para o destino e apaga a cópia da pasta do app. Se algo falhar no meio, a cópia parcial no destino é
    /// removida e o arquivo conferido continua na pasta do app (nada se perde). Ex.: <c>await Recebidos.PublicarAsync(ctx, "/.../mumu.png", null, ct)</c>.
    /// </summary>
    public static Task<ArquivoPublicado> PublicarAsync(Context c, string caminho, string? pastaEscolhida, CancellationToken ct) => Task.Run(() =>
    {
        string nome = Path.GetFileName(caminho);
        string? aviso = EhPastaEscolhida(pastaEscolhida) && !PastaEscolhida.AindaVale(c, pastaEscolhida!)
            ? "A pasta que você escolheu não está mais acessível; guardei no lugar padrão. Escolha a pasta de novo se quiser." : null;
        return DestinoPara(c, pastaEscolhida) switch
        {
            DestinoDeRecebidos.PastaEscolhida => Terminar(caminho, PublicarEmPasta(c, caminho, nome, AndroidUri.Parse(pastaEscolhida!)!, ct)),
            DestinoDeRecebidos.Downloads => Terminar(caminho, PublicarEmDownloads(c, caminho, nome, ct) with { Aviso = aviso }),
            DestinoDeRecebidos.DownloadsDireto => Terminar(caminho, PublicarEmDownloadsDireto(c, caminho, nome, ct) with { Aviso = aviso }),
            _ => new ArquivoPublicado(nome, TextoDoDestino(c, null), null, caminho, aviso ?? AvisoSemPermissao()),
        };
    }, ct);

    static ArquivoPublicado Terminar(string caminhoLocal, ArquivoPublicado publicado)
    {
        try { File.Delete(caminhoLocal); }
        catch (Exception e) { LogApp.Info($"[arquivo] não apaguei a cópia da pasta do app: {e.Message}"); }
        return publicado;
    }

    static ArquivoPublicado PublicarEmDownloads(Context c, string caminho, string nome, CancellationToken ct)
    {
        var r = c.ContentResolver!;
        var colecao = MediaStore.Downloads.ExternalContentUri!;
        string livre = NomeLivre.De(nome, n => ExisteEmDownloads(r, colecao, n));
        var valores = new ContentValues();
        valores.Put("_display_name", livre);
        valores.Put("mime_type", TipoDe(livre));
        valores.Put("relative_path", RelativaPublica);
        valores.Put("is_pending", 1);
        var uri = r.Insert(colecao, valores) ?? throw new IOException($"O Android não criou \"{livre}\" em {TextoPublico}; esperado: espaço livre e o app com acesso a Downloads.");
        try
        {
            using (var saida = r.OpenOutputStream(uri, "w") ?? throw new IOException("O Android não abriu o arquivo de destino para gravar."))
                Copiar(caminho, saida, ct);
            var pronto = new ContentValues();
            pronto.Put("is_pending", 0);
            r.Update(uri, pronto, null, null);
        }
        catch { try { r.Delete(uri, null, null); } catch { } throw; }
        return new ArquivoPublicado(livre, TextoPublico, uri.ToString(), null);
    }

    /// <summary>Android 8 e 9 sem a permissão de escrita: o arquivo fica a salvo na pasta do app, e a tela diz como levá-lo para Downloads.</summary>
    static string? AvisoSemPermissao() => Sdk < PoliticaDeDestino.PrimeiraComDownloadsPeloMediaStore
        ? "Sem a permissão para gravar em Downloads, o arquivo ficou na pasta do Streemu (Android/data). Libere a permissão de arquivos do Streemu nos ajustes do Android para os próximos irem para Downloads/Streemu." : null;

    /// <summary>
    /// Android 8 e 9: grava direto em <c>Downloads/Streemu</c> (com a permissão de escrita) e avisa a galeria e os gerenciadores de arquivos com o
    /// MediaScanner, para o arquivo aparecer na hora. A cópia parcial é apagada se algo falhar no meio.
    /// </summary>
    static ArquivoPublicado PublicarEmDownloadsDireto(Context c, string caminho, string nome, CancellationToken ct)
    {
        string pasta = Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath, "Streemu");
        Directory.CreateDirectory(pasta);
        string livre = NomeLivre.De(nome, n => File.Exists(Path.Combine(pasta, n)));
        string destino = Path.Combine(pasta, livre);
        try
        {
            using (var saida = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None, Bloco)) Copiar(caminho, saida, ct);
        }
        catch { try { File.Delete(destino); } catch { } throw; }
        Android.Media.MediaScannerConnection.ScanFile(c, [destino], [TipoDe(livre)], null);
        return new ArquivoPublicado(livre, TextoPublico, null, destino);
    }

    static bool ExisteEmDownloads(ContentResolver r, AndroidUri colecao, string nome)
    {
        using var cursor = r.Query(colecao, ["_id"], "relative_path = ? AND _display_name = ?", [RelativaPublica, nome], null);
        return cursor is { Count: > 0 };
    }

    static ArquivoPublicado PublicarEmPasta(Context c, string caminho, string nome, AndroidUri arvore, CancellationToken ct)
    {
        var r = c.ContentResolver!;
        var pai = DocumentsContract.BuildDocumentUriUsingTree(arvore, DocumentsContract.GetTreeDocumentId(arvore))!;
        var existentes = NomesNaPasta(r, arvore);
        string livre = NomeLivre.De(nome, existentes.Contains);
        var doc = DocumentsContract.CreateDocument(r, pai, TipoDe(livre), livre)
            ?? throw new IOException($"A pasta escolhida não aceitou criar \"{livre}\"; esperado: pasta com permissão de escrita. Escolha a pasta de novo.");
        try
        {
            using (var saida = r.OpenOutputStream(doc, "w") ?? throw new IOException("A pasta escolhida não abriu o arquivo para gravar."))
                Copiar(caminho, saida, ct);
        }
        catch { try { DocumentsContract.DeleteDocument(r, doc); } catch { } throw; }
        return new ArquivoPublicado(livre, NomeDaPastaEscolhida(arvore.ToString()!), doc.ToString(), null);
    }

    static HashSet<string> NomesNaPasta(ContentResolver r, AndroidUri arvore)
    {
        var filhos = DocumentsContract.BuildChildDocumentsUriUsingTree(arvore, DocumentsContract.GetTreeDocumentId(arvore))!;
        var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cursor = r.Query(filhos, ["_display_name"], null, null, null);
        while (cursor != null && cursor.MoveToNext()) nomes.Add(cursor.GetString(0) ?? "");
        return nomes;
    }

    static void Copiar(string origem, Stream destino, CancellationToken ct)
    {
        using var entrada = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.Read, Bloco);
        var buf = new byte[Bloco];
        int n;
        while ((n = entrada.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); destino.Write(buf, 0, n); }
        destino.Flush();
    }

    static string TipoDe(string nome)
    {
        string ext = Path.GetExtension(nome).TrimStart('.').ToLowerInvariant();
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) ?? "application/octet-stream";
    }

    /// <summary>"Downloads/Meus arquivos" a partir do endereço da pasta escolhida; se não der para ler, um texto genérico.</summary>
    static string NomeDaPastaEscolhida(string arvore)
    {
        try { return Uri.UnescapeDataString(arvore.Split("%3A").LastOrDefault() ?? arvore).Trim('/'); }
        catch (UriFormatException) { return "pasta escolhida"; }
    }

    /// <summary>
    /// Abre o arquivo no app que a pessoa tiver. Quem chama já perguntou se o tipo é arriscado (<see cref="TipoArriscado"/>). No Android 10+
    /// abre o endereço do MediaStore; no 8 e 9, o arquivo da pasta do app pelo <see cref="ProvedorDeRecebidos"/>. Falso se não há como abrir.
    /// </summary>
    public static bool Abrir(Context c, ArquivoPublicado p)
    {
        var uri = EnderecoParaAbrir(c, p);
        if (uri == null) return false;
        var i = new Intent(Intent.ActionView).SetDataAndType(uri, TipoDe(p.Nome))!.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        try { c.StartActivity(i); return true; }
        catch (ActivityNotFoundException) { return false; }
    }

    static AndroidUri? EnderecoParaAbrir(Context c, ArquivoPublicado p)
    {
        if (p.UriConteudo != null) return AndroidUri.Parse(p.UriConteudo);
        if (p.Caminho == null || !File.Exists(p.Caminho)) return null;
        try { return FileProvider.GetUriForFile(c, c.PackageName + Autoridade, new Java.IO.File(p.Caminho)); }
        catch (Java.Lang.IllegalArgumentException e) { LogApp.Info($"[arquivo] o provedor não cobre esse caminho: {e.Message}"); return null; }
    }

    /// <summary>
    /// Mostra onde o arquivo está: a lista de downloads do sistema (a pasta do Streemu fica nela), ou a pasta escolhida. Na pasta do app (8 e 9)
    /// não há como abrir um gerenciador de arquivos naquele lugar de forma confiável: devolve falso e a tela mostra o caminho em texto.
    /// </summary>
    public static bool MostrarNaPasta(Context c, string? pastaEscolhida)
    {
        var destino = DestinoPara(c, pastaEscolhida);
        if (destino == DestinoDeRecebidos.PastaDoApp) return false;
        var i = destino == DestinoDeRecebidos.PastaEscolhida
            ? new Intent(Intent.ActionView).SetData(AndroidUri.Parse(pastaEscolhida!))
            : new Intent(DownloadManager.ActionViewDownloads);
        try { c.StartActivity(i!.AddFlags(ActivityFlags.NewTask)); return true; }
        catch (ActivityNotFoundException) { return false; }
    }
}
