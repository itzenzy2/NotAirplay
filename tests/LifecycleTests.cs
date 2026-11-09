using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NotAirplay;

internal static class LifecycleTests
{
    public static async Task Run(string path)
    {
        var message = File.ReadAllBytes(Path.Combine(path, "m3.bin"));
        var setup = File.ReadAllBytes(Path.Combine(path, "setup.bplist"));
        var streamSetup = File.ReadAllBytes(Path.Combine(path, "stream-setup.bplist"));
        var payload = File.ReadAllBytes(Path.Combine(path, "h264.mirror"));
        using var gate = new SemaphoreSlim(1);
        // Repeat on the same admission gate: phone EOF, local stop, broken data packet.
        for (int cycle = 0; cycle < 4; cycle++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var updates = new ConcurrentQueue<MirrorUpdate>();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new MirrorSession(IPAddress.Loopback, IPAddress.Loopback, gate,
                f => { f.Dispose(); first.TrySetResult(); }, _ => { },
                target: cycle == 3 ? new VideoTarget(-1) : null, changed: updates.Enqueue);
            try
            {
                await session.SetupAsync(setup, message, new ReceiverIdentity(), deadline.Token);
                var response = new BinaryPropertyListReader(await session.SetupAsync(streamSetup, message, new ReceiverIdentity(), deadline.Token));
                int item = response.ReadArray(response.Find(response.Root, "streams")!.Value).Single();
                int port = (int)response.ReadInteger(response.Find(item, "dataPort")!.Value);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                await client.GetStream().WriteAsync(payload, deadline.Token);
                await first.Task.WaitAsync(deadline.Token); // Live output, without closing input.
                if (cycle is 0 or 3) client.Client.Shutdown(SocketShutdown.Send);
                else if (cycle == 1) session.RequestStop();
                else
                {
                    var bad = new byte[128]; bad[3] = 1; // Oversized frame.
                    await client.GetStream().WriteAsync(bad, deadline.Token);
                }
                var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = session.Ended.Register(() => end.TrySetResult());
                await end.Task.WaitAsync(deadline.Token);
            }
            finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
            var states = updates.Select(u => u.State).ToArray();
            var last = cycle == 2 ? MirrorState.Failed : MirrorState.Stopped;
            if (!states.SequenceEqual(new[] { MirrorState.Connecting, MirrorState.Streaming, last }) || gate.CurrentCount != 1)
                throw new Exception("Session lifecycle or admission release failed: " + string.Join(", ", states));
        }
        Console.WriteLine("PASS typed lifecycle: phone stop, local stop, malformed stream, native failure fallback, first frame before EOF, immediate reconnect");
    }
}
