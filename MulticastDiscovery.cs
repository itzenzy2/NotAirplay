using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NotAirplay;

internal sealed class MulticastDiscovery : IAsyncDisposable
{
    private const int Port = 5353;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(Group, Port);
    private readonly Socket socket;
    private readonly Action<string> report;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<int, Adapter> adapters = new();
    private readonly object sending = new();
    private Task receiving = Task.CompletedTask;
    private Task announcing = Task.CompletedTask;
    private int queries, replies, announcements, sendFailures;

    private MulticastDiscovery(Socket socket, Action<string> report)
    {
        this.socket = socket;
        this.report = report;
    }

    public string Summary => $"IPv4 mDNS on {adapters.Count} adapter(s). " +
        $"Announcements {Volatile.Read(ref announcements)}, matching queries {Volatile.Read(ref queries)}, " +
        $"replies {Volatile.Read(ref replies)}, send errors {Volatile.Read(ref sendFailures)}.";

    public static MulticastDiscovery Start(ReceiverIdentity identity, ushort port, Action<string> report)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));

            var discovery = new MulticastDiscovery(socket, report);
            try
            {
                discovery.JoinAdapters(identity, port);
                discovery.Announce();
                if (discovery.announcements == 0)
                    throw new InvalidOperationException("No IPv4 multicast announcement could be sent.");

                discovery.receiving = discovery.ReceiveAsync();
                discovery.announcing = discovery.AnnounceAsync();
                return discovery;
            }
            catch
            {
                discovery.stopping.Dispose();
                throw;
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void JoinAdapters(ReceiverIdentity identity, ushort port)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast ||
                !adapter.Supports(NetworkInterfaceComponent.IPv4) ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var properties = adapter.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                var address = properties.UnicastAddresses.FirstOrDefault(entry =>
                    entry.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(entry.Address));
                if (ipv4 is null || address is null || adapters.ContainsKey(ipv4.Index))
                    continue;

                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(Group, address.Address));
                adapters.Add(ipv4.Index, new Adapter(address.Address, address.IPv4Mask,
                    new MdnsRecords(identity, address.Address, port)));
            }
            catch (SocketException ex)
            {
                report($"Could not join mDNS on one adapter ({ex.SocketErrorCode}).");
            }
            catch (NetworkInformationException ex)
            {
                report($"Could not read one adapter's network configuration ({ex.ErrorCode}).");
            }
        }
    }

    private async Task ReceiveAsync()
    {
        var token = stopping.Token;
        var buffer = new byte[4096];
        var any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!token.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult received;
                try
                {
                    received = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, token)
                        .ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
                {
                    continue;
                }
                if ((received.SocketFlags & SocketFlags.Truncated) != 0 ||
                    !adapters.TryGetValue(received.PacketInformation.Interface, out var adapter))
                    continue;

                var sender = (IPEndPoint)received.RemoteEndPoint;
                if (!adapter.IsOnLink(sender.Address) || adapters.Values.Any(local => local.Address.Equals(sender.Address)))
                    continue;

                byte[]? answer = adapter.Records.Answer(buffer.AsSpan(0, received.ReceivedBytes),
                    sender.Port != Port, out bool unicast);
                if (answer is null)
                    continue;

                int count = Interlocked.Increment(ref queries);
                bool sent = Send(adapter, answer, unicast ? sender : MulticastEndpoint);
                if (sent)
                    Interlocked.Increment(ref replies);
                if (count <= 3)
                    report(sent ? "Received an AirPlay mDNS query and sent a reply." : "Received an AirPlay mDNS query; reply failed.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException ex)
        {
            report($"mDNS receive loop stopped ({ex.SocketErrorCode}).");
        }
    }

    private async Task AnnounceAsync()
    {
        var token = stopping.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                Announce();
                await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Announce(bool goodbye = false)
    {
        foreach (var adapter in adapters.Values)
        {
            if (Send(adapter, adapter.Records.Announcement(goodbye), MulticastEndpoint) && !goodbye)
                Interlocked.Increment(ref announcements);
        }
    }

    private bool Send(Adapter adapter, byte[] packet, IPEndPoint endpoint)
    {
        lock (sending)
        {
            try
            {
                if (endpoint == MulticastEndpoint)
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.Address.GetAddressBytes());
                socket.SendTo(packet, endpoint);
                return true;
            }
            catch (SocketException ex)
            {
                if (Interlocked.Increment(ref sendFailures) == 1)
                    report($"mDNS send failed ({ex.SocketErrorCode}).");
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try
        {
            await Task.WhenAll(receiving, announcing).ConfigureAwait(false);
            Announce(goodbye: true);
        }
        finally
        {
            socket.Dispose();
            stopping.Dispose();
        }
    }

    private sealed record Adapter(IPAddress Address, IPAddress Mask, MdnsRecords Records)
    {
        public bool IsOnLink(IPAddress sender)
        {
            var remote = sender.GetAddressBytes();
            var local = Address.GetAddressBytes();
            var mask = Mask.GetAddressBytes();
            for (int index = 0; index < local.Length; index++)
            {
                if ((remote[index] & mask[index]) != (local[index] & mask[index]))
                    return false;
            }
            return true;
        }
    }
}
