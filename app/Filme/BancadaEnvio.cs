#if DEBUG
using System.Diagnostics;
using Android.Content;
using Streemu.Filme;
using Streemu.Net;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// Só Debug. Bancada do envio do filme em cópia direta, sem ninguém para assistir (spec 028, F5): lê o arquivo de verdade e manda para N espectadores simulados por SRT em loopback, com
/// a posição da Sessão andando em tempo real, e escreve no logcat (tag Streemu, linhas <c>BANCADA</c>) a velocidade de leitura, o adiantado, os descartados e as épocas. Serve para saber, no
/// celular, se a fonte dá conta de alimentar N espectadores em tempo real antes de testar com gente (caso de 22/11: ~0,37x com 3 espectadores). Com o player tocando junto (carga de decodificação do
/// anfitrião) fica mais perto do uso real. Quem chama: <see cref="TesteEnvioActivity"/>.
/// </summary>
static class BancadaEnvio
{
    public static void Rodar(Context contexto, string arquivo, double posS, int espectadores, int segundos, CancellationToken ct)
    {
        var uri = Android.Net.Uri.FromFile(new Java.IO.File(arquivo))!;
        var faixas = SondaFilme.Sondar(contexto, uri, "bancada");
        if (faixas == null) { Logcat.Error("Streemu", "BANCADA o arquivo não vai em cópia direta (ver FILME cópia direta indisponível)"); return; }
        Logcat.Info("Streemu", $"BANCADA {faixas.Info.Resumo}; áudio {faixas.Audio?.Idioma} ({faixas.Audio?.NomeCodec}); {faixas.Audios.Count} faixa(s) de áudio; posição {posS:0.#} s; {espectadores} espectador(es); {segundos} s.");
        foreach (var f in faixas.Audios) Logcat.Info("Streemu", $"BANCADA faixa de áudio #{f.Indice}: {f.Mime} {f.Canais} canais, idioma \"{f.Idioma}\", número {f.TrackId ?? "?"}, {(f.PodeCopiar ? "vai em cópia" : "não vai")}{(f.Indice == faixas.Escolhida ? " <- escolhida" : "")}.");
        LeituraPura(contexto, uri, faixas, posS, Math.Min(segundos, 20));
        if (espectadores > 0 && !ct.IsCancellationRequested) Pipeline(contexto, uri, faixas, posS, espectadores, segundos, ct);
        Logcat.Info("Streemu", "BANCADA fim.");
    }

    /// <summary>A fonte sozinha, sem ritmo nem SRT: o teto de quanto o celular lê e monta por segundo.</summary>
    static void LeituraPura(Context contexto, Android.Net.Uri uri, FaixasDoFilme faixas, double posS, int segundos)
    {
        using var fonte = new FonteExtrator(contexto, uri, faixas);
        var relogio = Stopwatch.StartNew();
        fonte.Abrir(posS);
        long abrirMs = relogio.ElapsedMilliseconds;
        double ptsMax = posS; long bytes = 0; int blocos = 0;
        var leitura = Stopwatch.StartNew();
        while (leitura.Elapsed.TotalSeconds < segundos && fonte.Proximo() is { } b)
        {
            blocos++; bytes += b.Dados.Length;
            if (b.Video) ptsMax = Math.Max(ptsMax, b.PtsS);
        }
        double t = Math.Max(0.001, leitura.Elapsed.TotalSeconds), lido = ptsMax - posS;
        Logcat.Info("Streemu", FormattableString.Invariant($"BANCADA leitura pura: abrir {abrirMs} ms; {lido:0.#} s de filme em {t:0.#} s = {lido / t:0.0}x o tempo real; {bytes * 8 / 1e6 / t:0.0} Mbit/s; {blocos} blocos."));
    }

    /// <summary>O envio inteiro: épocas, ritmo e fila de cada espectador, com a posição da Sessão andando como o app faria (a cada 400 ms).</summary>
    static void Pipeline(Context contexto, Android.Net.Uri uri, FaixasDoFilme faixas, double posS, int espectadores, int segundos, CancellationToken ct)
    {
        Srt.srt_startup();
        using var envio = new EnvioFilmeCopia(faixas.Info, () => new FonteExtrator(contexto, uri, faixas)) { UploadMbps = 30 };   // o mesmo upload suposto do app (CopiaDoFilme)
        var locais = Enumerable.Range(0, espectadores).Select(i => new EspectadorLocal(envio, envio.Adicionar("b" + i, "esp" + i)!)).ToList();
        foreach (var l in locais) l.Iniciar();
        var relogio = Stopwatch.StartNew();
        double ultimoRelatorio = 0; long bytesAntes = 0;
        while (relogio.Elapsed.TotalSeconds < segundos && !ct.IsCancellationRequested)
        {
            envio.Seguir(posS + relogio.Elapsed.TotalSeconds, true);
            if (relogio.Elapsed.TotalSeconds - ultimoRelatorio >= 2) { Relatorio(envio, locais, relogio.Elapsed.TotalSeconds, ref ultimoRelatorio, ref bytesAntes); }
            Thread.Sleep(400);
        }
        Resumo(envio, locais, relogio.Elapsed.TotalSeconds);
        foreach (var l in locais) l.Dispose();
    }

    static void Relatorio(EnvioFilmeCopia envio, List<EspectadorLocal> locais, double t, ref double ultimo, ref long bytesAntes)
    {
        long bytes = locais.Sum(l => Interlocked.Read(ref l.Saida.BytesEnviados));
        double mbps = (bytes - bytesAntes) * 8 / Math.Max(0.001, t - ultimo) / 1e6; bytesAntes = bytes; ultimo = t;
        Logcat.Info("Streemu", FormattableString.Invariant(
            $"BANCADA t={t:0}s época {envio.Epoca} leitura {envio.VelocidadeDeLeitura:0.00}x adiantado {envio.Adiantado:0.#} s; enviando {mbps:0.0} Mbit/s no total; descartados {locais.Sum(l => Interlocked.Read(ref l.Saida.BlocosDescartados))}; falhas {locais.Sum(l => l.Falhas)}; fila {locais.Sum(l => l.Saida.BytesNaFila) / 1024} KB; recebido {locais.Sum(l => l.Recebido) / 1_000_000} MB."));
    }

    static void Resumo(EnvioFilmeCopia envio, List<EspectadorLocal> locais, double t) =>
        Logcat.Info("Streemu", FormattableString.Invariant(
            $"BANCADA resumo: {t:0} s, {envio.Epoca} época(s), {locais.Sum(l => Interlocked.Read(ref l.Saida.BlocosDescartados))} bloco(s) descartado(s), {locais.Sum(l => l.Falhas)} falha(s) de envio, {locais.Sum(l => l.Recebido) / 1_000_000} MB recebidos por {locais.Count} espectador(es)."));
}

/// <summary>Um espectador de mentira: o lado que envia (como o <see cref="SaidaSrtFilme"/>, sem ligação de rede) e um receptor SRT em loopback que só conta os bytes.</summary>
sealed class EspectadorLocal : IDisposable
{
    const string Senha = "bancada-do-envio-1234";
    readonly EnvioFilmeCopia _envio;
    volatile bool _fim;
    public SaidaFilme Saida { get; }
    public long Recebido, Falhas;

    public EspectadorLocal(EnvioFilmeCopia envio, SaidaFilme saida) { _envio = envio; Saida = saida; }

    public void Iniciar() => new Thread(Enviar) { IsBackground = true, Name = "bancada-envio-" + Saida.Apelido }.Start();

    void Enviar()
    {
        int porta = LocalRelay.FreeUdpPort();
        int escuta = Srt.Escutar(porta, Senha, EnvioFilmeCopia.LatenciaMs), u = Srt.Erro;
        new Thread(() => Receber(porta)) { IsBackground = true, Name = "bancada-recebe-" + Saida.Apelido }.Start();
        try
        {
            while (!_fim && u == Srt.Erro) u = Srt.Aceitar(escuta, 250);
            int epoca = 0;
            while (!_fim && u != Srt.Erro)
            {
                if (_envio.Epoca != epoca && _envio.Epoca > 0) { epoca = _envio.Epoca; _envio.Conectar(Saida); }   // a cada época, como o SaidaSrtFilme: recomeça no último GOP
                if (Saida.Proximo(250) is { } bloco && !Despachar(u, bloco)) Saida.PerderAteChave();
            }
        }
        catch (Exception e) { Logcat.Warn("Streemu", "BANCADA envio " + Saida.Apelido + ": " + e.Message); }
        finally { _envio.Desconectar(Saida); if (u != Srt.Erro) Srt.srt_close(u); Srt.srt_close(escuta); }
    }

    bool Despachar(int u, byte[] bloco)
    {
        for (int off = 0; off < bloco.Length; off += 1316)
        {
            int n = Math.Min(1316, bloco.Length - off);
            if (!Srt.Enviar(u, bloco, off, n)) { Interlocked.Increment(ref Falhas); return false; }
            Interlocked.Add(ref Saida.BytesEnviados, n);
        }
        return true;
    }

    void Receber(int porta)
    {
        int c = Srt.Erro;
        try
        {
            c = Srt.Conectar(porta, Senha, EnvioFilmeCopia.LatenciaMs);
            var buf = new byte[1500];
            while (!_fim)
            {
                int n = Srt.srt_recvmsg(c, buf, buf.Length);
                if (n > 0) { Interlocked.Add(ref Recebido, n); continue; }
                int estado = Srt.srt_getsockstate(c);
                if (estado != Srt.SrtsConnected) { Logcat.Warn("Streemu", $"BANCADA recebe {Saida.Apelido} saiu: estado {estado}, recebeu {Recebido / 1000} KB, erro \"{Srt.UltimoErro}\"."); break; }
            }
        }
        catch (Exception e) { Logcat.Warn("Streemu", "BANCADA recebe " + Saida.Apelido + ": " + e.Message); }
        finally { if (c != Srt.Erro) Srt.srt_close(c); }
    }

    public void Dispose() => _fim = true;
}
#endif
