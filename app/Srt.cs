using System.Runtime.InteropServices;
using System.Text;

namespace Streemu.Proto;

/// <summary>
/// O pouco do libsrt (nativo/compilar-srt.ps1) que o player usa: um socket "caller" em modo live, com senha (AES-128), ligado ao LocalRelay.
/// Os números das opções vêm do srt.h da v1.5.7 (enum SRT_SOCKOPT).
/// </summary>
static class Srt
{
    const string Lib = "srt";
    public const int Erro = -1;

    const int SRTO_RCVTIMEO = 14, SRTO_LATENCY = 23, SRTO_PASSPHRASE = 26, SRTO_PBKEYLEN = 27, SRTO_CONNTIMEO = 36, SRTO_TRANSTYPE = 50;
    const int SRTT_LIVE = 0;

    [DllImport(Lib)] public static extern int srt_startup();
    [DllImport(Lib)] public static extern int srt_create_socket();
    [DllImport(Lib)] public static extern int srt_close(int u);
    [DllImport(Lib)] static extern int srt_setsockflag(int u, int opt, ref int valor, int tamanho);
    [DllImport(Lib, EntryPoint = "srt_setsockflag")] static extern int srt_setsockflag_bytes(int u, int opt, byte[] valor, int tamanho);
    [DllImport(Lib)] static extern int srt_connect(int u, byte[] endereco, int tamanho);
    [DllImport(Lib)] public static extern int srt_recvmsg(int u, byte[] buf, int tamanho);
    [DllImport(Lib)] static extern IntPtr srt_getlasterror_str();

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

    /// <summary>struct sockaddr_in do Linux para 127.0.0.1:porta: família (2, ordem do processador), porta (ordem de rede), endereço, 8 zeros.</summary>
    static byte[] SockaddrLocal(int porta) => new byte[] { 2, 0, (byte)(porta >> 8), (byte)porta, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
}
