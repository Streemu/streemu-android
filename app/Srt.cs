using System.Runtime.InteropServices;
using System.Text;

namespace Streemu.Proto;

/// <summary>
/// O pouco do libsrt (nativo/compilar-srt.ps1) que o app usa: o "caller" do player e o "listener" de quem transmite (Anfitriao), em modo live, com senha (AES-128), ligados ao LocalRelay.
/// Os números das opções vêm do srt.h da v1.5.7 (enum SRT_SOCKOPT).
/// </summary>
static class Srt
{
    const string Lib = "srt";
    public const int Erro = -1;

    const int SRTO_RCVTIMEO = 14, SRTO_LATENCY = 23, SRTO_PASSPHRASE = 26, SRTO_PBKEYLEN = 27, SRTO_CONNTIMEO = 36, SRTO_TRANSTYPE = 50;
    const int SRTT_LIVE = 0;
    // só do envio (enum SRT_SOCKOPT da v1.5.7)
    const int SRTO_SNDSYN = 1, SRTO_RCVSYN = 2, SRTO_LINGER = 7, SRTO_SNDTIMEO = 13, SRTO_PAYLOADSIZE = 49;
    // SRT_ERRNO: SRT_EASYNCSND, SRT_EASYNCRCV, SRT_ETIMEOUT
    const int EAsyncSnd = 6001, EAsyncRcv = 6002, ETimeout = 6003;
    /// <summary>SRT_SOCKSTATUS: SRTS_CONNECTED. Os depois dele (broken, closing, closed, nonexist) são ligações mortas.</summary>
    public const int SrtsConnected = 5;

    [DllImport(Lib)] public static extern int srt_startup();
    [DllImport(Lib)] public static extern int srt_create_socket();
    [DllImport(Lib)] public static extern int srt_close(int u);
    [DllImport(Lib)] static extern int srt_setsockflag(int u, int opt, ref int valor, int tamanho);
    [DllImport(Lib, EntryPoint = "srt_setsockflag")] static extern int srt_setsockflag_bytes(int u, int opt, byte[] valor, int tamanho);
    [DllImport(Lib)] static extern int srt_connect(int u, byte[] endereco, int tamanho);
    [DllImport(Lib)] public static extern int srt_recvmsg(int u, byte[] buf, int tamanho);
    [DllImport(Lib)] static extern IntPtr srt_getlasterror_str();
    [DllImport(Lib)] static extern int srt_getlasterror(ref int errnoLoc);
    [DllImport(Lib)] static extern int srt_bind(int u, byte[] endereco, int tamanho);
    [DllImport(Lib)] static extern int srt_listen(int u, int fila);
    [DllImport(Lib)] static extern int srt_accept(int u, byte[] endereco, ref int tamanho);
    [DllImport(Lib)] static extern int srt_sendmsg(int u, ref byte buf, int tamanho, int ttl, int naOrdem);
    [DllImport(Lib)] public static extern int srt_getsockstate(int u);
    [DllImport(Lib)] static extern int srt_bstats(int u, byte[] perf, int limpar);

    public static string UltimoErro => Marshal.PtrToStringUTF8(srt_getlasterror_str()) ?? "?";

    static void Opcao(int u, int opt, int valor, string nome)
    {
        if (srt_setsockflag(u, opt, ref valor, sizeof(int)) == Erro) throw new InvalidOperationException($"SRT {nome}: {UltimoErro}");
    }

    /// <summary>Abre e conecta, como o ffplay do app: srt://127.0.0.1:porta?mode=caller&amp;latency=…&amp;passphrase=…&amp;pbkeylen=16&amp;transtype=live.</summary>
    public static int Conectar(int porta, string senha, int latenciaMs)
    {
        int u = srt_create_socket();
        if (u == Erro) throw new InvalidOperationException("SRT create_socket: " + UltimoErro);
        try
        {
            Opcao(u, SRTO_TRANSTYPE, SRTT_LIVE, "transtype");   // primeiro: ele redefine as outras opções para o padrão do modo live
            Opcao(u, SRTO_LATENCY, latenciaMs, "latency");
            Opcao(u, SRTO_PBKEYLEN, 16, "pbkeylen");
            var s = Encoding.ASCII.GetBytes(senha);
            if (srt_setsockflag_bytes(u, SRTO_PASSPHRASE, s, s.Length) == Erro) throw new InvalidOperationException("SRT passphrase: " + UltimoErro);
            Opcao(u, SRTO_CONNTIMEO, 10000, "conntimeo");
            Opcao(u, SRTO_RCVTIMEO, 3000, "rcvtimeo");   // o laço de leitura acorda para ver se é hora de parar
            if (srt_connect(u, SockaddrLocal(porta), 16) == Erro) throw new InvalidOperationException("SRT connect: " + UltimoErro);
            return u;
        }
        catch { srt_close(u); throw; }
    }

    /// <summary>
    /// O "listener" do envio (o que o ffmpeg do app Windows faz com srt://127.0.0.1:porta?mode=listener&amp;latency=80000&amp;pbkeylen=16&amp;passphrase=…&amp;pkt_size=1316):
    /// as mesmas opções do caller, ligado em 127.0.0.1:porta (a porta que o LocalRelay do espectador usa como FixedTarget).
    /// O accept é sem espera (RCVSYN falso): <see cref="Aceitar"/> sonda. Mais simples e mais seguro que fechar o socket de outra thread para destravar.
    /// </summary>
    public static int Escutar(int porta, string senha, int latenciaMs)
    {
        int u = srt_create_socket();
        if (u == Erro) throw new InvalidOperationException("SRT create_socket: " + UltimoErro);
        try
        {
            Opcao(u, SRTO_TRANSTYPE, SRTT_LIVE, "transtype");   // primeiro, como no caller
            Opcao(u, SRTO_LATENCY, latenciaMs, "latency");
            Opcao(u, SRTO_PBKEYLEN, 16, "pbkeylen");
            Opcao(u, SRTO_PAYLOADSIZE, 1316, "payloadsize");   // pkt_size=1316 do ffmpeg: 7 pacotes TS por datagrama
            var s = Encoding.ASCII.GetBytes(senha);
            if (srt_setsockflag_bytes(u, SRTO_PASSPHRASE, s, s.Length) == Erro) throw new InvalidOperationException("SRT passphrase: " + UltimoErro);
            Opcao(u, SRTO_RCVSYN, 0, "rcvsyn");   // no listener vale para o accept: sem espera
            if (srt_bind(u, SockaddrLocal(porta), 16) == Erro) throw new InvalidOperationException("SRT bind: " + UltimoErro);
            if (srt_listen(u, 1) == Erro) throw new InvalidOperationException("SRT listen: " + UltimoErro);
            return u;
        }
        catch { srt_close(u); throw; }
    }

    /// <summary>Espera um espectador conectar no listener, até <paramref name="esperaMs"/>. Devolve o socket da ligação, ou <see cref="Erro"/> se ninguém chegou.</summary>
    public static int Aceitar(int escuta, int esperaMs)
    {
        var fim = Environment.TickCount64 + esperaMs;
        var endereco = new byte[28];   // cabe o sockaddr_in6; o do 127.0.0.1 usa 16
        while (true)
        {
            int tam = endereco.Length;
            int u = srt_accept(escuta, endereco, ref tam);
            if (u != Erro) { Configurar(u); return u; }
            int e = 0;
            int codigo = srt_getlasterror(ref e);
            if (codigo != EAsyncRcv && codigo != EAsyncSnd && codigo != ETimeout) throw new InvalidOperationException("SRT accept: " + UltimoErro);
            if (Environment.TickCount64 >= fim) return Erro;
            Thread.Sleep(25);
        }
    }

    /// <summary>Opções da ligação aceita: o envio nunca espera mais que o timeout (quem atrasa perde, a fila do espectador resolve; a captura nem vê).</summary>
    static void Configurar(int u)
    {
        Opcao(u, SRTO_SNDSYN, 1, "sndsyn");
        Opcao(u, SRTO_SNDTIMEO, 150, "sndtimeo");
        Opcao(u, SRTO_RCVSYN, 1, "rcvsyn");
        srt_setsockflag_bytes(u, SRTO_LINGER, new byte[8], 8);   // struct linger zerada: fechar não espera esvaziar (o espectador saiu). Se o libsrt recusar, o padrão do modo live vale
    }

    /// <summary>
    /// Manda um datagrama (até 1316 bytes). Verdadeiro se entrou na fila do SRT; falso se a fila está cheia há mais de 150 ms (tente descartar).
    /// Lança se a ligação morreu (o espectador fechou ou sumiu): quem chama volta a escutar.
    /// </summary>
    public static bool Enviar(int u, byte[] dados, int inicio, int tamanho)
    {
        int n = srt_sendmsg(u, ref dados[inicio], tamanho, -1, 0);
        if (n != Erro) return true;
        int e = 0;
        int codigo = srt_getlasterror(ref e);
        if (codigo == EAsyncSnd || codigo == ETimeout) return false;
        throw new InvalidOperationException("SRT send: " + UltimoErro);
    }

    // ---- estatísticas (struct CBytePerfMon = SRT_TRACEBSTATS do srt.h da v1.5.7, em srtcore/srt.h) ----
    // A struct tem 496 bytes e não é espelhada em C#: srt_bstats escreve num buffer de 1024 bytes e só lemos os campos abaixo, pelos offsets.
    // Os offsets foram conferidos compilando o trecho da struct com gcc (offsetof; sizeof = 496). Em armeabi-v7a (AAPCS) e arm64 o int64_t e o double
    // alinham em 8 bytes, então os offsets são os mesmos nas duas ABIs. Se a libsrt for atualizada, reconferir (os campos novos entram no fim da struct).
    const int OffPktRecv = 144, OffPktRcvLoss = 156, OffPktRcvRetrans = 164, OffPktRcvBelated = 224, OffPktRcvDrop = 236,
              OffMsRtt = 328, OffPktRcvBuf = 380, OffMsRcvBuf = 388, OffMsRcvTsbPdDelay = 392;

    /// <summary>Números do SRT no intervalo desde a leitura anterior (os "local" do srt_bstats com clear) e os instantâneos.</summary>
    public readonly record struct Estatisticas(long Recebidos, int Perdidos, int Retransmitidos, int Descartados, long Belated,
        double RttMs, int LatenciaMs, int BufferMs, int BufferPacotes);

    /// <summary>Lê (e zera) as estatísticas do intervalo. Falso se o socket não existe mais.</summary>
    public static bool LerEstatisticas(int u, out Estatisticas e)
    {
        e = default;
        var b = new byte[1024];
        if (u == Erro || srt_bstats(u, b, 1) == Erro) return false;
        e = new Estatisticas(
            BitConverter.ToInt64(b, OffPktRecv), BitConverter.ToInt32(b, OffPktRcvLoss), BitConverter.ToInt32(b, OffPktRcvRetrans),
            BitConverter.ToInt32(b, OffPktRcvDrop), BitConverter.ToInt64(b, OffPktRcvBelated), BitConverter.ToDouble(b, OffMsRtt),
            BitConverter.ToInt32(b, OffMsRcvTsbPdDelay), BitConverter.ToInt32(b, OffMsRcvBuf), BitConverter.ToInt32(b, OffPktRcvBuf));
        return true;
    }

    /// <summary>struct sockaddr_in do Linux para 127.0.0.1:porta: família (2, ordem do processador), porta (ordem de rede), endereço, 8 zeros.</summary>
    static byte[] SockaddrLocal(int porta) => new byte[] { 2, 0, (byte)(porta >> 8), (byte)porta, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
}
