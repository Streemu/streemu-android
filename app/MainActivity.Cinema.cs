using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Widget;
using Microsoft.Win32.SafeHandles;
using JsonSeguro = Streemu.Util.JsonSeguro;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// Cinema, modo A ("tenho o arquivo"): o seletor do sistema (sem permissão de armazenamento), a identidade do arquivo contra a do anúncio e a FilmeActivity.
/// </summary>
public partial class MainActivity
{
    const int CodigoArquivoFilme = 43, CodigoCriarSessao = 44, CodigoNotificacaoCinema = 45;
    /// <summary>A sessão para a qual a pessoa está escolhendo o arquivo (o anúncio da sala).</summary>
    JsonObject? _sessaoEscolhida;

    void TenhoOArquivo(JsonObject anuncio)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        _sessaoEscolhida = anuncio;
        var i = new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("video/*");
        try { StartActivityForResult(i, CodigoArquivoFilme); }
        catch (Exception e) { Toast.MakeText(this, "Não deu para abrir o seletor de arquivos: " + e.Message, ToastLength.Long)?.Show(); }
    }

    /// <summary>
    /// "Criar sessão" (F4): o celular vira anfitrião do filme. Seletor do sistema (a permissão de leitura fica guardada para o serviço e para reabrir), a identidade pelo descritor,
    /// título = nome do arquivo sem extensão, e a FilmeActivity abre como anfitriã. Com h264/hevc e áudio AAC, AC-3, E-AC-3 ou MP3 o filme também vai pela transmissão (F5, stream=true); senão, quem entra tem o arquivo ou baixa do celular.
    /// </summary>
    void CriarSessao()
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (Sessao.Sala is not { Connected: true }) { Toast.MakeText(this, "Conecte-se ao grupo para criar uma sessão.", ToastLength.Long)?.Show(); return; }
        if (Filme.FilmeActivity.AnfitriaoAberto) { Toast.MakeText(this, "Você já está numa sessão do Cinema.", ToastLength.Long)?.Show(); return; }
        // a notificação do serviço no Android 13+; sem ela o serviço roda do mesmo jeito (só não aparece o aviso), então não espera a resposta
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Android.Content.PM.Permission.Granted)
            RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, CodigoNotificacaoCinema);
        var i = new Intent(Intent.ActionOpenDocument).AddCategory(Intent.CategoryOpenable).SetType("video/*")
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        try { StartActivityForResult(i, CodigoCriarSessao); }
        catch (Exception e) { Toast.MakeText(this, "Não deu para abrir o seletor de arquivos: " + e.Message, ToastLength.Long)?.Show(); }
    }

    void AoEscolherArquivoAnfitriao(Result resultado, Intent? dados)
    {
        if (resultado != Result.Ok || dados?.Data is not { } uri) return;
        try { ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); }
        catch (Exception e) { LogApp.Info("[filme] permissão persistente do arquivo: " + e.Message); }   // o Uri vale enquanto a activity do filme está de pé mesmo sem ela
        string nome = NomeDoArquivo(uri) ?? "filme";
        string titulo = System.IO.Path.GetFileNameWithoutExtension(nome), ext = System.IO.Path.GetExtension(nome);
        if (titulo.Length == 0) titulo = nome;
        Toast.MakeText(this, "Preparando o arquivo...", ToastLength.Short)?.Show();
        _ = Task.Run(() =>
        {
            string? id = null, erro = null;
            try
            {
                using var pfd = ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new IOException("o arquivo não abriu");
                using var fs = new FileStream(new SafeFileHandle((IntPtr)pfd.DetachFd(), ownsHandle: true), FileAccess.Read, 1 << 16);
                id = Streemu.Filme.Identidade.De(fs);
            }
            catch (Exception e) { erro = e.Message; }
            RunOnUiThread(() =>
            {
                if (IsDestroyed) return;
                if (id == null) { Toast.MakeText(this, $"Não consegui ler o arquivo ({erro}).", ToastLength.Long)?.Show(); return; }
                if (Filme.FilmeActivity.AnfitriaoAberto) return;
                StartActivity(new Intent(this, typeof(Filme.FilmeActivity)).SetData(uri).AddFlags(ActivityFlags.GrantReadUriPermission)
                    .PutExtra("sou_anfitriao", true).PutExtra("filme", id).PutExtra("titulo", titulo).PutExtra("ext", ext));
            });
        });
    }

    string? NomeDoArquivo(Android.Net.Uri uri)
    {
        try
        {
            using var c = ContentResolver!.Query(uri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null);
            if (c != null && c.MoveToFirst()) return c.GetString(0);
        }
        catch (Exception e) { LogApp.Info("[filme] nome do arquivo: " + e.Message); }
        return uri.LastPathSegment;
    }

    void BaixarOFilme(JsonObject anuncio)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        Filme.BaixarFilme.Iniciar(this, anuncio);
    }

    /// <summary>"Assistir pela transmissão" (F3): sem arquivo; a FilmeActivity pede o fluxo ao anfitrião e toca pelo cache do SRT.</summary>
    void AssistirPelaTransmissao(JsonObject anuncio)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        StartActivity(new Intent(this, typeof(Filme.FilmeActivity)).PutExtra("stream", true)
            .PutExtra("anfitriao", JsonSeguro.Texto(anuncio["de"]) ?? "").PutExtra("anfitriao_nome", JsonSeguro.Texto(anuncio["anfitriao"]) ?? "o anfitrião")
            .PutExtra("filme", JsonSeguro.Texto(anuncio["filme"]) ?? "").PutExtra("titulo", JsonSeguro.Texto(anuncio["nome"]) ?? ""));
    }

    /// <summary>"Assistir" no cartão: abre a FilmeActivity com o arquivo baixado (o contêiner já foi conferido ao terminar).</summary>
    void AssistirBaixado(JsonObject anuncio)
    {
        string filme = JsonSeguro.Texto(anuncio["filme"]) ?? "";
        if (Filme.BaixarFilme.De(filme) is not { Fase: "pronto", Caminho: { } caminho } || !File.Exists(caminho))
        { Toast.MakeText(this, "O arquivo baixado não está mais aqui. Baixe de novo.", ToastLength.Long)?.Show(); return; }
        StartActivity(new Intent(this, typeof(Filme.FilmeActivity)).SetData(Android.Net.Uri.FromFile(new Java.IO.File(caminho)))
            .PutExtra("anfitriao", JsonSeguro.Texto(anuncio["de"]) ?? "").PutExtra("anfitriao_nome", JsonSeguro.Texto(anuncio["anfitriao"]) ?? "o anfitrião")
            .PutExtra("filme", filme).PutExtra("titulo", JsonSeguro.Texto(anuncio["nome"]) ?? ""));
    }

    void AoEscolherArquivoFilme(Result resultado, Intent? dados)
    {
        var anuncio = _sessaoEscolhida; _sessaoEscolhida = null;
        if (resultado != Result.Ok || dados?.Data is not { } uri || anuncio == null) return;
        string esperada = JsonSeguro.Texto(anuncio["filme"]) ?? "", dono = JsonSeguro.Texto(anuncio["anfitriao"]) ?? "o anfitrião", titulo = JsonSeguro.Texto(anuncio["nome"]) ?? "";
        string anfitriao = JsonSeguro.Texto(anuncio["de"]) ?? "";
        Toast.MakeText(this, "Conferindo o arquivo...", ToastLength.Short)?.Show();
        _ = Task.Run(() =>
        {
            string? id = null, erro = null;
            try
            {
                // o descritor do seletor tem seek; o FileStream fica dono dele e o fecha
                using var pfd = ContentResolver!.OpenFileDescriptor(uri, "r") ?? throw new IOException("o arquivo não abriu");
                using var fs = new FileStream(new SafeFileHandle((IntPtr)pfd.DetachFd(), ownsHandle: true), FileAccess.Read, 1 << 16);
                id = Streemu.Filme.Identidade.De(fs);
            }
            catch (Exception e) { erro = e.Message; }
            RunOnUiThread(() =>
            {
                if (IsDestroyed) return;
                if (id == null) { Toast.MakeText(this, $"Não consegui ler o arquivo ({erro}).", ToastLength.Long)?.Show(); return; }
                if (id != esperada)
                {
                    LogApp.Info($"[filme] arquivo escolhido não bate com o de {dono} ({id} contra {esperada}).");
                    Toast.MakeText(this, "Esse arquivo não é o mesmo do anfitrião", ToastLength.Long)?.Show();
                    return;
                }
                StartActivity(new Intent(this, typeof(Filme.FilmeActivity)).SetData(uri).AddFlags(ActivityFlags.GrantReadUriPermission)
                    .PutExtra("anfitriao", anfitriao).PutExtra("anfitriao_nome", dono).PutExtra("filme", esperada).PutExtra("titulo", titulo));
            });
        });
    }
}
