using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotAirplay;

internal static class Smoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        string path = args.Length == 0 ? Path.GetFullPath("tests/fixtures") : args[0];
        TestProtocol(path).GetAwaiter().GetResult(); TestMdns(path); TestFairPlay().GetAwaiter().GetResult();
        TestMedia(path).GetAwaiter().GetResult(); TestFullReceiver(path).GetAwaiter().GetResult();
        LifecycleTests.Run(path).GetAwaiter().GetResult();
        AudioTests.Run(path).GetAwaiter().GetResult();
        if (args.Contains("--native")) { NativeTests.Run(path); UiTests.RunHosted(path); }
        RenderWindow(path, 960, 720);
        RenderWindow(path, 620, 520);
        UiTests.Run(path);
        return TestDiscovery().GetAwaiter().GetResult() ? 0 : 1;
    }

    private static async Task TestProtocol(string path)
    {
        var events = new List<string>();
        var identity = new ReceiverIdentity();
        string first = "OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 3\r\n\r\nAB";
        string rest = "GET /info?secret=private RTSP/1.0\r\nCSeq: 2\r\n\r\nPOST /fp-setup RTSP/1.0\r\nCSeq: 3\r\n\r\n";
        var stream = new ScriptedStream(first, "C", rest);
        await ControlConnection.HandleAsync(stream, identity, events.Add, CancellationToken.None);
        byte[] bytes = stream.Response.ToArray();
        string wire = Encoding.Latin1.GetString(bytes);
        if (!wire.StartsWith("RTSP/1.0 200 OK\r\nCSeq: 1") ||
            !wire.Contains("RTSP/1.0 200 OK\r\nCSeq: 2") ||
            !wire.Contains("RTSP/1.0 400 Bad Request\r\nCSeq: 3") ||
            !events.Contains("RTSP/1.0 GET /info") || !events.Contains("RTSP/1.0 POST /fp-setup") ||
            events.Any(message => message.Contains("secret") || message.Contains("private")))
            throw new Exception(wire);
        int plist = wire.IndexOf("bplist00", StringComparison.Ordinal);
        int end = wire.IndexOf("RTSP/1.0 400", StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(path, "metadata.plist"), bytes[plist..end]);

        var malformed = new ScriptedStream("GET /info RTSP/1.0\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n");
        await ControlConnection.HandleAsync(malformed, identity, events.Add, CancellationToken.None);
        if (malformed.Response.Length != 0 || !events.Contains("Malformed or oversized control request."))
            throw new Exception("Ambiguous framing was accepted.");

        var http = new ScriptedStream("GET /server-info HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await ControlConnection.HandleAsync(http, identity, events.Add, CancellationToken.None);
        string httpWire = Encoding.UTF8.GetString(http.Response.ToArray());
        if (!httpWire.Contains("text/x-apple-plist+xml") || !httpWire.Contains("<integer>4399430631415</integer>"))
            throw new Exception(httpWire);
        Console.WriteLine("PASS fragmented body, pipelined RTSP, HTTP metadata, malformed framing and private log fields");
    }

    private static async Task TestFairPlay()
    {
        var fixtures = new[] {"46504c59030102000000008202000f9f3f9e0a2521dbdf312ab2bfb29e8d232b6376a8c818701d22ae93d82737feaf9db4fdf41c2dba9d1f49caaabf6591ac1f7bc6f7e0663d21afe01565953eab81f418ceed095adb7c3d0e254909a79831d49c3982973434facb42c63a1cd911a6fe941a8a6d4a743b46c3a7649e44c78955e49d8155009549c4e2f7a3f6d5ba", "46504c5903010200000000820201cf32a25714b2524f8aa0ad7af164e37bcf4424e200047efc0ad67afcd95ded1c2730bb591b962ed63a9c4ded88ba8fc78de64d91ccfd5c7b56da88e31f5cceafc7431995a01665a54e1939d25b94db64b9e45d8d063e1e6af07e9656162b0efa404275ea5a44d9591c7256b9fbe6513898b80227721988571650942ad946688a", "46504c5903010200000000820202c169a352eeed35b18cdd9c58d64f16c1519a89eb5317bd0d4336cd68f638ff9d016a5b52b7fa9216b2b65482c78444118121a2c7fed83db7119e9182aad7d18c7063e2a457555910af9e0efc76347d164043807f581ee4fbe42ca9dedc1b5eb2a3aa3d2ecd59e7eee70b3629f22afd161d877353ddb99adc8e07006e56f850ce", "46504c59030102000000008202039001e1727e0f57f9f5880db104a6257a23f5cfff1abbe1e93045251afb97eb9fc0011ebe0f3a81df5b691d76acb2f7a5c708e3d328f56bb39dbde5f29c8a17f481487e3ae863c678325422e6f78e166d18aa7fd636258bce28726f661f738893ce44311e4be6c0535193e5ef72e8686233729c227d820c999445d89246c8c359"};
        var identity = new ReceiverIdentity();
        for (byte mode = 0; mode < 4; mode++)
        {
            var m1 = Convert.FromHexString("46504C590301010000000004020000BB");
            m1[14] = mode;
            var m3 = new byte[164];
            Convert.FromHexString("46504C590301030000000098").CopyTo(m3, 0);
            m3[12] = mode;
            for (int index = 144; index < m3.Length; index++)
                m3[index] = (byte)index;

            string Header(int length, int sequence) =>
                $"POST /fp-setup RTSP/1.0\r\nCSeq: {sequence}\r\nContent-Length: {length}\r\n\r\n";
            string m1Text = Encoding.Latin1.GetString(m1);
            string m3Text = Encoding.Latin1.GetString(m3);
            var stream = new ScriptedStream(Header(16, 1) + m1Text[..5], m1Text[5..],
                Header(164, 2) + m3Text + "SETUP rtsp://localhost/9 RTSP/1.0\r\nCSeq: 3\r\nContent-Length: 2\r\n\r\nok");
            var log = new List<string>();
            await ControlConnection.HandleAsync(stream, identity, log.Add, CancellationToken.None);
            string wire = Encoding.Latin1.GetString(stream.Response.ToArray());

            string HeaderAndBody(int sequence, int length, byte[] payload) =>
                $"RTSP/1.0 200 OK\r\nCSeq: {sequence}\r\nServer: AirTunes/220.68\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\n\r\n"
                + Encoding.Latin1.GetString(payload);
            byte[] expectedM4 = [..Convert.FromHexString("46504C590301040000000014"), ..m3.AsSpan(144)];
            if (!wire.StartsWith(HeaderAndBody(1, 142, Convert.FromHexString(fixtures[mode])) + HeaderAndBody(2, 32, expectedM4), StringComparison.Ordinal) ||
                !wire.Contains("RTSP/1.0 501 Not Implemented\r\nCSeq: 3") ||
                !log.Contains($"FairPlay v3.1 mode {mode} challenge sent.") ||
                !log.Contains("Stream SETUP received (2 bytes). Media key handling is pending."))
                throw new Exception($"Unexpected result for mode {mode}.");

            var outOfOrder = new ScriptedStream(Header(164, 1) + m3Text);
            await ControlConnection.HandleAsync(outOfOrder, identity, _ => { }, CancellationToken.None);
            if (!Encoding.ASCII.GetString(outOfOrder.Response.ToArray()).StartsWith("RTSP/1.0 400 Bad Request"))
                throw new Exception("M3 was accepted without M1 on this connection.");

            m3[12] = (byte)((mode + 1) % 4);
            var wrongMode = new ScriptedStream(Header(16, 1) + m1Text +
                Header(164, 2) + Encoding.Latin1.GetString(m3));
            await ControlConnection.HandleAsync(wrongMode, identity, _ => { }, CancellationToken.None);
            if (!Encoding.Latin1.GetString(wrongMode.Response.ToArray()).Contains("RTSP/1.0 400 Bad Request\r\nCSeq: 2"))
                throw new Exception("M3 was accepted with a different mode.");
        }
        Console.WriteLine("PASS exact captured M2 bytes for all four modes, fragmented RTSP, M4 echo, session isolation, mode mismatch, and SETUP boundary");
    }

    private static async Task TestMedia(string path)
    {
        const ulong id = 15850927297563084863;
        byte[] message = File.ReadAllBytes(Path.Combine(path, "m3.bin"));
        byte[] setup = File.ReadAllBytes(Path.Combine(path, "setup.bplist"));
        byte[] videoSetup = File.ReadAllBytes(Path.Combine(path, "stream-setup.bplist"));
        var identity = new ReceiverIdentity();
        using var onlyOne = new SemaphoreSlim(1);
        foreach (var (codec, width, height) in new[] { ("h264", 96, 64), ("hevc", 64, 96) })
        {
            byte[] input = File.ReadAllBytes(Path.Combine(path, codec + ".mirror"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int frames = 0;
            bool imageOk = false;
            await using var session = new MirrorSession(System.Net.IPAddress.Loopback, System.Net.IPAddress.Loopback,
                onlyOne, frame =>
                {
                    using (frame)
                    {
                        imageOk |= frame.Width == width && frame.Height == height &&
                            frame.Pixels.AsSpan(0, frame.Stride * frame.Height).ContainsAnyExcept((byte)0);
                        if (Interlocked.Increment(ref frames) == 12)
                            done.TrySetResult();
                    }
                }, Console.WriteLine);
            var response = new BinaryPropertyListReader(await session.SetupAsync(setup, message, identity, timeout.Token));
            if (response.Find(response.Root, "displays") is not int displays || response.ReadArray(displays).Length != 1)
                throw new Exception("Combined SETUP did not return display capabilities.");
            var answer = new BinaryPropertyListReader(await session.SetupAsync(videoSetup, message, identity, timeout.Token));
            int stream = answer.ReadArray(answer.Find(answer.Root, "streams")!.Value).Single();
            int port = (int)answer.ReadInteger(answer.Find(stream, "dataPort")!.Value);
            bool loopback = true;
            using var sender = new System.Net.Sockets.TcpClient();
            try
            {
                await sender.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
            {
                loopback = false;
                Console.WriteLine("Loopback client restricted. Feeding the same encrypted wire bytes from a stream.");
            }
            if (loopback)
            {
                await sender.GetStream().WriteAsync(input, timeout.Token);
                sender.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                await done.Task.WaitAsync(timeout.Token);
            }
            else
            {
                await session.DecodeStreamAsync(new MemoryStream(input), id, timeout.Token);
            }
            if (frames != 12 || !imageOk)
                throw new Exception($"Expected 12 decoded {codec} frames with nonzero pixels. Got {frames}.");
            Console.WriteLine($"PASS {codec} control SETUP, media key, encrypted packets, and {frames} decoded {width}x{height} frames");
        }
    }

    private static async Task TestFullReceiver(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        await using var receiver = await ReceiverProbe.StartAsync(log.Enqueue, frame =>
        {
            using (frame)
            {
                if (frame.Width == 96 && frame.Height == 64 && Interlocked.Increment(ref frames) == 12)
                    done.TrySetResult();
            }
        });

        using var control = new System.Net.Sockets.TcpClient();
        await control.ConnectAsync(System.Net.IPAddress.Loopback, receiver.Port, timeout.Token);
        using var tcp = control.GetStream();
        int sequence = 0;
        async Task<byte[]> Send(string method, string endpoint, byte[]? payload = null)
        {
            string header = $"{method} {endpoint} RTSP/1.0\r\nCSeq: {++sequence}\r\nContent-Length: {payload?.Length ?? 0}\r\n\r\n";
            await tcp.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
            if (payload is not null)
                await tcp.WriteAsync(payload, timeout.Token);
            using var response = new MemoryStream();
            byte[] one = new byte[1];
            uint window = 0;
            while (window != 0x0d0a0d0a)
            {
                await tcp.ReadExactlyAsync(one, timeout.Token);
                response.WriteByte(one[0]);
                window = (window << 8) | one[0];
            }
            string received = Encoding.ASCII.GetString(response.ToArray());
            if (!received.StartsWith($"RTSP/1.0 200 OK\r\nCSeq: {sequence}\r\n"))
                throw new Exception(received + "\n" + string.Join("\n", log));
            string length = received.Split("\r\n").Single(x => x.StartsWith("Content-Length: "));
            byte[] body = new byte[int.Parse(length[16..])];
            await tcp.ReadExactlyAsync(body, timeout.Token);
            return body;
        }

        byte[] m1 = Convert.FromHexString("46504C590301010000000004020001BB");
        byte[] m3 = File.ReadAllBytes(Path.Combine(path, "m3.bin"));
        if ((await Send("POST", "/fp-setup", m1)).Length != 142 ||
            (await Send("POST", "/fp-setup", m3)).Length != 32)
            throw new Exception("FairPlay response size was wrong.");

        using var timing = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int timingPort = ((System.Net.IPEndPoint)timing.Client.LocalEndPoint!).Port;
        var fixture = new BinaryPropertyListReader(File.ReadAllBytes(Path.Combine(path, "setup.bplist")));
        byte[] wrapped = fixture.ReadData(fixture.Find(fixture.Root, "ekey")!.Value).ToArray();
        byte[] setup = BinaryPropertyList.Write(new Dictionary<string, object>
        {
            ["ekey"] = wrapped,
            ["timingProtocol"] = "NTP",
            ["timingPort"] = (ulong)timingPort,
            ["combinedGetInfoWithControlSetup"] = true,
        });
        var controlSetup = new BinaryPropertyListReader(await Send("SETUP", "rtsp://localhost/1", setup));
        var timingPacket = await timing.ReceiveAsync(timeout.Token);
        if (timingPacket.Buffer.Length != 32 || timingPacket.Buffer[1] != 0xd2 ||
            controlSetup.ReadInteger(controlSetup.Find(controlSetup.Root, "timingPort")!.Value) != (ulong)timingPacket.RemoteEndPoint.Port)
            throw new Exception("Unexpected AirPlay timing query.");
        await Send("RECORD", "rtsp://localhost/1");
        await Send("POST", "/feedback");
        var response = new BinaryPropertyListReader(await Send("SETUP", "rtsp://localhost/1",
            File.ReadAllBytes(Path.Combine(path, "stream-setup.bplist"))));
        int stream = response.ReadArray(response.Find(response.Root, "streams")!.Value).Single();
        int port = (int)response.ReadInteger(response.Find(stream, "dataPort")!.Value);

        using var data = new System.Net.Sockets.TcpClient();
        await data.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
        await data.GetStream().WriteAsync(File.ReadAllBytes(Path.Combine(path, "h264.mirror")), timeout.Token);
        data.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        await done.Task.WaitAsync(timeout.Token);
        if (await tcp.ReadAsync(new byte[1], timeout.Token) != 0 ||
            !log.Any(line => line.StartsWith("First video frame decoded")))
            throw new Exception("Missing first frame or control was not released when the video ended.");
        Console.WriteLine("PASS full RTSP socket: two FairPlay messages, combined SETUP, UDP timing, RECORD, feedback, video SETUP, encrypted video, and disconnect");
    }

    private static void TestMdns(string path)
    {
        var records = new MdnsRecords(new ReceiverIdentity(), System.Net.IPAddress.Parse("192.168.12.34"), 12501);
        File.WriteAllBytes(Path.Combine(path, "announce.dns"), records.Announcement());
        File.WriteAllBytes(Path.Combine(path, "goodbye.dns"), records.Announcement(goodbye: true));
        byte[] airplay = [0x37, 0xc4, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
            8, .. "_airplay"u8, 4, .. "_tcp"u8, 5, .. "local"u8, 0, 0, 12, 0, 1];
        byte[] two = [.. airplay, 5, .. "_raop"u8, 0xc0, 21, 0, 12, 0x80, 1];
        two[5] = 2;
        File.WriteAllBytes(Path.Combine(path, "browse.dns"), records.Answer(two, false, out bool multi)!);
        if (multi)
            throw new Exception("Mixed QU/QM query should get a multicast response.");
        airplay[^2] = 0x80;
        var qu = records.Answer(airplay, false, out bool unicast);
        if (qu is null || !unicast)
            throw new Exception("QU query should get a unicast response.");
        File.WriteAllBytes(Path.Combine(path, "legacy.dns"), records.Answer(airplay, true, out bool legacy)!);
        if (!legacy)
            throw new Exception("Legacy query should get a unicast response.");
        byte[] malformed = [.. airplay[..12], 0xc0, 12, 0, 12, 0, 1];
        if (records.Answer(malformed, false, out _) is not null ||
            records.Answer(airplay.AsSpan(0, airplay.Length - 1), false, out _) is not null ||
            records.Answer(qu, false, out _) is not null)
            throw new Exception("Malformed packets or a response packet were treated as queries.");
        Console.WriteLine("PASS compressed paired-service query, QU/QM, legacy unicast and malformed mDNS input");
    }

    private static void RenderWindow(string path, int width, int height)
    {
        MainWindow.UseSavedSettings = false;
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var pixels = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(size));
            drawing.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) }, null, new Rect(size));
        }
        pixels.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(pixels));
        using var file = File.Create(Path.Combine(path, $"window-{width}.png"));
        png.Save(file);
        Console.WriteLine($"PASS WPF layout rendered at {width}x{height}");
    }

    private static async Task<bool> TestDiscovery()
    {
        try
        {
            for (int cycle = 0; cycle < 2; cycle++)
            {
                var native = await ReceiverProbe.StartAsync(Console.WriteLine).WaitAsync(TimeSpan.FromSeconds(8));
                Console.WriteLine($"PASS discovery start (TCP port {native.Port}). {native.DiscoverySummary}");
                await native.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                Console.WriteLine("PASS discovery stop");
            }
            return true;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Discovery lifecycle check failed: {error}");
            return false;
        }
    }

    private sealed class ScriptedStream(params string[] chunks) : MemoryStream
    {
        private readonly Queue<byte[]> input = new(chunks.Select(Encoding.Latin1.GetBytes));
        public MemoryStream Response { get; } = new();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!input.TryDequeue(out var chunk))
                return ValueTask.FromResult(0);
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => Response.WriteAsync(buffer, cancellationToken);
    }
}







