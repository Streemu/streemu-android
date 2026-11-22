using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Streemu.Net;

/// <summary>
/// Lacre das mensagens de controle do túnel (<see cref="Tunnel"/>, tipo TCtrlSelado). Até então o controle (JSON) ia em claro e sem autenticação,
/// e a tag de 4 bytes viaja em todo pacote: quem estivesse no caminho (Wi-Fi aberto, operadora) lia o controle e forjava comandos
/// (<c>filme_baixar</c>, <c>seen_as</c>...). Aqui o JSON vai cifrado e autenticado com AES-GCM, com a chave derivada dos dois segredos.
///
/// Payload: contador(8, little-endian) + cifrado + tag(16). Nonce (12) = lado(1) + 3 zeros + contador(8). O "lado" (0 ou 1) vem de quem é o
/// primeiro na ordem dos segredos, então os dois sentidos nunca repetem nonce com a mesma chave. O AAD é o cabeçalho do túnel (7 bytes) + o
/// contador: o tipo e a tag do pacote ficam amarrados ao conteúdo. O contador só cresce; quem recebe recusa repetido ou velho demais
/// (anti-replay), porque um atacante que grava um pacote válido poderia reenviá-lo (ex.: repetir um "pausar").
/// </summary>
sealed class SeloControle : IDisposable
{
    public const int Contador = 8, Etiqueta = 16;
    /// <summary>Quantos contadores atrás do maior já visto ainda são aceitos (pacotes fora de ordem na rede).</summary>
    const int Janela = 1024;

    readonly AesGcm _aes;
    readonly byte[] _chave;
    readonly byte _meuLado, _ladoDele;
    // O AesGcm do .NET não promete ser seguro entre threads; o controle é pouco volume, então uma trava simples.
    readonly object _cripto = new();
    long _tx;
    readonly object _rxGate = new();
    ulong _maior;
    readonly HashSet<ulong> _vistos = new();
    readonly Queue<ulong> _ordem = new();

    public SeloControle(byte[] chave, bool souPrimeiro)
    {
        _chave = chave;
        _aes = new AesGcm(chave, Etiqueta);
        _meuLado = (byte)(souPrimeiro ? 0 : 1);
        _ladoDele = (byte)(1 - _meuLado);
    }

    public bool MesmaChave(byte[] chave, bool souPrimeiro) => _chave.AsSpan().SequenceEqual(chave) && _meuLado == (souPrimeiro ? 0 : 1);

    static void Nonce(Span<byte> nonce, byte lado, ulong contador)
    {
        nonce.Clear();
        nonce[0] = lado;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], contador);
    }

    /// <summary>Monta o pacote inteiro (cabeçalho do túnel já com a tag + payload lacrado). Cada chamada usa um contador novo.</summary>
    public byte[] Selar(byte tipo, ReadOnlySpan<byte> tag4, ReadOnlySpan<byte> claro)
    {
        const int Cab = 7;
        var pkt = new byte[Cab + Contador + claro.Length + Etiqueta];
        pkt[0] = (byte)'L'; pkt[1] = (byte)'X'; pkt[2] = tipo;
        tag4.CopyTo(pkt.AsSpan(3));
        lock (_cripto)
        {
            ulong c = (ulong)Interlocked.Increment(ref _tx);
            BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(Cab), c);
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _meuLado, c);
            _aes.Encrypt(nonce, claro, pkt.AsSpan(Cab + Contador, claro.Length), pkt.AsSpan(Cab + Contador + claro.Length, Etiqueta), pkt.AsSpan(0, Cab + Contador));
        }
        return pkt;
    }

    /// <summary>
    /// Abre um pacote inteiro recebido (cabeçalho de 7 bytes + payload). Falso se não autentica, se o contador já foi visto ou se é velho
    /// demais. O anti-replay só entra DEPOIS de abrir: pacote forjado nunca gasta a janela.
    /// </summary>
    public bool Abrir(ReadOnlySpan<byte> pkt, out byte[] claro)
    {
        const int Cab = 7;
        claro = Array.Empty<byte>();
        int n = pkt.Length - Cab - Contador - Etiqueta;
        // o teto é o do UDP, não 4 KB: a fila de episódios e a lista de pessoas do Cinema passam disso com uma série grande
        if (n < 0 || n > 65000) return false;
        ulong c = BinaryPrimitives.ReadUInt64LittleEndian(pkt[Cab..]);
        if (c == 0) return false;
        var saida = new byte[n];
        try
        {
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _ladoDele, c);
            lock (_cripto) _aes.Decrypt(nonce, pkt.Slice(Cab + Contador, n), pkt.Slice(Cab + Contador + n, Etiqueta), saida, pkt[..(Cab + Contador)]);
        }
        catch (CryptographicException) { return false; }
        catch (ObjectDisposedException) { return false; }
        lock (_rxGate)
        {
            if (c + Janela <= _maior || _vistos.Contains(c)) return false;
            _vistos.Add(c);
            _ordem.Enqueue(c);
            if (c > _maior) _maior = c;
            // a fila não é ordenada (pacote fora de ordem), então limpa pelo valor: o que saiu da janela deixa de importar
            while (_ordem.Count > 0 && _ordem.Peek() + Janela <= _maior) _vistos.Remove(_ordem.Dequeue());
            while (_ordem.Count > 2 * Janela) _vistos.Remove(_ordem.Dequeue()); // teto de memória
        }
        claro = saida;
        return true;
    }

    public void Dispose()
    {
        lock (_cripto) _aes.Dispose();
    }
}
