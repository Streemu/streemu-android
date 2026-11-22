using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// A thread que lê o SRT sem parar e escreve no <see cref="CacheTs"/> (spec 028, 5.1): caller em 127.0.0.1 na porta do LocalRelay da ligação, latência de 150 ms como o
/// Windows (<c>EnvioStream.LatenciaMs</c>). O SRT em modo live descarta o que não é lido a tempo; esta thread só lê (nada de decodificar aqui), então o player pode ficar parado.
/// Cada época do anfitrião é uma conexão nova: um leitor por época.
/// </summary>
public sealed class LeitorSrt : IDisposable
{
    public const int LatenciaMs = 150;
    static int _iniciou;

    readonly CacheTs _cache;
    readonly int _porta;
    readonly string _senha;
    readonly Thread _thread;
    volatile bool _parar;
    public long Bytes;
    public int Conectou;
    /// <summary>Quando o primeiro byte chegou (ms desde que o leitor abriu), para o log.</summary>
    public long PrimeiroByteMs = -1;

    public LeitorSrt(int porta, string senha, CacheTs cache)
    {
        if (Interlocked.Exchange(ref _iniciou, 1) == 0) Srt.srt_startup();
        _cache = cache; _porta = porta; _senha = senha;
        _thread = new Thread(Laco) { IsBackground = true, Name = "srt-filme" };
        _thread.Start();
    }

    void Laco()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int u = Srt.Erro;
        string? erro = null;
        try
        {
            u = Srt.Conectar(_porta, _senha, LatenciaMs);
            Conectou = (int)sw.ElapsedMilliseconds;
            var buf = new byte[2048];
            while (!_parar)
            {
                int n = Srt.srt_recvmsg(u, buf, buf.Length);
                if (n == Srt.Erro)
                {
                    string e = Srt.UltimoErro;
                    // a libsrt escreve "transmission timed out" (não "timeout"), como no PlayerActivity
                    if (e.Contains("timeout", StringComparison.OrdinalIgnoreCase) || e.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                        || e.Contains("again", StringComparison.OrdinalIgnoreCase)) { if (Srt.srt_getsockstate(u) == Srt.SrtsConnected) continue; }
                    erro = e;
                    break;
                }
                if (n == 0) continue;
                if (PrimeiroByteMs < 0) PrimeiroByteMs = sw.ElapsedMilliseconds;
                Bytes += n;
                _cache.Escrever(buf, n);
            }
        }
        catch (Exception e) { erro = e.Message; }
        finally
        {
            if (u != Srt.Erro) Srt.srt_close(u);
            _cache.Terminar(_parar ? null : erro ?? "o fluxo acabou");
            Logcat.Info("Streemu", $"FILME leitor SRT terminou: {(_parar ? "parado" : erro ?? "fim")}; {Bytes / 1024} KiB.");
        }
    }

    /// <summary>Para de ler (a thread fecha o SRT em até 3 s). O cache fica como está, para o player terminar de tocar o que já tem.</summary>
    public void Dispose() => _parar = true;
}
