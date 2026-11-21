using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Widget;
using Uri = System.Uri;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace Streemu.Proto;

sealed record VersaoDisponivel(int Codigo, string Versao, string Notas, bool Obrigatoria, Uri Url, string Sha256, long Tamanho)
{
    const string Prefixo = "https://github.com/Streemu/streemu-android/releases/download/";
    const string NomeDoApk = "Streemu-Android.apk";
    public const string Manifesto = "https://github.com/Streemu/streemu-android/releases/latest/download/atualizacao.json";
    public const string NomeDasPreferencias = "atualizacao";
    static readonly Regex CaminhoDoApk = new(@"\A[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}/" + Regex.Escape(NomeDoApk) + @"\z", RegexOptions.CultureInvariant);

    public static VersaoDisponivel Ler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement;
        int codigo = item.GetProperty("versionCode").GetInt32();
        string versao = item.GetProperty("versao").GetString() ?? "";
        string notas = item.GetProperty("notas").GetString() ?? "";
        bool obrigatoria = item.GetProperty("obrigatoria").GetBoolean();
        string url = item.GetProperty("url").GetString() ?? "";
        string sha = item.GetProperty("sha256").GetString() ?? "";
        long tamanho = item.GetProperty("tamanho").GetInt64();
        if (codigo is < 1 or > 2_100_000_000 || versao.Length is < 1 or > 40 || notas.Length > 12_000 ||
            tamanho is < 1 or > 512 * 1024 * 1024 || sha.Length != 64 || !sha.All(Uri.IsHexDigit) ||
            !url.StartsWith(Prefixo, StringComparison.Ordinal) ||
            !CaminhoDoApk.IsMatch(url[Prefixo.Length..]))
            throw new InvalidDataException("manifesto_invalido");
        return new(codigo, versao, notas, obrigatoria, new Uri(url), sha, tamanho);
    }
}

// O estado e a interface vivem na thread principal. Rede, arquivo, hash e cópia para o instalador ficam numa thread Android de fundo.
static class Atualizacao
{
    static readonly HttpClient Http = new();
    static readonly TimeSpan Intervalo = TimeSpan.FromHours(6);
    static readonly Handler Ui = new(Looper.MainLooper!);
    static readonly Java.Lang.Runnable Timer = new(() => Verificar());
    static Context _app = null!;
    static ISharedPreferences _prefs = null!;
    static MainActivity? _tela;
    static CancellationTokenSource? _operacao;
    static int _players, _instalada, _tentouWifi, _sessao = -1;
    static int? _permissaoPara;
    static Intent? _confirmacao;
    static bool _confirmacaoAberta;
    static bool _baixado, _preparando;
    static string Caminho => Path.Combine(_app.CacheDir!.AbsolutePath, "atualizacao.apk");
    static bool EmLive => _players > 0 || Telemetria.Ativo;
    public static string VersaoInstalada { get; private set; } = "";
    public static VersaoDisponivel? Disponivel { get; private set; }
    public static bool BloqueiaSala => Disponivel?.Obrigatoria == true;
    public static bool DeveVerificarAoAbrir => AtrasoParaVerificar() == 0;
    public static bool PodeAdiar => _sessao < 0 && !_preparando;
    public static bool Ocupado => _operacao != null || _sessao >= 0;
    public static string Rotulo => _sessao >= 0 ? "Aguardando confirmação…" : _operacao != null ? "Aguarde…" : "Atualizar";
    public static event Action? Mudou;

    public static void Iniciar(Context c)
    {
        if (_app != null) return;
        _app = c.ApplicationContext!;
        _prefs = Preferencias(_app);
        var pm = _app.PackageManager!;
        var instalada = pm.GetPackageInfo(_app.PackageName!, 0)!;
        _instalada = (int)Codigo(instalada);
        VersaoInstalada = instalada.VersionName ?? "";
        ApagarApk();
        if (_prefs.GetString("obrigatoria", null) is { } json)
        {
            try
            {
                var versao = VersaoDisponivel.Ler(json);
                if (versao.Codigo > _instalada && versao.Obrigatoria) Disponivel = versao;
            }
            catch (Exception) { _prefs.Edit()!.Remove("obrigatoria")!.Apply(); }
        }
    }

    public static void Mostrar(MainActivity tela)
    {
        _tela = tela;
        TentarConfirmacao();
        if (_permissaoPara is int codigo)
        {
            _permissaoPara = null;
            if (Disponivel is { } nova && nova.Codigo == codigo)
            {
                if (_app.PackageManager!.CanRequestPackageInstalls()) Atualizar();
                else RecusarInstalacao(nova);
            }
        }
        Verificar();
        TentarNoWifi();
    }

    public static void Ocultar(MainActivity tela)
    {
        if (_tela != tela) return;
        _tela = null;
        Ui.RemoveCallbacks(Timer);
    }

    public static void PlayerAbriu()
    {
        _players++;
        _operacao?.Cancel();
        if (_sessao >= 0 && !_confirmacaoAberta)
        {
            LimparConfirmacao();
            Abandonar(_sessao);
        }
        Ui.RemoveCallbacks(Timer);
    }

    public static void PlayerFechou()
    {
        _players--;
        Verificar();
        TentarNoWifi();
    }

    public static void Verificar(bool manual = false)
    {
        if (_tela == null || EmLive || Ocupado) return;
        Ui.RemoveCallbacks(Timer);
        long atraso = AtrasoParaVerificar();
        if (!manual && atraso > 0)
        {
            Ui.PostDelayed(Timer, atraso);
            return;
        }
        _prefs.Edit()!.PutLong("ultima", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!.Apply();
        Executar(ct =>
        {
            using var resposta = Buscar(new Uri(VersaoDisponivel.Manifesto), ct);
            using var corpo = resposta.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
            using var buffer = new MemoryStream();
            Copiar(corpo, buffer, 64 * 1024, ct);
            string json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return (Json: json, Versao: VersaoDisponivel.Ler(json));
        }, (resultado, erro, cancelada) =>
        {
            try
            {
                if (erro != null || cancelada || EmLive) return;
                AplicarManifesto(resultado.Json, resultado.Versao, manual);
            }
            finally { Sessao.AplicarBloqueioDeAtualizacao(); }
        }, TimeSpan.FromSeconds(8));
    }

    static long AtrasoParaVerificar()
    {
        long tempo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _prefs.GetLong("ultima", 0);
        long intervalo = (long)Intervalo.TotalMilliseconds;
        return tempo >= 0 && tempo < intervalo ? intervalo - tempo : 0;
    }

    static void AplicarManifesto(string json, VersaoDisponivel nova, bool manual)
    {
        if (nova.Codigo <= _instalada)
        {
            if (Disponivel == null && manual) Avisar("Você já está na versão mais recente.");
            return;
        }
        if (Disponivel is { } anterior && nova.Codigo < anterior.Codigo) return;
        if (Disponivel != nova) ApagarApk();
        if (manual) _prefs.Edit()!.Remove("adiada")!.Apply();
        if (!nova.Obrigatoria && _prefs.GetInt("adiada", 0) == nova.Codigo) return;

        var prefs = _prefs.Edit()!;
        if (nova.Obrigatoria) prefs.PutString("obrigatoria", json);
        else prefs.Remove("obrigatoria");
        prefs.Apply();
        Disponivel = nova;
        if (manual) _tela?.MostrarAtualizacao();
    }

    public static void Depois()
    {
        if (Disponivel is not { Obrigatoria: false } nova || !PodeAdiar) return;
        _operacao?.Cancel();
        if (_operacao == null) ApagarApk();
        _prefs.Edit()!.PutInt("adiada", nova.Codigo)!.Apply();
        Disponivel = null;
        Evento(VersaoInstalada, nova.Versao, "recusou", "depois");
        Mudou?.Invoke();
    }

    public static void Atualizar()
    {
        if (_tela is not { } tela || Disponivel is not { } nova || EmLive || Ocupado) return;
        if (_baixado) { ConfirmarInstalacao(tela, nova); return; }
        if (NoWifi()) { Baixar(nova, instalar: true, exigirWifi: true); return; }
        string tamanho = (nova.Tamanho / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture);
        new AlertDialog.Builder(tela)
            .SetTitle("Baixar usando dados móveis?")!
            .SetMessage($"A atualização ocupa {tamanho} MB.")!
            .SetPositiveButton("Baixar", (_, _) =>
            {
                if (_tela == tela && !Ocupado && !EmLive && Disponivel == nova)
                    Baixar(nova, instalar: true, exigirWifi: false);
            })!
            .SetNegativeButton("Cancelar", (_, _) => Evento(VersaoInstalada, nova.Versao, "recusou", "dados_moveis"))!
            .Show();
    }

    static void TentarNoWifi()
    {
        if (_tela == null || EmLive || Ocupado || _baixado || Disponivel is not { } nova || _tentouWifi == nova.Codigo || !NoWifi()) return;
        Baixar(nova, instalar: false, exigirWifi: true);
    }

    static bool NoWifi()
    {
        var rede = (ConnectivityManager)_app.GetSystemService(Context.ConnectivityService)!;
        return rede.GetNetworkCapabilities(rede.ActiveNetwork)?.HasTransport(TransportType.Wifi) == true;
    }

    static void Baixar(VersaoDisponivel nova, bool instalar, bool exigirWifi)
    {
        if (exigirWifi) _tentouWifi = nova.Codigo;
        Executar(ct =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void ConferirRede()
            {
                if (exigirWifi && !NoWifi()) throw new InvalidDataException("wifi_desconectado");
            }
            using (var resposta = Buscar(nova.Url, ct))
            using (var corpo = resposta.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult())
            using (var arquivo = File.Create(Caminho))
            {
                if (resposta.Content.Headers.ContentLength is { } tamanho && tamanho != nova.Tamanho)
                    throw new InvalidDataException("tamanho");
                ConferirRede();
                if (Copiar(corpo, arquivo, nova.Tamanho, ct, ConferirRede, hash) != nova.Tamanho)
                    throw new InvalidDataException("tamanho");
            }
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(Convert.FromHexString(nova.Sha256)))
                throw new InvalidDataException("sha256");
            ct.ThrowIfCancellationRequested();
            ConferirApk(nova);
            return true;
        }, (_, erro, cancelada) =>
        {
            if (erro != null || cancelada || Disponivel != nova || EmLive)
            {
                ApagarApk();
                if (erro is not OperationCanceledException && erro != null)
                {
                    Evento(VersaoInstalada, nova.Versao, "falhou", Motivo(erro));
                    if (instalar) Avisar("Não foi possível baixar ou conferir a atualização.");
                }
                return;
            }

            _baixado = true;
            Evento(VersaoInstalada, nova.Versao, "baixou", null);
            if (instalar && _tela is { } tela) ConfirmarInstalacao(tela, nova);
        }, TimeSpan.FromMinutes(20));
    }

    static void ConfirmarInstalacao(MainActivity tela, VersaoDisponivel nova)
    {
        if (_app.PackageManager!.CanRequestPackageInstalls())
        {
            Instalar(nova);
            return;
        }

        new AlertDialog.Builder(tela)
            .SetTitle("Permitir que o Streemu instale a atualização")!
            .SetMessage("Nas configurações do Android, permita a instalação de apps pelo Streemu. Depois, volte para confirmar a atualização.")!
            .SetPositiveButton("Abrir configurações", (_, _) =>
            {
                _permissaoPara = nova.Codigo;
                try
                {
                    tela.StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                        Android.Net.Uri.Parse("package:" + _app.PackageName)));
                }
                catch (ActivityNotFoundException)
                {
                    _permissaoPara = null;
                    Avisar("Abra as configurações do Android para permitir a instalação pelo Streemu.");
                }
            })!
            .SetNegativeButton("Cancelar", (_, _) => RecusarInstalacao(nova))!
            .SetOnCancelListener(new AoCancelar(() => RecusarInstalacao(nova)))!
            .Show();
    }

    static void RecusarInstalacao(VersaoDisponivel nova)
    {
        ApagarApk();
        Evento(VersaoInstalada, nova.Versao, "recusou", "permissao");
        Mudou?.Invoke();
    }

    sealed class AoCancelar(Action cancelar) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => cancelar();
    }

    static void Instalar(VersaoDisponivel nova)
    {
        _preparando = true;
        var instalador = _app.PackageManager!.PackageInstaller!;
        Executar(ct =>
        {
            using var parametros = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            parametros.SetAppPackageName(_app.PackageName);
            parametros.SetSize(nova.Tamanho);
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) parametros.SetRequireUserAction((int)PackageInstallUserAction.Required);
            int id = instalador.CreateSession(parametros);
            try
            {
                using var sessao = instalador.OpenSession(id);
                using var apk = File.OpenRead(Caminho);
                using var destino = sessao.OpenWrite("base.apk", 0, nova.Tamanho);
                Copiar(apk, destino, nova.Tamanho, ct);
                sessao.Fsync(destino);
                return id;
            }
            catch
            {
                Abandonar(id);
                throw;
            }
        }, (id, erro, cancelada) =>
        {
            _preparando = false;
            if (erro != null || cancelada || EmLive || _tela == null || Disponivel != nova)
            {
                if (erro == null) Abandonar(id);
                if (erro is not null and not OperationCanceledException)
                    FalhaInstalacao(nova, "preparar");
                return;
            }

            try
            {
                using var sessao = instalador.OpenSession(id);
                using var intent = new Intent(_app, typeof(ResultadoDaInstalacao))
                    .PutExtra("de", VersaoInstalada).PutExtra("para", nova.Versao).PutExtra("sessao", id);
                var flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : (PendingIntentFlags)0;
                using var retorno = PendingIntent.GetBroadcast(_app, id, intent, flags)!;
                _sessao = id;
                sessao.Commit(retorno.IntentSender!);
                // O Android já tem a sua própria cópia na sessão.
                ApagarApk();
            }
            catch (Exception)
            {
                _sessao = -1;
                Abandonar(id);
                FalhaInstalacao(nova, "confirmar");
            }
        }, TimeSpan.FromMinutes(2));
    }

    static void FalhaInstalacao(VersaoDisponivel nova, string motivo)
    {
        ApagarApk();
        Evento(VersaoInstalada, nova.Versao, "falhou", motivo);
        Avisar("Não foi possível iniciar a instalação.");
    }

    public static void ReceberResultado(Context c, Intent intent)
    {
        int id = intent.GetIntExtra("sessao", -1);
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
        if (id < 0 || intent.GetStringExtra("de") is not { } de || intent.GetStringExtra("para") is not { } para) return;
        if (status == PackageInstallStatus.PendingUserAction)
        {
            if (_sessao != id || EmLive)
            {
                try { c.PackageManager!.PackageInstaller!.AbandonSession(id); }
                catch (Java.Lang.Exception) { }
                return;
            }
            if (_confirmacaoAberta || _confirmacao != null) return;
#pragma warning disable CA1422
            _confirmacao = intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422
            if (_confirmacao == null) Abandonar(id);
            else TentarConfirmacao();
            return;
        }

        var prefs = Preferencias(c);
        if (prefs.GetInt("sessao_concluida", -1) == id) return;
        string resultado = status switch
        {
            PackageInstallStatus.Success => "instalou",
            PackageInstallStatus.FailureAborted => "recusou",
            _ => "falhou"
        };
        string? motivo = status == PackageInstallStatus.Success ? null : $"android_{(int)status}";
        if (_sessao == id)
        {
            _sessao = -1;
            LimparConfirmacao();
        }
        var editor = prefs.Edit()!.PutInt("sessao_concluida", id)!;
        if (!Sessao.Iniciada) editor.PutString("resultado", Campos(de, para, resultado, motivo).ToJsonString());
        editor.Apply();
        if (Sessao.Iniciada) Evento(de, para, resultado, motivo);
        if (resultado == "falhou") Avisar("O Android não conseguiu instalar a atualização.");
        Mudou?.Invoke();
        Verificar();
    }

    static void TentarConfirmacao()
    {
        if (EmLive || _tela is not { } tela || _confirmacao is not { } confirmacao) return;
        _confirmacao = null;
        try
        {
            _confirmacaoAberta = true;
            tela.StartActivity(confirmacao);
        }
        catch (ActivityNotFoundException) { Abandonar(_sessao); }
        finally { confirmacao.Dispose(); }
    }

    static void LimparConfirmacao()
    {
        _confirmacaoAberta = false;
        _confirmacao?.Dispose();
        _confirmacao = null;
    }

    static ISharedPreferences Preferencias(Context c) => c.GetSharedPreferences(VersaoDisponivel.NomeDasPreferencias, FileCreationMode.Private)!;

    public static void RegistrarResultadoPendente()
    {
        if (_prefs.GetString("resultado", null) is not { } json) return;
        _prefs.Edit()!.Remove("resultado")!.Apply();
        Telemetria.Evento("atualizacao", JsonNode.Parse(json)?.AsObject());
    }

    static JsonObject Campos(string de, string para, string resultado, string? motivo) =>
        new() { ["de"] = de, ["para"] = para, ["resultado"] = resultado, ["motivo"] = motivo };

    static void Evento(string de, string para, string resultado, string? motivo) =>
        Telemetria.Evento("atualizacao", Campos(de, para, resultado, motivo));

    static void Avisar(string texto)
    {
        if (_tela is { } tela) Toast.MakeText(tela, texto, ToastLength.Long)?.Show();
    }

    static long Codigo(PackageInfo info)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) return info.LongVersionCode;
#pragma warning disable CS0618, CA1422
        return info.VersionCode;
#pragma warning restore CS0618, CA1422
    }

    static void ConferirApk(VersaoDisponivel nova)
    {
        var pm = _app.PackageManager!;
#pragma warning disable CS0618, CA1422
        var flags = OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
        using var apk = pm.GetPackageArchiveInfo(Caminho, flags);
        using var atual = pm.GetPackageInfo(_app.PackageName!, flags)!;
        if (apk == null || apk.PackageName != atual.PackageName || Codigo(apk) != nova.Codigo || apk.VersionName != nova.Versao)
            throw new InvalidDataException("pacote_ou_versao");
        IList<Signature>? Assinaturas(PackageInfo info) => OperatingSystem.IsAndroidVersionAtLeast(28)
            ? info.SigningInfo?.GetApkContentsSigners() : info.Signatures;
#pragma warning restore CS0618, CA1422
        var confiaveis = Assinaturas(atual);
        var recebidas = Assinaturas(apk);
        if (confiaveis is not { Count: > 0 } || recebidas?.Count != confiaveis.Count ||
            !recebidas.All(s => confiaveis.Any(a => a.Equals(s))))
            throw new InvalidDataException("assinatura");
    }

    static string Motivo(Exception erro) => erro is InvalidDataException ? erro.Message : "download";

    static HttpResponseMessage Buscar(Uri url, CancellationToken ct)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Get, url);
        var resposta = Http.SendAsync(pedido, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (resposta.IsSuccessStatusCode) return resposta;
        resposta.Dispose();
        throw new HttpRequestException("O GitHub não retornou o arquivo solicitado.");
    }

    static long Copiar(Stream origem, Stream destino, long limite, CancellationToken ct,
        Action? conferirRede = null, IncrementalHash? hash = null)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        long proximaConferencia = Environment.TickCount64;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (conferirRede != null && Environment.TickCount64 >= proximaConferencia)
            {
                conferirRede();
                proximaConferencia = Environment.TickCount64 + 1000;
            }
            int n = origem.ReadAsync(buffer.AsMemory(), ct).AsTask().GetAwaiter().GetResult();
            if (n == 0) return total;
            total += n;
            if (total > limite) throw new InvalidDataException("tamanho");
            destino.Write(buffer, 0, n);
            hash?.AppendData(buffer, 0, n);
        }
    }

    static void Abandonar(int id)
    {
        try { _app.PackageManager!.PackageInstaller!.AbandonSession(id); }
        catch (Java.Lang.Exception) { }
    }

    static void ApagarApk()
    {
        _baixado = false;
        try { File.Delete(Caminho); }
        catch (IOException) { }
    }

    static void Executar<T>(Func<CancellationToken, T> executar, Action<T, Exception?, bool> concluir, TimeSpan limite)
    {
        var op = new CancellationTokenSource(limite);
        _operacao = op;
        Mudou?.Invoke();
        new Thread(() =>
        {
            T resultado = default!;
            Exception? erro = null;
            try
            {
                Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background);
                resultado = executar(op.Token);
            }
            catch (Exception e) { erro = e; }
            Ui.Post(() =>
            {
                bool cancelada = op.IsCancellationRequested;
                op.Dispose();
                _operacao = null;
                concluir(resultado, erro, cancelada);
                Mudou?.Invoke();
                Verificar();
                TentarNoWifi();
            });
        }) { IsBackground = true, Name = "Streemu Atualização" }.Start();
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ResultadoDaInstalacao : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context != null && intent != null) Atualizacao.ReceberResultado(context, intent);
    }
}
