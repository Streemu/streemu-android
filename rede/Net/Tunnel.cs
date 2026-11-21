using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

public enum TunnelState { Discovering, Ready, Punching, Connected, Lost, Failed }

/// <summary>Como um pacote chega ao amigo. A ordem é a preferência: direto é melhor que relay.</summary>
public enum CandKind { Direct = 0, PeerRelay = 1, ViaMyRelay = 2 }

public sealed record Cand(IPEndPoint Ep, CandKind Kind)
{
    public override string ToString() => Tunnel.Mask(Ep) + Kind switch
    {
        CandKind.PeerRelay => " (relay do amigo)",
        CandKind.ViaMyRelay => " (pelo meu relay)",
        _ => ""
    };
}

/// <summary>
/// Um único socket UDP que: descobre o endereço público (STUN), fura o NAT até o amigo,
/// e depois carrega controle (JSON), ping e os pacotes SRT das duas direções.
/// Formato: 'L' 'X' tipo(1) tag(4) [seq(4)] payload. A tag vem dos segredos dos dois códigos.
///
/// O amigo pode ser alcançável por vários caminhos ("candidatos"): o endereço público do código, o
/// alternativo (endereço interno da operadora), o relay dele (TURN), o meu relay, e os endereços de onde
/// os pacotes dele chegam de verdade. Punch e ping vão por todos; cada ping leva o índice do candidato e
/// o pong confirma qual caminho de envio funciona. Entre os que funcionam, fica o de maior preferência.
/// </summary>
public sealed class Tunnel : IDisposable
{
    const byte TPunch = 1, TPing = 3, TPong = 4, TCtrl = 5;
    public const byte TToViewer = 0x10, TToSender = 0x11;
    /// <summary>Pedaços do arquivo do modo filme e as confirmações (spec 010, 4.2); o conteúdo é do Filme/Transferencia.cs. App antigo ignora.</summary>
    public const byte TArquivo = 0x12;
    const int HeaderLen = 7, MediaHeaderLen = 11, MaxCandidates = 10;
    const int SIO_UDP_CONNRESET = -1744830452;
    const int SprayLow = -64, SprayHigh = 512, SprayPerTick = 48;

    readonly Socket _sock;
    readonly bool _localMode;
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _stunGate = new();
    readonly Dictionary<string, TaskCompletionSource<IPEndPoint>> _stunPending = new();
    readonly HashSet<string> _seenCtrl = new();
    readonly Queue<string> _seenOrder = new();
    readonly HashSet<string> _seenAsSent = new();
    readonly object _candGate = new();

    byte[] _tag = new byte[4];
    PeerCode? _peerCode;
    volatile Cand[] _cands = Array.Empty<Cand>();
    long[] _candPong = Array.Empty<long>();
    volatile Cand? _target;
    volatile bool _validated;
    long _lastRxMs = -1, _connectedAtMs = -1, _punchStartMs = -1, _lastPongMs = -1;
    bool _oneWayWarned;
    uint _txSeqViewer, _txSeqSender;
    readonly SeqTracker _rxViewer = new(), _rxSender = new();
    long _txBytes, _rxBytes, _txPackets, _rxPeerPackets, _rxWrongTag, _rxOtherSource, _pongs, _rxViaRelay;

    public byte[] Secret { get; }
    public IPEndPoint? PublicEndpoint { get; private set; }
    public IPEndPoint? Peer => _target?.Ep;
    public Cand? Target => _target;
    /// <summary>
    /// O destino confirmado é o endereço alternativo do amigo (o dele dentro da operadora, CGNAT), e não o público? Só vale se o alternativo for mesmo um IP de
    /// operadora ou privado: o candidato alternativo de teste (127.0.0.2) e qualquer outro IP público nunca contam, então a telemetria não vê "direto pela operadora" à toa.
    /// </summary>
    public bool TargetIsCarrierAlt => _target is { Kind: CandKind.Direct } t && _peerCode is { Alt: { } alt } c && !alt.Equals(c.Endpoint) && t.Ep.Equals(alt) && Upnp.IsCarrierOrPrivate(alt.Address);
    public int LocalPort { get; }
    public string Passphrase { get; private set; } = "";
    public TunnelState State { get; private set; } = TunnelState.Discovering;
    public double RttMs { get; private set; } = -1;
    // depois de "Lost" o início da tentativa é reiniciado; sem essa checagem o relatório mostrava tempo negativo
    public long ConnectMs => _connectedAtMs >= 0 && _punchStartMs >= 0 && _connectedAtMs >= _punchStartMs ? _connectedAtMs - _punchStartMs : -1;
    public long TxBytes => Interlocked.Read(ref _txBytes);
    public long RxBytes => Interlocked.Read(ref _rxBytes);
    public SeqTracker RxViewer => _rxViewer;
    public SeqTracker RxSender => _rxSender;
    public TurnClient? Turn { get; private set; }
    /// <summary>Recebo o amigo, mas ele não responde meus pings: meus pacotes não chegam nele.</summary>
    public bool OneWay => State == TunnelState.Connected && _oneWayWarned && _lastPongMs < 0;
    /// <summary>Apelido enviado no punch, para o outro lado saber quem está tentando.</summary>
    public string Nick { get; set; } = "";
    /// <summary>
    /// Túnel combinado pela sala do grupo (<see cref="PeerLink"/>): os códigos são trocados pelo app, nunca à mão. Desde a
    /// spec 022 todo túnel é assim, então ninguém mais lê isto; fica porque o <see cref="PeerLink"/> ainda o marca.
    /// </summary>
    public bool CodesExchangedByApp { get; set; }

    public JsonObject Counters => new()
    {
        ["pacotes_enviados"] = Interlocked.Read(ref _txPackets),
        ["pacotes_do_amigo"] = Interlocked.Read(ref _rxPeerPackets),
        ["pacotes_pelo_meu_relay"] = Interlocked.Read(ref _rxViaRelay),
        ["pacotes_com_codigo_errado"] = Interlocked.Read(ref _rxWrongTag),
        ["pacotes_de_outro_endereco_aceitos"] = Interlocked.Read(ref _rxOtherSource),
        ["pongs_recebidos"] = Interlocked.Read(ref _pongs),
        ["so_de_ida"] = OneWay,
        ["candidatos"] = string.Join(", ", _cands.Select(c => c.ToString())),
        ["caminho_de_envio"] = (_target?.ToString() ?? "-") + (_validated ? " (confirmado)" : " (sem confirmação)"),
        ["meu_relay"] = Turn?.Relayed == null ? "desligado" : Mask(Turn.Relayed)
    };

    public event Action<TunnelState, string>? StateChanged;
    /// <summary>Avisos de diagnóstico da ligação (conexão só de ida); a sala os mostra como aviso (MainForm.Grupo.cs).</summary>
    public event Action<string>? Hint;
    /// <summary>
    /// A conexão ficou "só de ida" (true) ou voltou a ser de ida e volta (false). Existe ao lado do <see cref="Hint"/> porque os dois avisos chegam com
    /// <see cref="OneWay"/> ainda verdadeiro: quem grava telemetria precisa saber qual dos dois foi sem ler o texto do aviso.
    /// </summary>
    public event Action<bool>? OneWayChanged;
    public event Action<JsonObject>? Control;
    public Action<ReadOnlySpan<byte>>? ToViewer;
    public Action<ReadOnlySpan<byte>>? ToSender;
    public Action<ReadOnlySpan<byte>>? Arquivo;
    /// <summary>Chave AES-256 do arquivo do modo filme: sai dos dois segredos, como a tag e a senha do SRT.</summary>
    public byte[] ChaveArquivo { get; private set; } = Array.Empty<byte>();

    public Tunnel(bool localMode, byte[]? secret = null, int preferredPort = 0)
    {
        _localMode = localMode;
        Secret = secret is { Length: 6 } ? secret : RandomNumberGenerator.GetBytes(6);
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // SIO_UDP_CONNRESET é do Winsock: sem ele, um ICMP "porta inacessível" derruba o próximo ReceiveFrom no Windows. No Linux/Android o UDP sem connect() não tem esse problema.
        if (OperatingSystem.IsWindows()) _sock.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        _sock.ReceiveBufferSize = 4 * 1024 * 1024;
        _sock.SendBufferSize = 4 * 1024 * 1024;
        var bindIp = localMode ? IPAddress.Loopback : IPAddress.Any;
        try { _sock.Bind(new IPEndPoint(bindIp, preferredPort)); }
        catch (SocketException) { _sock.Bind(new IPEndPoint(bindIp, 0)); }
        LocalPort = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        new Thread(ReceiveLoop) { IsBackground = true, Name = "tunnel-rx" }.Start();
        new Thread(TimerLoop) { IsBackground = true, Name = "tunnel-timer" }.Start();
    }

    // ------------------------------------------------------------------ descoberta (STUN)

    public async Task<IPEndPoint> DiscoverAsync()
    {
        SetState(TunnelState.Discovering, "Descobrindo seu endereço público...");
        if (_localMode)
        {
            PublicEndpoint = new IPEndPoint(IPAddress.Loopback, LocalPort);
            SetState(TunnelState.Ready, "Modo local (teste no mesmo PC).");
            return PublicEndpoint;
        }
        string[] servers = { "stun.l.google.com:19302", "stun.cloudflare.com:3478", "stun1.l.google.com:19302" };
        var found = new List<IPEndPoint>();
        foreach (var s in servers)
        {
            try
            {
                var ep = await StunQueryAsync(s, TimeSpan.FromSeconds(2));
                found.Add(ep);
                Log.Info($"STUN {s}: porta pública {ep.Port}");
            }
            catch (Exception e) { Log.Info($"STUN {s} falhou: {e.Message}"); }
        }
        if (found.Count == 0)
        {
            SetState(TunnelState.Failed, "Nenhum servidor STUN respondeu: UDP de saída parece bloqueado (firewall/antivírus/rede).");
            throw new InvalidOperationException("STUN sem resposta");
        }
        if (found.Select(f => f.ToString()).Distinct().Count() > 1)
            Log.Info("ATENÇÃO: a porta pública mudou conforme o servidor (NAT simétrico). A conexão direta provavelmente vai falhar; o relay de reserva resolve.");
        PublicEndpoint = found[0];
        SetState(TunnelState.Ready, "Pronto para conectar.");
        return PublicEndpoint;
    }

    async Task<IPEndPoint> StunQueryAsync(string hostPort, TimeSpan timeout)
    {
        var parts = hostPort.Split(':');
        var addrs = await Dns.GetHostAddressesAsync(parts[0]);
        var ip = addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var target = new IPEndPoint(ip, int.Parse(parts[1]));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var txid = RandomNumberGenerator.GetBytes(12);
            var req = new byte[20];
            req[1] = 1; req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            txid.CopyTo(req, 8);
            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            string key = Convert.ToHexString(txid);
            lock (_stunGate) _stunPending[key] = tcs;
            _sock.SendTo(req, target);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            lock (_stunGate) _stunPending.Remove(key);
            if (done == tcs.Task) return await tcs.Task;
        }
        throw new TimeoutException("sem resposta");
    }

    void HandleStun(ReadOnlySpan<byte> r)
    {
        string key = Convert.ToHexString(r.Slice(8, 12));
        TaskCompletionSource<IPEndPoint>? tcs;
        lock (_stunGate) _stunPending.TryGetValue(key, out tcs);
        if (tcs == null) return;
        int i = 20;
        while (i + 4 <= r.Length)
        {
            int type = (r[i] << 8) | r[i + 1];
            int len = (r[i + 2] << 8) | r[i + 3];
            if ((type == 0x0020 || type == 0x0001) && len >= 8 && r[i + 5] == 1)
            {
                int port = (r[i + 6] << 8) | r[i + 7];
                var ipb = r.Slice(i + 8, 4).ToArray();
                if (type == 0x0020)
                {
                    port ^= 0x2112;
                    ipb[0] ^= 0x21; ipb[1] ^= 0x12; ipb[2] ^= 0xA4; ipb[3] ^= 0x42;
                }
                tcs.TrySetResult(new IPEndPoint(new IPAddress(ipb), port));
                return;
            }
            i += 4 + len + ((4 - len % 4) % 4);
        }
    }

    // ------------------------------------------------------------------ relay (TURN)

    /// <summary>Reserva um endereço no servidor de relay. Os pacotes que chegarem lá vêm para este mesmo socket.</summary>
    public async Task<IPEndPoint> EnableRelayAsync(IPEndPoint server, string username, string password)
    {
        var turn = new TurnClient(server, (bytes, to) =>
        {
            try { _sock.SendTo(bytes, to); Interlocked.Add(ref _txBytes, bytes.Length); } catch { }
        });
        turn.DataReceived = (peer, data) =>
        {
            var probe = _relayProbe;
            if (probe != null && data.AsSpan().SequenceEqual(probe.Value.Payload)) { probe.Value.Done.TrySetResult(true); return; }
            Interlocked.Increment(ref _rxViaRelay);
            ProcessPacket(data, peer);
        };
        Turn = turn;
        try
        {
            var relayed = await turn.AllocateAsync(username, password);
            if (_peerCode != null) PermitPeer();
            return relayed;
        }
        catch { Turn = null; throw; }
    }

    (byte[] Payload, TaskCompletionSource<bool> Done)? _relayProbe;

    /// <summary>
    /// Autoteste do relay: um socket à parte (faz o papel de um amigo) manda um pacote para o nosso endereço de relay
    /// e conferimos se ele chega de volta pelo servidor TURN. Prova credencial, reserva, permissão e repasse.
    /// </summary>
    public async Task<bool> SelfTestRelayAsync(TimeSpan timeout)
    {
        var turn = Turn;
        if (turn?.Relayed == null || PublicEndpoint == null) return false;
        turn.Permit(PublicEndpoint.Address);
        await Task.Delay(700); // dá tempo da permissão chegar no servidor
        var payload = Encoding.ASCII.GetBytes("LXRT").Concat(RandomNumberGenerator.GetBytes(8)).ToArray();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _relayProbe = (payload, done);
        try
        {
            using var friend = new UdpClient(0);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout && !done.Task.IsCompleted)
            {
                await friend.SendAsync(payload, payload.Length, turn.Relayed);
                await Task.WhenAny(done.Task, Task.Delay(300));
            }
            return done.Task.IsCompleted;
        }
        finally { _relayProbe = null; }
    }

    void PermitPeer()
    {
        var t = Turn;
        if (t == null) return;
        foreach (var c in _cands) if (c.Kind != CandKind.ViaMyRelay) t.Permit(c.Ep.Address);
    }

    // ------------------------------------------------------------------ conexão

    public void ConnectTo(PeerCode code)
    {
        // Tag e senha do SRT derivam dos dois segredos, em ordem fixa, para os dois lados chegarem ao mesmo valor.
        byte[] a = Secret, b = code.Secret;
        bool aFirst = Convert.ToHexString(a).CompareTo(Convert.ToHexString(b)) <= 0;
        var material = Encoding.ASCII.GetBytes("streemu-v1").Concat(aFirst ? a : b).Concat(aFirst ? b : a).ToArray();
        var hash = SHA256.HashData(material);
        _tag = hash[..4];
        Passphrase = Convert.ToHexString(hash, 4, 16).ToLowerInvariant();
        ChaveArquivo = SHA256.HashData(Encoding.ASCII.GetBytes("streemu-arquivo-v1").Concat(material).ToArray());
        _peerCode = code;
        var cands = new List<Cand> { new(code.Endpoint, CandKind.Direct) };
        if (code.Alt != null && !code.Alt.Equals(code.Endpoint)) cands.Add(new(code.Alt, CandKind.Direct));
        if (code.Relay != null) cands.Add(new(code.Relay, CandKind.PeerRelay));
        if (Turn?.Relayed != null) cands.Add(new(code.Endpoint, CandKind.ViaMyRelay));
        lock (_candGate) { _cands = cands.ToArray(); _candPong = new long[_cands.Length]; Array.Fill(_candPong, -1); }
        _target = cands[0];
        _validated = false;
        lock (_seenAsSent) _seenAsSent.Clear();
        PermitPeer();
        _punchStartMs = _clock.ElapsedMilliseconds;
        _lastRxMs = -1; _connectedAtMs = -1; _lastPongMs = -1; _oneWayWarned = false; RttMs = -1;
        SetState(TunnelState.Punching, $"Tentando conexão com {string.Join(" e ", cands.Select(c => c.ToString()))}...");
    }

    /// <summary>Acrescenta um caminho (ex.: o amigo avisou o endereço de relay dele depois do código).</summary>
    public void AddCandidate(IPEndPoint ep, CandKind kind, string why)
    {
        lock (_candGate)
        {
            if (_cands.Any(c => c.Ep.Equals(ep) && c.Kind == kind) || _cands.Length >= MaxCandidates) return;
            _cands = _cands.Append(new Cand(ep, kind)).ToArray();
            var pongs = _candPong.ToList(); pongs.Add(-1); _candPong = pongs.ToArray();
        }
        if (kind == CandKind.Direct) Interlocked.Increment(ref _rxOtherSource);
        if (kind != CandKind.ViaMyRelay) Turn?.Permit(ep.Address);
        Log.Info($"Novo caminho até o amigo: {new Cand(ep, kind)} ({why}).");
    }

    void TimerLoop()
    {
        long lastStun = 0, lastPing = 0, lastReport = 0;
        int sprayCursor = 0;
        bool sprayLogged = false;
        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(100);
            long now = _clock.ElapsedMilliseconds;
            try
            {
                switch (State)
                {
                    case TunnelState.Ready:
                        // mantém o mapeamento do NAT vivo enquanto os códigos são trocados
                        if (!_localMode && now - lastStun > 15000) { lastStun = now; _ = StunQueryAsync("stun.l.google.com:19302", TimeSpan.FromSeconds(2)).ContinueWith(t => { if (t.IsCompletedSuccessfully && PublicEndpoint != null && !t.Result.Equals(PublicEndpoint)) Log.Info($"ATENÇÃO: seu endereço público mudou para porta {t.Result.Port}. Feche e abra o app para gerar o código novo."); }); }
                        break;
                    case TunnelState.Punching:
                    case TunnelState.Lost:
                        SendPunchAll();
                        if (now - lastPing >= 1000) { lastPing = now; SendPings(); }
                        if (!_localMode && now - _punchStartMs > 3000)
                        {
                            if (!sprayLogged) { sprayLogged = true; Log.Info($"Nada chegou ainda: tentando também as portas vizinhas do amigo ({SprayLow}..+{SprayHigh})."); }
                            SendSpray(ref sprayCursor);
                        }
                        if (now - lastReport > 15000 && now - _punchStartMs > 14000)
                        {
                            lastReport = now;
                            long wrong = Interlocked.Read(ref _rxWrongTag);
                            Log.Info($"Ainda sem resposta do amigo: {Interlocked.Read(ref _txPackets)} pacotes enviados, 0 recebidos com o código certo" +
                                     (wrong > 0 ? $", {wrong} com código errado." : ". Se o amigo já recebeu o seu código, algo no caminho bloqueia; com o relay de reserva ligado nos dois, isso se resolve."));
                        }
                        if (now - _punchStartMs > 10 * 60 * 1000 && State == TunnelState.Punching)
                            SetState(TunnelState.Failed, "Não conectou em 10 min. Copie o relatório e mande para análise.");
                        if (!_localMode && now - lastStun > 15000) { lastStun = now; _ = StunQueryAsync("stun.l.google.com:19302", TimeSpan.FromSeconds(2)).ContinueWith(_ => { }); }
                        ChooseTarget(now);
                        break;
                    case TunnelState.Connected:
                        // até confirmar um caminho de envio, continua abrindo o NAT do outro lado por todos os candidatos
                        if (!_validated || now - _connectedAtMs < 10000) SendPunchAll();
                        if (now - lastPing >= 1000) { lastPing = now; SendPings(); }
                        ChooseTarget(now);
                        if (!_oneWayWarned && _lastPongMs < 0 && now - _connectedAtMs > 6000)
                        {
                            _oneWayWarned = true;
                            RaiseHint("Conexão SÓ DE IDA: eu recebo os pacotes do amigo, mas ele não recebe os meus (nenhum ping voltou). " +
                                      "O relay de reserva entra sozinho; se não entrar, peça para ele reabrir o app.");
                            OneWayChanged?.Invoke(true);
                        }
                        if (_lastRxMs >= 0 && now - _lastRxMs > 8000)
                        {
                            _punchStartMs = now;
                            _validated = false;
                            SetState(TunnelState.Lost, "Conexão perdida (8 s sem pacotes do amigo). Tentando reconectar...");
                        }
                        break;
                }
            }
            catch (Exception e) { Log.Info("tunnel timer: " + e.Message); }
        }
    }

    /// <summary>Entre os caminhos com pong nos últimos 4 s, usa o de maior preferência (direto antes de relay).</summary>
    void ChooseTarget(long now)
    {
        Cand? best = null;
        lock (_candGate)
        {
            for (int i = 0; i < _cands.Length; i++)
            {
                if (_candPong[i] < 0 || now - _candPong[i] > 4000) continue;
                if (best == null || _cands[i].Kind < best.Kind) best = _cands[i];
            }
        }
        if (best == null) { _validated = false; return; }
        if (!best.Equals(_target) || !_validated)
        {
            Log.Info($"Caminho de envio: {best}" + (best.Kind == CandKind.Direct ? "." : " (o direto não respondeu)."));
            _target = best;
        }
        _validated = true;
    }

    byte[] PunchPayload()
    {
        // meu segredo + segredo que eu acho que é do amigo + apelido: o outro lado consegue dizer
        // quem está tentando e se alguém está com código velho.
        var nick = Encoding.UTF8.GetBytes(Nick.Length > 24 ? Nick[..24] : Nick);
        var p = new byte[13 + nick.Length];
        Secret.CopyTo(p, 0);
        _peerCode?.Secret.CopyTo(p, 6);
        p[12] = (byte)nick.Length;
        nick.CopyTo(p, 13);
        return p;
    }

    void SendPunchAll()
    {
        var payload = PunchPayload();
        foreach (var c in _cands) SendRaw(TPunch, payload, c);
    }

    void SendPings()
    {
        var cands = _cands;
        Span<byte> p = stackalloc byte[9];
        for (int i = 0; i < cands.Length; i++)
        {
            BitConverter.TryWriteBytes(p, _clock.ElapsedTicks);
            p[8] = (byte)i;
            SendRaw(TPing, p, cands[i]);
        }
    }

    /// <summary>
    /// Varredura: manda punch para portas vizinhas da porta do código. Se o CGNAT do amigo usa outra porta
    /// para falar comigo, o meu NAT passa a aceitar essa porta e o pacote dele entra.
    /// </summary>
    void SendSpray(ref int cursor)
    {
        var cands = _cands;
        if (cands.Length == 0) return;
        var primary = cands[0].Ep;
        var payload = PunchPayload();
        for (int i = 0; i < SprayPerTick; i++)
        {
            int offset = SprayLow + cursor;
            cursor = (cursor + 1) % (SprayHigh - SprayLow + 1);
            int port = primary.Port + offset;
            if (offset == 0 || port < 1024 || port > 65535) continue;
            SendRaw(TPunch, payload, new Cand(new IPEndPoint(primary.Address, port), CandKind.Direct));
        }
    }

    void ReceiveLoop()
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = _sock.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            var span = buf.AsSpan(0, n);
            var ep = (IPEndPoint)from;
            var turn = Turn;
            if (turn != null && ep.Equals(turn.Server) && turn.Handle(span)) continue;
            if (n >= 20 && (span[0] & 0xC0) == 0 && span[4] == 0x21 && span[5] == 0x12 && span[6] == 0xA4 && span[7] == 0x42)
            {
                HandleStun(span);
                continue;
            }
            ProcessPacket(span, ep);
        }
    }

    void ProcessPacket(ReadOnlySpan<byte> span, IPEndPoint ep)
    {
        int n = span.Length;
        if (n < HeaderLen || span[0] != (byte)'L' || span[1] != (byte)'X') return;
        byte type = span[2];
        var payload = span[HeaderLen..];
        if (_peerCode == null || !span.Slice(3, 4).SequenceEqual(_tag))
        {
            Interlocked.Increment(ref _rxWrongTag);
            return;
        }
        if (!_cands.Any(c => c.Ep.Equals(ep) && c.Kind != CandKind.ViaMyRelay))
        {
            AddCandidate(ep, CandKind.Direct, "os pacotes dele chegam desse endereço");
            TellSeenAs(ep);
        }
        Interlocked.Add(ref _rxBytes, n);
        Interlocked.Increment(ref _rxPeerPackets);
        _lastRxMs = _clock.ElapsedMilliseconds;
        if (State != TunnelState.Connected)
        {
            _connectedAtMs = _clock.ElapsedMilliseconds;
            SetState(TunnelState.Connected, $"Conectado com o amigo (em {ConnectMs} ms).");
        }
        switch (type)
        {
            case TPunch: break;
            case TPing:
            {
                // responde por todos os caminhos: o de volta pode ser diferente do de ida.
                // O pong leva a minha hora no fim (relógio comum do modo filme, spec 010, 7.2); app antigo lê só os 9 primeiros bytes.
                byte[] reply = payload.ToArray();
                if (payload.Length == 9) { reply = new byte[17]; payload.CopyTo(reply); BitConverter.TryWriteBytes(reply.AsSpan(9), NowUs); }
                SendRaw(TPong, reply, new Cand(ep, CandKind.Direct));
                foreach (var c in _cands) if (!(c.Ep.Equals(ep) && c.Kind == CandKind.Direct)) SendRaw(TPong, reply, c);
                break;
            }
            case TPong:
                if (payload.Length >= 8)
                {
                    Interlocked.Increment(ref _pongs);
                    if (_lastPongMs < 0 && _oneWayWarned) { RaiseHint("Agora os pings voltam: a conexão ficou de ida e volta."); OneWayChanged?.Invoke(false); }
                    long now = _clock.ElapsedMilliseconds;
                    _lastPongMs = now;
                    if (payload.Length >= 9)
                    {
                        int idx = payload[8];
                        lock (_candGate) { if (idx < _candPong.Length) _candPong[idx] = now; }
                        ChooseTarget(now);
                    }
                    long sent = BitConverter.ToInt64(payload);
                    double rtt = (_clock.ElapsedTicks - sent) * 1000.0 / Stopwatch.Frequency;
                    if (_target != null && payload.Length >= 9 && payload[8] < _cands.Length && _cands[payload[8]].Equals(_target))
                        RttMs = RttMs < 0 ? rtt : RttMs * 0.8 + rtt * 0.2;
                    if (payload.Length >= 17) LearnPeerClock(BitConverter.ToInt64(payload[9..]), rtt, now);
                }
                break;
            case TCtrl: HandleCtrl(payload); break;
            case TToViewer:
                if (payload.Length > 4) { _rxViewer.Track(BitConverter.ToUInt32(payload)); ToViewer?.Invoke(payload[4..]); }
                break;
            case TToSender:
                if (payload.Length > 4) { _rxSender.Track(BitConverter.ToUInt32(payload)); ToSender?.Invoke(payload[4..]); }
                break;
            case TArquivo: Arquivo?.Invoke(payload); break;
        }
    }

    /// <summary>Conta ao amigo de que endereço os pacotes dele chegam aqui (ele pode não saber que a operadora usa outro).</summary>
    void TellSeenAs(IPEndPoint ep)
    {
        lock (_seenAsSent) { if (!_seenAsSent.Add(ep.ToString())) return; }
        SendControl(new JsonObject { ["type"] = "seen_as", ["ep"] = ep.ToString() });
    }

    // ------------------------------------------------------------------ envio

    /// <summary>
    /// Só para teste (protótipo Android): nada sai direto para o amigo, só pelo relay (o meu, ou o dele). Serve para provar o caminho do TURN numa
    /// rede em que a conexão direta funcionaria. STUN e a conversa com o servidor TURN não passam por aqui e seguem normais. Não existe no app Windows.
    /// </summary>
    public bool SoRelay { get; set; }

    void Transmit(ReadOnlySpan<byte> pkt, Cand c)
    {
        if (SoRelay && c.Kind == CandKind.Direct) return;
        try
        {
            // Pelo meu relay, quem conta os bytes é o envio do TurnClient (EnableRelayAsync), com o cabeçalho do TURN: contar aqui também
            // dobrava o enviado_kbps da telemetria nas ligações pelo relay (protótipo Android, 2026-11-21: 25,6 Mbit/s contados para 12,6 recebidos).
            if (c.Kind == CandKind.ViaMyRelay) Turn?.SendVia(c.Ep, pkt);
            else { _sock.SendTo(pkt, SocketFlags.None, c.Ep); Interlocked.Add(ref _txBytes, pkt.Length); }
            Interlocked.Increment(ref _txPackets);
        }
        catch (ObjectDisposedException) { }
        catch (Exception e) { Log.Info("envio UDP falhou: " + e.Message); }
    }

    void SendRaw(byte type, ReadOnlySpan<byte> payload, Cand? to = null)
    {
        var dest = to ?? _target;
        if (dest == null) return;
        Span<byte> pkt = stackalloc byte[HeaderLen + payload.Length];
        pkt[0] = (byte)'L'; pkt[1] = (byte)'X'; pkt[2] = type;
        _tag.CopyTo(pkt[3..]);
        payload.CopyTo(pkt[HeaderLen..]);
        Transmit(pkt, dest);
    }

    public void SendMedia(byte type, ReadOnlySpan<byte> data)
    {
        if (State != TunnelState.Connected) return;
        var dest = _target;
        if (dest == null) return;
        Span<byte> pkt = stackalloc byte[MediaHeaderLen + data.Length];
        pkt[0] = (byte)'L'; pkt[1] = (byte)'X'; pkt[2] = type;
        _tag.CopyTo(pkt[3..]);
        uint seq = type == TToViewer ? Interlocked.Increment(ref _txSeqViewer) : Interlocked.Increment(ref _txSeqSender);
        BitConverter.TryWriteBytes(pkt[HeaderLen..], seq);
        data.CopyTo(pkt[MediaHeaderLen..]);
        Transmit(pkt, dest);
    }

    public void SendArquivo(ReadOnlySpan<byte> data) { if (State == TunnelState.Connected) SendRaw(TArquivo, data); }

    /// <summary>Hora do PC em microssegundos (QPC: o mesmo para todos os processos do PC). Base do relógio comum do modo filme.</summary>
    public static long NowUs => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    readonly Queue<(long AtMs, double RttMs, long OffsetUs)> _clockSamples = new();

    /// <summary>Hora do amigo menos a minha (µs), da medida de menor ida e volta dos últimos 30 s (como o NTP). Nulo até o primeiro pong novo.</summary>
    public long? PeerClockOffsetUs { get; private set; }
    /// <summary>Ida e volta da medida usada em <see cref="PeerClockOffsetUs"/>: o erro do relógio fica abaixo da metade dela.</summary>
    public double PeerClockRttMs { get; private set; } = -1;

    void LearnPeerClock(long peerUs, double rttMs, long nowMs)
    {
        long offset = peerUs - (NowUs - (long)(rttMs * 500));
        lock (_clockSamples)
        {
            _clockSamples.Enqueue((nowMs, rttMs, offset));
            while (_clockSamples.Count > 0 && (nowMs - _clockSamples.Peek().AtMs > 30000 || _clockSamples.Count > 200)) _clockSamples.Dequeue();
            var best = _clockSamples.MinBy(s => s.RttMs);
            PeerClockOffsetUs = best.OffsetUs;
            PeerClockRttMs = best.RttMs;
        }
    }

    /// <summary>Mensagem de controle; repetida 3 vezes (UDP pode perder), o outro lado ignora duplicatas.
    /// Enquanto o caminho de envio não está confirmado, vai por todos os candidatos.</summary>
    public void SendControl(JsonObject msg)
    {
        msg["id"] = Guid.NewGuid().ToString("N");
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                if (_validated) SendRaw(TCtrl, bytes);
                else foreach (var c in _cands) SendRaw(TCtrl, bytes, c);
                await Task.Delay(150);
            }
        });
    }

    void HandleCtrl(ReadOnlySpan<byte> payload)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject; } catch { return; }
        string? id = msg?["id"]?.GetValue<string>();
        if (msg == null || id == null) return;
        lock (_seenCtrl)
        {
            if (!_seenCtrl.Add(id)) return;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > 200) _seenCtrl.Remove(_seenOrder.Dequeue());
        }
        Control?.Invoke(msg);
    }

    void SetState(TunnelState s, string message)
    {
        State = s;
        Log.Info($"[conexão] {s}: {message}");
        StateChanged?.Invoke(s, message);
    }

    void RaiseHint(string message)
    {
        Log.Info("[aviso] " + message);
        Hint?.Invoke(message);
    }

    public static string Mask(IPEndPoint? ep)
    {
        if (ep == null) return "-";
        var b = ep.Address.GetAddressBytes();
        return b.Length == 4 ? $"{b[0]}.{b[1]}.x.x:{ep.Port}" : "ipv6:" + ep.Port;
    }

    public void Dispose()
    {
        // libera a reserva do relay ANTES de parar o recebimento (a resposta chega por este socket); sem isso,
        // reabrir o app na mesma porta em menos de 10 min dava TURN erro 437 (teste 0.1.8)
        Turn?.Release();
        _cts.Cancel();
        try { _sock.Dispose(); } catch { }
    }
}

/// <summary>Conta pacotes recebidos e perdidos (pelos números de sequência) numa janela de tempo.</summary>
public sealed class SeqTracker
{
    readonly object _gate = new();
    uint _highest;
    bool _started;
    long _received, _lost, _winReceived, _winLost;

    public void Track(uint seq)
    {
        lock (_gate)
        {
            if (!_started || seq + 1000 < _highest) { _started = true; _highest = seq; _received++; _winReceived++; return; }
            if (seq > _highest)
            {
                long gap = seq - _highest - 1;
                if (gap > 0 && gap < 10000) { _lost += gap; _winLost += gap; }
                _highest = seq;
            }
            _received++; _winReceived++;
        }
    }

    /// <summary>Perda (%) desde a última chamada.</summary>
    public double TakeWindowLossPct()
    {
        lock (_gate)
        {
            long total = _winReceived + _winLost;
            double pct = total == 0 ? 0 : 100.0 * _winLost / total;
            _winReceived = 0; _winLost = 0;
            return pct;
        }
    }

    public double TotalLossPct { get { lock (_gate) { long t = _received + _lost; return t == 0 ? 0 : 100.0 * _lost / t; } } }
    public long Received { get { lock (_gate) return _received; } }
}
