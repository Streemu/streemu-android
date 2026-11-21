using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>O que um amigo está transmitindo, como a sala do grupo mostra para todos.</summary>
public sealed record LiveInfo(string StreamId, string Game, string Quality, long SinceUnixMs, int Watching);

public sealed record Member(string Id, string Nick, string Version, LiveInfo? Live);

/// <summary>
/// Sala do grupo no Worker (relay/worker.js, Durable Object). Cada app aberto fica conectado por WebSocket e
/// todos sabem quem está online e quem está ao vivo. É por aqui que o pedido de "quero assistir" e os códigos de
/// pareamento chegam ao outro lado: sem isso o botão Assistir do Discord não teria como levar o código de quem
/// clicou até quem transmite (o furo de NAT precisa dos dois códigos).
/// A sala só responde a membro do grupo, com o token de acesso da conta (<c>/grupos/&lt;id&gt;/sala</c>); o id é a conta que o servidor conferiu e o <c>ola</c>
/// não leva id. IP e porta nunca saem dela. Desde a spec 022 não existe mais a sala antiga (<c>/sala</c>, com a senha do grupo).
/// </summary>
public sealed class GroupRoom : ISala
{
    readonly Uri _url;
    /// <summary>Dá o token de acesso da conta (o parâmetro é "renove antes", depois de um 401).</summary>
    readonly Func<bool, Task<string?>> _token;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    readonly object _gate = new();
    ClientWebSocket? _ws;
    string _nick;
    LiveInfo? _live;
    /// <summary>Quem a sala diz que está conectado agora.</summary>
    List<Member> _members = new();
    /// <summary>Quem sumiu da sala há pouco (id → como estava e quando sumiu). Ver <see cref="AwayGrace"/>.</summary>
    readonly Dictionary<string, (Member Member, DateTime GoneAt)> _away = new();

    /// <summary>
    /// Quanto tempo quem sumiu da sala continua na lista. A conexão com a sala cai sem aviso por ~2 s e volta (dois amigos
    /// em 2026-11-10: 4 e 2 quedas no dia; ver DESCOBERTAS); sem essa folga o nome sumia e voltava, a miniatura e o
    /// avatar eram esquecidos e pedidos de novo. 10 s cobrem as duas primeiras tentativas de reconexão (2 s e 4 s).
    /// Quem fecha o app de verdade fica no máximo isso a mais na lista.
    /// </summary>
    static readonly TimeSpan AwayGrace = TimeSpan.FromSeconds(10);

    public string MyId { get; }
    /// <summary>Nome do grupo, como a sala informou.</summary>
    public string GrupoNome { get; private set; } = "";
    /// <summary>Membros guardados que não estão conectados.</summary>
    public IReadOnlyList<Ausente> Ausentes { get { lock (_gate) return _ausentes; } }
    IReadOnlyList<Ausente> _ausentes = Array.Empty<Ausente>();
    public bool Connected { get; private set; }
    const int AtividadesMax = 50;
    IReadOnlyList<Atividade> _atividades = Array.Empty<Atividade>();
    /// <summary>Atividades recentes, da mais nova para a mais antiga.</summary>
    public IReadOnlyList<Atividade> Atividades { get { lock (_gate) return _atividades; } }
    public event Action? AtividadesMudaram;
    /// <summary>Conta uma atividade ao servidor (só conectada). O servidor decide se vale e não repete (relay/sala.js, <c>atividadeDoApp</c>).</summary>
    public void EnviarAtividade(string tipo, string? alvo = null)
    {
        if (!Connected) return;
        var m = new JsonObject { ["t"] = "atividade", ["tipo"] = tipo };
        if (alvo != null) m["alvo"] = alvo;
        _ = SendAsync(m);
    }
    /// <summary>Última situação da sala, em português, para mostrar na tela.</summary>
    public string Status { get; private set; } = "Entrando na sala do grupo...";
    /// <summary>Quem está na sala, contando quem sumiu há menos de <see cref="AwayGrace"/> (para a tela não piscar).</summary>
    public IReadOnlyList<Member> Members => MesmaPessoa.Juntar(MembrosBrutos(), MyId); // a mesma pessoa com ids diferentes vira uma linha só

    /// <summary>Todas as linhas como a sala mandou (mais quem sumiu há pouco), SEM juntar a mesma pessoa: é onde se procura por id (<see cref="Find"/>).</summary>
    List<Member> MembrosBrutos()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            return _members.Concat(_away.Values.Where(a => now - a.GoneAt < AwayGrace).Select(a => a.Member)).ToList();
        }
    }
    public IEnumerable<Member> Friends => Members.Where(m => m.Id != MyId);
    /// <summary>A sala diz que ele está conectado AGORA (sem a folga de <see cref="Members"/>).</summary>
    public bool IsPresent(string id) { lock (_gate) return _members.Any(m => m.Id == id); }

    public event Action? Changed;
    /// <summary>Mensagem de um amigo: (id, apelido, mensagem).</summary>
    public event Action<string, string, JsonObject>? Message;
    /// <summary>A sala não achou o destinatário (ele fechou o app): (id, mensagem que não foi entregue).</summary>
    public event Action<string, JsonObject?>? Undeliverable;
    /// <summary>Alguém marcou um problema numa live em que estou, ou o admin pediu: enviar a telemetria já (spec 009).</summary>
    public event Action? TelemetryNow;
    public event Action<string>? Encerrada;

    /// <summary>Pede para estes amigos enviarem a telemetria já (marquei um problema numa live com eles).</summary>
    public Task SendTelemetryNow(IEnumerable<string> ids) => SendAsync(new JsonObject { ["t"] = "telemetria-agora", ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i!).ToArray()) });

    /// <summary><paramref name="contaId"/> é o id da conta (o que a sala mostra a todos).</summary>
    public GroupRoom(Uri url, string contaId, string nick, Func<bool, Task<string?>> token)
    {
        _url = url; MyId = contaId; _nick = nick; _token = token;
        _ = Task.Run(RunAsync);
    }

    /// <summary>wss://.../grupos/&lt;id&gt;/sala.</summary>
    public static Uri RoomUrl(string serverUrl, string groupId)
    {
        var b = new UriBuilder(serverUrl.TrimEnd('/') + "/grupos/" + Uri.EscapeDataString(groupId) + "/sala");
        b.Scheme = b.Scheme == "http" ? "ws" : "wss";
        if (b.Uri.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    // por id e por stream procura entre TODAS as linhas: uma ligação aberta com um id que a junção escondeu continua achando o dono dela
    public Member? Find(string id) => MembrosBrutos().FirstOrDefault(m => m.Id == id);
    public Member? FindByStream(string streamId) => MembrosBrutos().FirstOrDefault(m => m.Live?.StreamId == streamId);

    public void SetNick(string nick)
    {
        if (nick == _nick) return;
        _nick = nick;
        _ = SendAsync(Hello());
    }

    public void SetLive(LiveInfo? live)
    {
        _live = live;
        _ = SendAsync(LiveMessage());
    }

    public Task SendTo(string id, JsonObject msg) => SendAsync(new JsonObject { ["t"] = "para", ["para"] = id, ["msg"] = msg });

    // o id vem do token que o servidor conferiu: o `ola` não leva id
    JsonObject Hello() => new() { ["t"] = "ola", ["apelido"] = _nick, ["versao"] = Program.Version };

    JsonObject LiveMessage() => new()
    {
        ["t"] = "estado",
        ["ao_vivo"] = _live == null ? null : new JsonObject
        {
            ["sala"] = _live.StreamId, ["jogo"] = _live.Game, ["qualidade"] = _live.Quality,
            ["desde"] = _live.SinceUnixMs, ["assistindo"] = _live.Watching
        }
    };

    async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        bool loggedDown = false;
        while (!_cts.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            bool opened = false;
            bool encerrou = false;
            bool renovarToken = _renovarToken; _renovarToken = false;
            {
                string? tk = null;
                try { tk = await _token(renovarToken); } catch { }
                if (tk == null)
                {
                    // sem sessão no momento (sem rede e token vencido, ou saiu): espera e tenta de novo; quem sai de vez derruba a sala por fora
                    SetStatus("Sem sala do grupo: não consegui renovar sua sessão. Tento de novo sozinho.");
                    ws.Dispose();
                    try { await Task.Delay(delay, _cts.Token); } catch { break; }
                    delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
                    continue;
                }
                ws.Options.SetRequestHeader("Authorization", "Bearer " + tk);
            }
            ws.Options.CollectHttpResponseDetails = true;
            ws.Options.KeepAliveInterval = TimeSpan.Zero; // o "ping" de texto abaixo é respondido sem acordar a sala
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(_url, connectCts.Token);
                }
                _ws = ws;
                opened = true;
                await SendAsync(Hello());
                if (_live != null) await SendAsync(LiveMessage());
                Connected = true;
                SetStatus("Na sala do grupo.");
                Log.Info("Sala do grupo: conectado.");
                loggedDown = false;
                delay = TimeSpan.FromSeconds(2);
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _ = PingLoopAsync(ws, pingCts.Token);
                await ReceiveLoopAsync(ws);
                pingCts.Cancel();
                // fechamentos do servidor que NÃO se reconectam: 4003 removido, 4004 sessão encerrada, 4005 saiu do grupo
                int codigo = (int?)ws.CloseStatus ?? 0;
                if (codigo is 4003 or 4004 or 4005)
                {
                    encerrou = true;
                    string motivo = codigo == 4003 ? "removido" : codigo == 4004 ? "sessao" : "saiu";
                    SetStatus(codigo == 4003 ? "Você foi removido deste grupo." : codigo == 4004 ? "Sua sessão foi encerrada. Entre de novo." : "Você saiu deste grupo.");
                    Log.Info($"Sala do grupo: encerrada pelo servidor ({motivo}).");
                    Encerrada?.Invoke(motivo);
                }
                else if (!_cts.IsCancellationRequested) Log.Info($"Sala do grupo: conexão caiu ({ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "sem motivo"}). Reconectando...");
            }
            catch (Exception e) when (!_cts.IsCancellationRequested && opened)
            {
                // A conexão já estava aberta e caiu sem o fechamento do WebSocket. Antes isso caía no ramo de baixo e virava
                // "o servidor respondeu 101" (o 101 é o da abertura que DEU certo), o que parecia erro do servidor.
                SetStatus("Reconectando à sala do grupo...");
                Log.Info($"Sala do grupo: a conexão caiu sem aviso ({e.GetBaseException().Message}). Reconectando...");
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                string why = ws.HttpStatusCode switch
                {
                    HttpStatusCode.Unauthorized => "a sessão não foi aceita (vou renová-la)",
                    HttpStatusCode.Forbidden => "você não é mais membro deste grupo",
                    HttpStatusCode.NotFound or HttpStatusCode.UpgradeRequired => "o servidor do grupo não achou a sala: quem cuida do Worker precisa publicar a versão nova (relay/LEIA-ME.md)",
                    0 => "sem resposta (" + e.GetBaseException().Message + ")",
                    var c => $"o servidor respondeu {(int)c}"
                };
                SetStatus("Sem sala do grupo: " + why + ". Ela volta sozinha quando der.");
                if (!loggedDown) Log.Info("Sala do grupo indisponível: " + why + ". Tento de novo sozinho.");
                loggedDown = true;
                if (ws.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.UpgradeRequired or HttpStatusCode.Forbidden)
                    delay = TimeSpan.FromSeconds(60);
                if (ws.HttpStatusCode == HttpStatusCode.Unauthorized) { _renovarToken = true; delay = TimeSpan.FromSeconds(5); }
            }
            catch { }
            finally
            {
                _ws = null;
                Connected = false;
                SetMembers(new List<Member>()); // quem estava fica na lista pela folga, enquanto reconecto
                Changed?.Invoke();
                ws.Dispose();
            }
            if (encerrou) break;
            try { await Task.Delay(delay, _cts.Token); } catch { break; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    bool _renovarToken;

    async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes("ping");
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                await _sendGate.WaitAsync(ct);
                try { await ws.SendAsync(ping, WebSocketMessageType.Text, true, ct); }
                finally { _sendGate.Release(); }
            }
        }
        catch { }
    }

    /// <summary>
    /// Teto de uma mensagem recebida da sala. O maior caso legítimo é o avatar de um amigo (até 256 KB de imagem, ~342 KB em base64);
    /// a lista de 50 membros e as 50 atividades somam uns 30 KB, a capa do filme tem até 32 KB e a miniatura 12 KB.
    /// 1 MiB dá mais de 2x de folga. Sem teto, um servidor (ou sala) mal-intencionado faria o MemoryStream crescer sem parar.
    /// </summary>
    const int MaxMensagemBytes = 1024 * 1024;

    /// <summary>Quando o último "envie a telemetria agora" foi atendido (o mínimo entre dois é <see cref="TelemetriaAgoraIntervalo"/>).</summary>
    DateTime _telemetriaAgoraEm = DateTime.MinValue;
    static readonly TimeSpan TelemetriaAgoraIntervalo = TimeSpan.FromSeconds(60);

    async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        var buf = new byte[16 * 1024];
        var msg = new MemoryStream();
        bool grande = false; // passou do teto: lê o resto sem guardar
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buf, _cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (!grande && msg.Length + r.Count > MaxMensagemBytes)
            {
                grande = true;
                msg.SetLength(0);
                Log.Info($"Sala do grupo: mensagem acima de {MaxMensagemBytes / 1024} KB descartada (a sala normal nunca manda isso).");
            }
            if (!grande) msg.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            if (grande) { grande = false; continue; }
            string text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
            msg.SetLength(0);
            if (text == "pong") continue;
            try { Handle(JsonNode.Parse(text) as JsonObject); }
            catch (Exception e) { Log.Info("Sala do grupo: mensagem inválida: " + e.Message); }
        }
    }

    void Handle(JsonObject? m)
    {
        switch (m?["t"]?.GetValue<string>())
        {
            case "atividades":
            {
                var itens = new List<Atividade>();
                foreach (var node in m["itens"]?.AsArray() ?? new JsonArray()) if (Atividade.Ler(node) is { } a) itens.Add(a);
                lock (_gate) _atividades = itens.OrderByDescending(a => a.Hora).ThenByDescending(a => a.Id).Take(AtividadesMax).ToList();
                AtividadesMudaram?.Invoke();
                break;
            }
            case "atividade":
            {
                if (Atividade.Ler(m["item"]) is not { } nova) break;
                lock (_gate)
                {
                    if (_atividades.Any(a => a.Id == nova.Id && a.Id != 0)) break;
                    _atividades = _atividades.Prepend(nova).OrderByDescending(a => a.Hora).ThenByDescending(a => a.Id).Take(AtividadesMax).ToList();
                }
                AtividadesMudaram?.Invoke();
                break;
            }
            case "membros":
                var list = new List<Member>();
                foreach (var node in m["membros"]?.AsArray() ?? new JsonArray())
                {
                    if (node is not JsonObject o || o["id"]?.GetValue<string>() is not string id) continue;
                    LiveInfo? live = null;
                    if (o["ao_vivo"] is JsonObject v && v["sala"]?.GetValue<string>() is string sala && sala != "")
                        live = new LiveInfo(sala, v["jogo"]?.GetValue<string>() ?? "", v["qualidade"]?.GetValue<string>() ?? "",
                            v["desde"]?.GetValue<long>() ?? 0, v["assistindo"]?.GetValue<int>() ?? 0);
                    list.Add(new Member(id, o["apelido"]?.GetValue<string>() ?? "amigo", o["versao"]?.GetValue<string>() ?? "", live));
                }
                // Mesmo id duas vezes: a sala manda fechar a conexão antiga quando a pessoa volta, mas ela ainda pode vir na lista
                // até o fechamento ser processado. Fica uma linha por pessoa (a que está ao vivo, senão a mais nova). Ids DIFERENTES
                // da mesma pessoa (o mesmo amigo 3x em 2026-11-08) não dá para juntar aqui: ver DESCOBERTAS.
                list = list.GroupBy(x => x.Id).Select(g => g.LastOrDefault(x => x.Live != null) ?? g.Last()).ToList();
                {
                    GrupoNome = m["grupo"]?["nome"]?.GetValue<string>() ?? GrupoNome;
                    var aus = new List<Ausente>();
                    foreach (var node in m["ausentes"]?.AsArray() ?? new JsonArray())
                        if (node is JsonObject a && a["id"]?.GetValue<string>() is string aid) aus.Add(new Ausente(aid, a["apelido"]?.GetValue<string>() ?? "amigo"));
                    lock (_gate) _ausentes = aus;
                }
                SetMembers(list);
                Changed?.Invoke();
                break;
            case "de":
                if (m["de"]?.GetValue<string>() is string from && m["msg"] is JsonObject body)
                    Message?.Invoke(from, m["apelido"]?.GetValue<string>() ?? "amigo", body);
                break;
            case "telemetria-agora":
                // qualquer membro pode pedir; sem este intervalo um app adulterado faria todos os PCs enviarem telemetria sem parar
                if (DateTime.UtcNow - _telemetriaAgoraEm < TelemetriaAgoraIntervalo)
                {
                    Log.Info("Sala do grupo: pedido de telemetria agora ignorado (já atendi um há menos de 60 s).");
                    break;
                }
                _telemetriaAgoraEm = DateTime.UtcNow;
                TelemetryNow?.Invoke();
                break;
            case "erro":
                if (m["motivo"]?.GetValue<string>() == "offline" && m["para"]?.GetValue<string>() is string to)
                    Undeliverable?.Invoke(to, m["msg"] as JsonObject);
                break;
        }
    }

    /// <summary>Troca a lista de quem está na sala; quem saiu vai para <see cref="_away"/> e some da tela depois da folga.</summary>
    void SetMembers(List<Member> now)
    {
        bool someoneLeft = false;
        lock (_gate)
        {
            var at = DateTime.Now;
            foreach (var old in _members)
                if (!now.Any(m => m.Id == old.Id)) { _away[old.Id] = (old, at); someoneLeft = true; }
            foreach (var m in now) _away.Remove(m.Id);
            foreach (var id in _away.Where(a => at - a.Value.GoneAt >= AwayGrace).Select(a => a.Key).ToList()) _away.Remove(id);
            _members = now;
        }
        // acabou a folga: avisa a tela para tirar quem não voltou
        if (someoneLeft && !_cts.IsCancellationRequested)
            _ = Task.Delay(AwayGrace + TimeSpan.FromMilliseconds(200), _cts.Token).ContinueWith(t => { if (!t.IsCanceled) Changed?.Invoke(); }, TaskScheduler.Default);
    }

    async Task SendAsync(JsonObject msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendGate.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch (Exception e) { Log.Info("Sala do grupo: envio falhou: " + e.Message); }
        finally { _sendGate.Release(); }
    }

    void SetStatus(string s)
    {
        Status = s;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        var ws = _ws;
        // fecha educadamente ANTES de cancelar (cancelar o recebimento aborta o socket): assim a sala tira a gente
        // da lista na hora, e o amigo não vê "online" nem "ao vivo" de quem já fechou o app
        if (ws != null)
        {
            // saindo ao vivo: avisa antes que a live acabou. Quem sai fica na lista dos amigos pela folga (AwayGrace) e,
            // sem isso, o cartão "ao vivo" de quem já fechou o app ficaria lá esses segundos
            if (_live != null)
            {
                _live = null;
                try { Task.Run(() => SendAsync(LiveMessage())).Wait(300); } catch { } // fora da thread da tela: o await dentro voltaria para ela, que está parada aqui

            }
            try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "saindo", CancellationToken.None).Wait(500); } catch { }
        }
        _cts.Cancel();
    }
}
