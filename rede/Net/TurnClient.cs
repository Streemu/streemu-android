using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Cliente TURN mínimo (RFC 8656) sobre UDP, usando o MESMO socket do túnel.
/// Serve para receber: reservamos um endereço no servidor de relay ("relayed address"),
/// colocamos no código, e o amigo manda para lá; o servidor repassa para nós em Data indications.
/// Também sabe mandar pelo relay (Send indication) quando nem o caminho direto de saída funciona.
/// </summary>
/// <summary>Erro do servidor TURN com o código (ex.: 437) e uma explicação em português.</summary>
public sealed class TurnException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class TurnClient
{
    const ushort Allocate = 0x0003, Refresh = 0x0004, SendInd = 0x0016, DataInd = 0x0017, CreatePermission = 0x0008;
    const ushort AUsername = 0x0006, AMessageIntegrity = 0x0008, AErrorCode = 0x0009, ALifetime = 0x000D,
                 AXorPeer = 0x0012, AData = 0x0013, ARealm = 0x0014, ANonce = 0x0015, AXorRelayed = 0x0016,
                 ARequestedTransport = 0x0019, AXorMapped = 0x0020, ASoftware = 0x8022;

    readonly Action<byte[], IPEndPoint> _send;
    readonly Dictionary<string, TaskCompletionSource<byte[]>> _pending = new();
    readonly HashSet<string> _permitted = new();
    readonly object _gate = new();
    string _username = "", _password = "", _realm = "", _nonce = "";
    CancellationTokenSource? _cts;

    public IPEndPoint Server { get; }
    public IPEndPoint? Relayed { get; private set; }
    /// <summary>Pacote que um amigo mandou para o nosso endereço de relay.</summary>
    public Action<IPEndPoint, byte[]>? DataReceived;

    public TurnClient(IPEndPoint server, Action<byte[], IPEndPoint> send)
    {
        Server = server;
        _send = send;
    }

    public async Task<IPEndPoint> AllocateAsync(string username, string password)
    {
        _username = username; _password = password;
        // 1ª tentativa sem credencial: o servidor responde 401 com realm e nonce
        var r = await TransactAsync(Allocate, BuildAttrs(withAuth: false, a => a.Add((ARequestedTransport, new byte[] { 17, 0, 0, 0 }))));
        if (ErrorCode(r) is int e1 && e1 != 401) throw Refused(e1, r);
        ReadRealmNonce(r);
        Log.Info($"TURN: servidor pediu autenticação (realm \"{_realm}\", nonce de {_nonce.Length} caracteres).");
        bool triedFreeing = false;
        for (int i = 0; i < 3; i++)
        {
            r = await TransactAsync(Allocate, BuildAttrs(withAuth: true, a =>
            {
                a.Add((ARequestedTransport, new byte[] { 17, 0, 0, 0 }));
                a.Add((ALifetime, BE32(600)));
            }));
            int? err = ErrorCode(r);
            if (err == 438) { ReadRealmNonce(r); continue; } // nonce velho: tenta de novo
            if (err == 437 && !triedFreeing)
            {
                // Reserva presa da última abertura (mesmo IP:porta). Tenta liberar com LIFETIME 0; a Cloudflare só aceita
                // se a credencial for a mesma da reserva antiga, o que quase nunca é (a do Worker é nova a cada abertura).
                triedFreeing = true;
                var fr = await TransactAsync(Refresh, BuildAttrs(withAuth: true, a => a.Add((ALifetime, BE32(0)))), attempts: 2);
                if (ErrorCode(fr) == null) { Log.Info("TURN: reserva antiga liberada; reservando de novo."); continue; }
            }
            if (err != null) throw Refused(err.Value, r);
            Relayed = ReadXorAddress(r, AXorRelayed) ?? throw new InvalidOperationException("TURN não devolveu o endereço de relay");
            _cts = new CancellationTokenSource();
            _ = RefreshLoop(_cts.Token);
            return Relayed;
        }
        throw new InvalidOperationException("TURN: nonce rejeitado duas vezes");
    }

    /// <summary>Explica o erro com o motivo provável. 437 é o caso do teste 0.1.8 (não era credencial).</summary>
    static TurnException Refused(int code, byte[] r) => new(code, code switch
    {
        437 => "o relay ainda guarda a reserva da última vez que o app abriu (TURN erro 437); ela vence em até 10 min",
        401 or 441 => $"TURN recusou a credencial (erro {code} {ErrorReason(r)}); o servidor do grupo pode estar com a chave errada",
        486 => "a conta do relay atingiu o limite de reservas (TURN erro 486)",
        508 => "o relay está sem capacidade agora (TURN erro 508)",
        _ => $"TURN recusou a reserva (erro {code} {ErrorReason(r)})"
    });

    /// <summary>
    /// Libera a reserva no servidor (Refresh com LIFETIME 0). Sem isso, reabrir o app em menos de 10 min com a mesma
    /// porta dava erro 437 (a reserva antiga continuava presa ao mesmo IP:porta). Espera no máximo ~1,5 s.
    /// </summary>
    public bool Release()
    {
        _cts?.Cancel();
        if (Relayed == null) return false;
        bool released = false;
        try
        {
            var task = Task.Run(async () =>
            {
                var r = await TransactAsync(Refresh, BuildAttrs(withAuth: true, a => a.Add((ALifetime, BE32(0)))), attempts: 2, waitMs: 600);
                if (ErrorCode(r) == 438) { ReadRealmNonce(r); r = await TransactAsync(Refresh, BuildAttrs(withAuth: true, a => a.Add((ALifetime, BE32(0)))), attempts: 1, waitMs: 400); }
                return ErrorCode(r);
            });
            if (task.Wait(1600))
            {
                released = task.Result == null;
                Log.Info(released ? "TURN: reserva do relay liberada." : $"TURN: não consegui liberar a reserva (erro {task.Result}); ela vence sozinha em até 10 min.");
            }
            else Log.Info("TURN: o servidor não confirmou a liberação da reserva; ela vence sozinha em até 10 min.");
        }
        catch (Exception e) { Log.Info("TURN: falha ao liberar a reserva: " + e.GetBaseException().Message); }
        Relayed = null;
        return released;
    }

    /// <summary>Autoriza um IP a mandar para o nosso relay (o servidor descarta quem não tem permissão).</summary>
    public void Permit(IPAddress ip)
    {
        if (Relayed == null || ip.AddressFamily != AddressFamily.InterNetwork) return;
        // Teste 0.1.9: pedir permissão para 100.90.x.x (endereço interno da operadora) fazia a Cloudflare recusar o pedido
        // INTEIRO com 403 (todos os IPs vão juntos), inclusive o público do amigo. Endereço não público nunca passa pelo relay.
        if (!IsPublic(ip)) return;
        lock (_gate) { if (!_permitted.Add(ip.ToString())) return; }
        _ = SendPermissionsAsync();
    }

    /// <summary>IPv4 roteável na internet (fora de 10/8, 172.16/12, 192.168/16, 100.64/10 CGNAT, 127/8, 169.254/16, 0/8).</summary>
    static bool IsPublic(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                 || (b[0] == 169 && b[1] == 254));
    }

    async Task SendPermissionsAsync()
    {
        string[] ips;
        lock (_gate) ips = _permitted.ToArray();
        try
        {
            var r = await TransactAsync(CreatePermission, BuildAttrs(withAuth: true, a =>
            {
                foreach (var ip in ips) a.Add((AXorPeer, XorAddress(new IPEndPoint(IPAddress.Parse(ip), 0))));
            }));
            if (ErrorCode(r) == 438) { ReadRealmNonce(r); await SendPermissionsAsync(); return; }
            if (ErrorCode(r) is int err) Log.Info($"TURN: permissão recusada (erro {err}).");
            else Log.Info($"TURN: {ips.Length} endereço(s) autorizado(s) a mandar para o seu relay.");
        }
        catch (Exception e) { Log.Info("TURN: falha ao autorizar: " + e.Message); }
    }

    async Task RefreshLoop(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
            tick++;
            try
            {
                if (tick % 4 == 0) await SendPermissionsAsync(); // permissões valem 5 min
                if (tick % 5 == 0)                               // reserva vale 10 min
                {
                    var r = await TransactAsync(Refresh, BuildAttrs(withAuth: true, a => a.Add((ALifetime, BE32(600)))));
                    if (ErrorCode(r) == 438) { ReadRealmNonce(r); await TransactAsync(Refresh, BuildAttrs(withAuth: true, a => a.Add((ALifetime, BE32(600))))); }
                }
            }
            catch (Exception e) { Log.Info("TURN: falha ao renovar: " + e.Message); }
        }
    }

    /// <summary>Manda um pacote para o amigo SAINDO pelo nosso relay (o amigo vê como origem o endereço de relay).</summary>
    public void SendVia(IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        if (Relayed == null) return;
        var attrs = new List<(ushort, byte[])> { (AXorPeer, XorAddress(peer)), (AData, data.ToArray()) };
        _send(Encode(SendInd, RandomNumberGenerator.GetBytes(12), attrs, integrity: false), Server);
    }

    /// <summary>Processa um pacote que veio do servidor TURN. Devolve true se era dele.</summary>
    public bool Handle(ReadOnlySpan<byte> m)
    {
        if (m.Length < 20 || m[4] != 0x21 || m[5] != 0x12 || m[6] != 0xA4 || m[7] != 0x42) return false;
        ushort type = (ushort)((m[0] << 8) | m[1]);
        if (type == DataInd)
        {
            var arr = m.ToArray();
            var peer = ReadXorAddress(arr, AXorPeer);
            var data = ReadAttr(arr, AData);
            if (peer != null && data != null) DataReceived?.Invoke(peer, data);
            return true;
        }
        string key = Convert.ToHexString(m.Slice(8, 12));
        TaskCompletionSource<byte[]>? tcs;
        lock (_gate) _pending.Remove(key, out tcs);
        if (tcs == null) return false;
        tcs.TrySetResult(m.ToArray());
        return true;
    }

    public void Stop() => _cts?.Cancel();

    // ------------------------------------------------------------------ mensagens

    List<(ushort, byte[])> BuildAttrs(bool withAuth, Action<List<(ushort, byte[])>> fill)
    {
        var a = new List<(ushort, byte[])> { (ASoftware, Encoding.ASCII.GetBytes("Streemu")) };
        fill(a);
        if (withAuth)
        {
            a.Add((AUsername, Encoding.UTF8.GetBytes(_username)));
            a.Add((ARealm, Encoding.UTF8.GetBytes(_realm)));
            a.Add((ANonce, Encoding.UTF8.GetBytes(_nonce)));
        }
        return a;
    }

    async Task<byte[]> TransactAsync(ushort method, List<(ushort, byte[])> attrs, int attempts = 4, int waitMs = 1500)
    {
        bool auth = attrs.Any(x => x.Item1 == AUsername);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            var txid = RandomNumberGenerator.GetBytes(12);
            var msg = Encode(method, txid, attrs, integrity: auth);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            string key = Convert.ToHexString(txid);
            lock (_gate) _pending[key] = tcs;
            _send(msg, Server);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(waitMs));
            lock (_gate) _pending.Remove(key);
            if (done == tcs.Task) return await tcs.Task;
        }
        throw new TimeoutException("o servidor TURN não respondeu");
    }

    byte[] Encode(ushort type, byte[] txid, List<(ushort Type, byte[] Value)> attrs, bool integrity)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[20]);
        foreach (var (t, v) in attrs) WriteAttr(ms, t, v);
        if (integrity)
        {
            // MESSAGE-INTEGRITY: HMAC-SHA1 da mensagem com o tamanho já contando este atributo
            var body = ms.ToArray();
            WriteHeader(body, type, body.Length - 20 + 24, txid);
            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{_username}:{_realm}:{_password}"));
            var mac = HMACSHA1.HashData(key, body);
            WriteAttr(ms, AMessageIntegrity, mac);
        }
        var result = ms.ToArray();
        WriteHeader(result, type, result.Length - 20, txid);
        return result;
    }

    static void WriteHeader(byte[] b, ushort type, int length, byte[] txid)
    {
        b[0] = (byte)(type >> 8); b[1] = (byte)type;
        b[2] = (byte)(length >> 8); b[3] = (byte)length;
        b[4] = 0x21; b[5] = 0x12; b[6] = 0xA4; b[7] = 0x42;
        txid.CopyTo(b, 8);
    }

    static void WriteAttr(Stream s, ushort type, byte[] value)
    {
        s.WriteByte((byte)(type >> 8)); s.WriteByte((byte)type);
        s.WriteByte((byte)(value.Length >> 8)); s.WriteByte((byte)value.Length);
        s.Write(value);
        int pad = (4 - value.Length % 4) % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    static byte[]? ReadAttr(byte[] m, ushort want)
    {
        int i = 20;
        while (i + 4 <= m.Length)
        {
            int t = (m[i] << 8) | m[i + 1], len = (m[i + 2] << 8) | m[i + 3];
            if (i + 4 + len > m.Length) break;
            if (t == want) return m.AsSpan(i + 4, len).ToArray();
            i += 4 + len + (4 - len % 4) % 4;
        }
        return null;
    }

    static int? ErrorCode(byte[] m)
    {
        if ((m[1] & 0x10) == 0 || (m[0] & 0x01) == 0) return null; // não é resposta de erro
        var e = ReadAttr(m, AErrorCode);
        return e is { Length: >= 4 } ? e[2] * 100 + e[3] : 400;
    }

    static string ErrorReason(byte[] m)
    {
        var e = ReadAttr(m, AErrorCode);
        return e is { Length: > 4 } ? "\"" + Encoding.UTF8.GetString(e, 4, e.Length - 4) + "\"" : "";
    }

    void ReadRealmNonce(byte[] m)
    {
        var realm = ReadAttr(m, ARealm);
        var nonce = ReadAttr(m, ANonce);
        if (realm != null) _realm = Encoding.UTF8.GetString(realm);
        if (nonce != null) _nonce = Encoding.UTF8.GetString(nonce);
    }

    static IPEndPoint? ReadXorAddress(byte[] m, ushort type)
    {
        var v = ReadAttr(m, type);
        if (v == null || v.Length < 8 || v[1] != 1) return null;
        int port = ((v[2] << 8) | v[3]) ^ 0x2112;
        var ip = new byte[] { (byte)(v[4] ^ 0x21), (byte)(v[5] ^ 0x12), (byte)(v[6] ^ 0xA4), (byte)(v[7] ^ 0x42) };
        return new IPEndPoint(new IPAddress(ip), port);
    }

    static byte[] XorAddress(IPEndPoint ep)
    {
        var ip = ep.Address.GetAddressBytes();
        int port = ep.Port ^ 0x2112;
        return new byte[] { 0, 1, (byte)(port >> 8), (byte)port, (byte)(ip[0] ^ 0x21), (byte)(ip[1] ^ 0x12), (byte)(ip[2] ^ 0xA4), (byte)(ip[3] ^ 0x42) };
    }

    static byte[] BE32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
