using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace NotAirplay;

internal sealed class ReceiverProbe : IAsyncDisposable
{
    private const int MaxClients = 4;
    private readonly ReceiverIdentity identity;
    private readonly TcpListener listener = new(IPAddress.IPv6Any, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim activeMirror = new(1, 1);
    private readonly Action<string> report;
    private readonly Action<VideoFrame> present;
    private readonly VideoTarget? target;
    private readonly Action<MirrorUpdate>? changed;
    private readonly List<DiscoveryRegistration> nativeDiscovery = new(2);
    private MulticastDiscovery? multicast;
    private Task accepting = Task.CompletedTask;
    private readonly ConcurrentDictionary<MirrorSession, byte> sessions = new();

    public void StopMirroring()
    {
        foreach (var session in sessions.Keys) session.RequestStop();
    }

    public event Action<float>? VolumeChanged;
    public event Action<string>? TrackChanged;

    public void SetMute(bool mute)
    {
        foreach (var session in sessions.Keys) session.SetMute(mute);
    }

    public void SetVolume(float volume)
    {
        foreach (var session in sessions.Keys) session.SetVolume(volume);
    }

    private ReceiverProbe(Action<string> report, Action<VideoFrame> present, VideoTarget? target, Action<MirrorUpdate>? changed)
    {
        this.report = report;
        this.present = present;
        this.target = target;
        identity = new ReceiverIdentity();
        this.changed = changed;
    }

    public ushort Port => (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
    public string DiscoverySummary => multicast?.Summary ?? "Windows DNS-SD registered both services. Packet counters unavailable.";

    public static async Task<ReceiverProbe> StartAsync(Action<string> report, Action<VideoFrame>? present = null,
        VideoTarget? target = null, Action<MirrorUpdate>? changed = null)
    {
        var probe = new ReceiverProbe(report, present ?? (frame => frame.Dispose()), target, changed);
        try
        {
            probe.listener.Server.DualMode = true;
            probe.listener.ExclusiveAddressUse = true;
            probe.listener.Start(MaxClients);
            probe.accepting = probe.AcceptAsync();
            await probe.StartDiscoveryAsync().ConfigureAwait(false);
            return probe;
        }
        catch
        {
            await probe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        try
        {
            if (multicast is not null)
                await multicast.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(nativeDiscovery.Select(service => service.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            await accepting.ConfigureAwait(false);
            activeMirror.Dispose();
            stopping.Dispose();
        }
    }

    private async Task StartDiscoveryAsync()
    {
        try
        {
            multicast = await Task.Run(() => MulticastDiscovery.Start(identity, Port, report)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or InvalidOperationException)
        {
            report($"Direct mDNS unavailable. Trying Windows DNS-SD. {ex.Message}");
            nativeDiscovery.Add(await DiscoveryRegistration.RegisterAsync(identity.AirPlay, Port).ConfigureAwait(false));
            nativeDiscovery.Add(await DiscoveryRegistration.RegisterAsync(identity.Raop, Port).ConfigureAwait(false));
        }
    }

    private async Task AcceptAsync()
    {
        var clients = new List<Task>(MaxClients);
        var token = stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                clients.RemoveAll(task => task.IsCompleted);
                if (clients.Count == MaxClients)
                {
                    client.Dispose();
                    continue;
                }
                clients.Add(HandleAsync(client, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException ex) when (token.IsCancellationRequested || ex.SocketErrorCode == SocketError.OperationAborted)
        { }
        catch (SocketException ex)
        {
            report($"Control listener stopped ({ex.SocketErrorCode}).");
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                report("Incoming connection.");
                var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                await using var mirror = new MirrorSession(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local,
                    remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote, activeMirror, present, report, target, changed);
                mirror.VolumeChanged += v => VolumeChanged?.Invoke(v);
                mirror.TrackChanged += t => TrackChanged?.Invoke(t);
                try
                {
                    sessions.TryAdd(mirror, 0);
                    await ControlConnection.HandleAsync(client.GetStream(), identity, report, token, mirror).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (mirror.Ended.IsCancellationRequested) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    mirror.Fail("The iPhone stopped responding. Reconnect to resume mirroring.");
                }
                catch (IOException) when (!token.IsCancellationRequested)
                {
                    mirror.Fail("The connection to your iPhone was lost.");
                }
                finally { sessions.TryRemove(mirror, out _); }
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                    report("Control request timed out.");
            }
            catch (SocketException) { }
            catch (IOException) { }
        }
    }
}
