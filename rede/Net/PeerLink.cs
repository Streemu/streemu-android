using System.Net;
using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Uma ligação com UM amigo para UMA transmissão, combinada pela sala do grupo (sem colar código).
/// Cada ligação tem o seu próprio <see cref="Tunnel"/> (socket, porta, segredo e código novos) e o seu <see cref="LocalRelay"/>.
/// Por que um túnel por ligação, e não vários amigos no mesmo túnel: o protocolo do túnel (punch, ping por candidato,
/// relay, seen_as) já foi validado em campo para um par; repetir ele por espectador não muda nada do que funciona,
/// deixa vários amigos assistirem a mesma transmissão e um amigo assistir duas ao mesmo tempo (multistream).
/// </summary>
public sealed class PeerLink : IDisposable
{
    static RelayConfig.Credentials? s_creds;
    static DateTime s_credsAt;
    static readonly SemaphoreSlim s_credsGate = new(1, 1);

    public string SessionId { get; }
    public string PeerId { get; }
    public string PeerNick { get; set; }
    /// <summary>true: eu transmito para ele; false: eu assisto ele.</summary>
    public bool IAmSender { get; }
    public Tunnel Tunnel { get; }
    public LocalRelay Relay { get; }
    public string MyCode { get; private set; } = "";
    public DateTime CreatedAt { get; } = DateTime.Now;
    /// <summary>Id da transmissão (o do link do Discord) que esta ligação assiste.</summary>
    public string StreamId { get; init; } = "";

    public event Action<JsonObject>? Control;

    PeerLink(string sessionId, string peerId, string peerNick, bool iAmSender, bool localMode)
    {
        SessionId = sessionId; PeerId = peerId; PeerNick = peerNick; IAmSender = iAmSender;
        Tunnel = new Tunnel(localMode) { CodesExchangedByApp = true };
        Relay = new LocalRelay((iAmSender ? "envio-" : "player-") + sessionId[..Math.Min(6, sessionId.Length)]);
        if (iAmSender)
        {
            Tunnel.ToSender = d => Relay.Deliver(d);
            Relay.Outgoing = d => Tunnel.SendMedia(Tunnel.TToViewer, d);
        }
        else
        {
            Tunnel.ToViewer = d => Relay.Deliver(d);
            Relay.Outgoing = d => Tunnel.SendMedia(Tunnel.TToSender, d);
        }
        Tunnel.Control += m => Control?.Invoke(m);
    }

    /// <summary>
    /// Cria a ligação e prepara o meu código: STUN, endereço dentro da operadora (o mesmo IP do código principal,
    /// com a porta deste túnel) e relay de reserva, se o grupo tiver.
    /// </summary>
    public static async Task<PeerLink> CreateAsync(string sessionId, string peerId, string peerNick, bool iAmSender,
        bool localMode, string nick, IPAddress? carrierIp, RelayConfig.Settings? relay, string streamId = "")
    {
        var link = new PeerLink(sessionId, peerId, peerNick, iAmSender, localMode) { StreamId = streamId };
        link.Tunnel.Nick = nick;
        try
        {
            var pub = await link.Tunnel.DiscoverAsync();
            var alt = carrierIp != null ? new IPEndPoint(carrierIp, link.Tunnel.LocalPort) : null;
            IPEndPoint? relayed = null;
            if (relay != null && !localMode)
            {
                try
                {
                    var creds = await CredentialsAsync(relay);
                    relayed = await link.Tunnel.EnableRelayAsync(creds.Server, creds.Username, creds.Password).WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception e) { Log.Info($"Ligação com {peerNick}: relay de reserva não ligou ({e.Message}); segue só direto."); }
            }
            link.MyCode = new PeerCode(pub.Address, pub.Port, link.Tunnel.Secret, alt, relayed).Encode();
            return link;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    /// <summary>Credencial do relay reaproveitada entre ligações (vale 12 h; pedimos de novo depois de 1 h).</summary>
    static async Task<RelayConfig.Credentials> CredentialsAsync(RelayConfig.Settings cfg)
    {
        await s_credsGate.WaitAsync();
        try
        {
            if (s_creds == null || DateTime.Now - s_credsAt > TimeSpan.FromHours(1))
            {
                s_creds = await RelayConfig.FetchAsync(cfg);
                s_credsAt = DateTime.Now;
            }
            return s_creds;
        }
        finally { s_credsGate.Release(); }
    }

    public void Connect(string peerCode) => Tunnel.ConnectTo(PeerCode.Parse(peerCode));

    public bool Connected => Tunnel.State == TunnelState.Connected;

    public void Dispose()
    {
        try { Tunnel.Dispose(); } catch { }
        try { Relay.Dispose(); } catch { }
    }
}
