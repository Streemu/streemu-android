using System.Net;
using System.Net.Sockets;
using Streemu.Util;

namespace Streemu.Net;

/// <summary>
/// Socket UDP em 127.0.0.1 que liga um processo local (ffmpeg ou ffplay, falando SRT) ao túnel.
/// Pacotes do processo saem por <see cref="Outgoing"/>; pacotes do túnel entram por <see cref="Deliver"/>.
/// </summary>
public sealed class LocalRelay : IDisposable
{
    const int SIO_UDP_CONNRESET = -1744830452;
    readonly Socket _sock;
    readonly string _name;
    volatile IPEndPoint? _learned;

    public int Port { get; }
    /// <summary>Destino fixo (ffmpeg em modo listener). Se nulo, responde para quem mandou por último (ffplay em modo caller).</summary>
    public IPEndPoint? FixedTarget { get; set; }
    public Action<ReadOnlySpan<byte>>? Outgoing;
    public event Action? PacketFromProcess;

    public LocalRelay(string name)
    {
        _name = name;
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // ver o comentário igual no Tunnel.cs: só o Windows precisa (e só o Windows tem) o SIO_UDP_CONNRESET
        if (OperatingSystem.IsWindows()) _sock.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        _sock.ReceiveBufferSize = 4 * 1024 * 1024;
        _sock.SendBufferSize = 4 * 1024 * 1024;
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        new Thread(Loop) { IsBackground = true, Name = "relay-" + name }.Start();
    }

    public void Deliver(ReadOnlySpan<byte> data)
    {
        var target = FixedTarget ?? _learned;
        if (target == null) return;
        try { _sock.SendTo(data, SocketFlags.None, target); } catch { }
    }

    void Loop()
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int n;
            try { n = _sock.ReceiveFrom(buf, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (FixedTarget == null) _learned = (IPEndPoint)from;
            PacketFromProcess?.Invoke();
            Outgoing?.Invoke(buf.AsSpan(0, n));
        }
        Log.Info($"relay {_name} encerrado");
    }

    public void Dispose() { try { _sock.Dispose(); } catch { } }

    public static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }
}
