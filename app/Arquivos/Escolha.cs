using Android.Content;
using Android.OS;
using Android.Provider;
using Microsoft.Win32.SafeHandles;
using Streemu.Arquivos;
using Streemu.Filme;
using AndroidUri = Android.Net.Uri;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>Um arquivo que a pessoa escolheu e que dá para mandar: o endereço do seletor, o nome, o tamanho e a identidade (a mesma conta do PC).</summary>
public sealed record ArquivoEscolhido(AndroidUri Uri, string Nome, long Tamanho, string Identidade);

/// <summary>Um arquivo que não dá para mandar, com o motivo já em português para a tela.</summary>
public sealed record ArquivoRecusado(string Nome, string Motivo);

public sealed record ResultadoEscolha(IReadOnlyList<ArquivoEscolhido> Aceitos, IReadOnlyList<ArquivoRecusado> Recusados);

/// <summary>
/// Escolher o que enviar (spec 030, seção 4): o seletor do sistema (sem permissão de armazenamento), o acesso guardado para retomar e para
/// "Pedir de novo", e a leitura de nome, tamanho e identidade pelo descritor de arquivo. Arquivo que só existe na nuvem (Drive, Fotos) ou sem
/// tamanho confiável é recusado com o motivo. O nome que vai ao amigo é o que o seletor informa; quem recebe o limpa pelo NomeSeguro.
/// Ex.: <c>StartActivityForResult(Escolha.Seletor(), codigo);</c> e, no resultado, <c>await Escolha.LerAsync(this, Escolha.UrisDe(dados), ct)</c>.
/// </summary>
static class Escolha
{
    const string MotivoNuvem = "Esse arquivo está só na nuvem. Baixe no aparelho e tente de novo.";

    public static Intent Seletor() =>
        new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("*/*")
            .PutExtra(Intent.ExtraAllowMultiple, true)
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);

    /// <summary>Os endereços que o seletor devolveu: vários vêm em ClipData, um só pode vir em Data.</summary>
    public static IReadOnlyList<AndroidUri> UrisDe(Intent? dados)
    {
        var lista = new List<AndroidUri>();
        if (dados?.ClipData is { } clip)
            for (int i = 0; i < clip.ItemCount; i++) if (clip.GetItemAt(i)?.Uri is { } u) lista.Add(u);
        if (lista.Count == 0 && dados?.Data is { } um) lista.Add(um);
        return lista;
    }

    public static Task<ResultadoEscolha> LerAsync(Context c, IReadOnlyList<AndroidUri> uris, CancellationToken ct) => Task.Run(() =>
    {
        var aceitos = new List<ArquivoEscolhido>();
        var recusados = new List<ArquivoRecusado>();
        foreach (var uri in uris.Take(MensagemArquivo.MaxArquivos))
        {
            ct.ThrowIfCancellationRequested();
            Classificar(c, uri, aceitos, recusados);
        }
        foreach (var uri in uris.Skip(MensagemArquivo.MaxArquivos))
            recusados.Add(new(NomeDe(c, uri), $"Um pedido leva no máximo {MensagemArquivo.MaxArquivos} arquivos. Mande o resto em outro pedido."));
        return new ResultadoEscolha(aceitos, recusados);
    }, ct);

    static void Classificar(Context c, AndroidUri uri, List<ArquivoEscolhido> aceitos, List<ArquivoRecusado> recusados)
    {
        GuardarAcesso(c, uri);
        string nome = NomeDe(c, uri);
        try
        {
            if (EhVirtual(c, uri)) { recusados.Add(new(nome, MotivoNuvem)); return; }
            using var fs = AbrirParaLer(c, uri);
            if (fs.Length <= 0) { recusados.Add(new(nome, "Esse arquivo está vazio ou o aparelho não informou o tamanho.")); return; }
            aceitos.Add(new(uri, nome, fs.Length, Identidade.De(fs)));
        }
        catch (Exception e) when (e is IOException or Java.IO.IOException or UnauthorizedAccessException or Java.Lang.SecurityException or Java.IO.FileNotFoundException)
        {
            LogApp.Info($"[arquivo] não consegui ler um arquivo escolhido: {e.GetType().Name}");
            recusados.Add(new(nome, "Não consegui ler esse arquivo. Ele pode ter sido movido ou o acesso foi perdido; escolha de novo."));
        }
    }

    /// <summary>Guarda a permissão de leitura para retomar e para o "Pedir de novo" depois que o app fechar. O sistema limita quantas guarda; falhar aqui não impede o envio de agora.</summary>
    public static void GuardarAcesso(Context c, AndroidUri uri)
    {
        try { c.ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); }
        catch (Exception e) { LogApp.Info($"[arquivo] acesso não guardado ({e.GetType().Name}): o envio de agora segue, mas retomar depois pode pedir o arquivo de novo"); }
    }

    /// <summary>O arquivo para ler, com seek (a identidade e o envio leem por posição). Quem chama fecha.</summary>
    public static FileStream AbrirParaLer(Context c, AndroidUri uri)
    {
        using var pfd = c.ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new IOException($"O Android não abriu {uri}; esperado: arquivo acessível.");
        return new FileStream(new SafeFileHandle((IntPtr)pfd.DetachFd(), ownsHandle: true), FileAccess.Read, 1 << 16);
    }

    static bool EhVirtual(Context c, AndroidUri uri)
    {
        if (!DocumentsContract.IsDocumentUri(c, uri) || Build.VERSION.SdkInt < BuildVersionCodes.N) return false;
        using var cursor = c.ContentResolver!.Query(uri, [DocumentsContract.Document.ColumnFlags], null, null, null);
        return cursor != null && cursor.MoveToFirst() && ((DocumentContractFlags)cursor.GetInt(0) & DocumentContractFlags.VirtualDocument) != 0;
    }

    public static string NomeDe(Context c, AndroidUri uri)
    {
        try
        {
            using var cursor = c.ContentResolver!.Query(uri, [OpenableColumns.DisplayName], null, null, null);
            if (cursor != null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } nome) return nome;
        }
        catch (Exception e) { LogApp.Info($"[arquivo] nome do arquivo escolhido ({e.GetType().Name})"); }
        return uri.LastPathSegment ?? "arquivo";
    }
}
