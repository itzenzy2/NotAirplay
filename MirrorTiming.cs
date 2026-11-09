using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NotAirplay;

internal sealed class MirrorTiming : IAsyncDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task send, receive;
    private IPEndPoint? currentPeer;

    public MirrorTiming(IPAddress local, IPEndPoint? peer, Action<string> report)
    {
        currentPeer = peer;
        udp = new UdpClient(new IPEndPoint(local, 0));
        const int SIO_UDP_CONNRESET = -1744830452;
        try { udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
        send = RunAsync(() => SendAsync(stopping.Token), report);
        receive = RunAsync(() => ReceiveAsync(stopping.Token), report);
    }

    public ushort Port => (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port;

    public void SetPeer(IPEndPoint peer) => Volatile.Write(ref currentPeer, peer);

    private async Task SendAsync(CancellationToken token)
    {
        var packet = new byte[32];
        packet[0] = 0x80;
        packet[1] = 0xd2;
        packet[3] = 7;
        while (!token.IsCancellationRequested)
        {
            var peer = Volatile.Read(ref currentPeer);
            if (peer is not null)
            {
                Stamp(packet.AsSpan(24));
                try { await udp.SendAsync(packet, peer, token).ConfigureAwait(false); }
                catch (SocketException) { }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            var packet = result.Buffer;
            if (packet.Length != 32)
                continue;

            Volatile.Write(ref currentPeer, result.RemoteEndPoint);
            byte type = packet[1];
            if (type is 0xd2 or 0x52)
            {
                packet[1] = 0xd3;
                packet.AsSpan(24, 8).CopyTo(packet.AsSpan(8));
                Stamp(packet.AsSpan(16));
                Stamp(packet.AsSpan(24));
                try { await udp.SendAsync(packet, result.RemoteEndPoint, token).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
    }

    private async Task RunAsync(Func<Task> operation, Action<string> report)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (SocketException ex) when (!stopping.IsCancellationRequested)
        { report($"AirPlay timing socket stopped ({ex.SocketErrorCode})."); }
        catch (SocketException) { }
    }

    private static void Stamp(Span<byte> bytes)
    {
        long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        uint seconds = unchecked((uint)(ticks / TimeSpan.TicksPerSecond + 2_208_988_800L));
        uint fraction = (uint)(((ulong)(ticks % TimeSpan.TicksPerSecond) << 32) / TimeSpan.TicksPerSecond);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, seconds);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], fraction);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        udp.Dispose();
        try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
        catch { }
        finally { stopping.Dispose(); }
    }
}
