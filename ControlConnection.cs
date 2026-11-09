using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NotAirplay;

internal static class ControlConnection
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private const int MaxRequests = 16;
    private static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;

    public static async Task HandleAsync(Stream stream, ReceiverIdentity identity, Action<string> report,
        CancellationToken stopping, MirrorSession? mirror = null)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int count = 0;
        using var fairPlay = new FairPlayProbe();
        using var sessionStop = mirror is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stopping, mirror.Ended);
        var connectionToken = sessionStop?.Token ?? stopping;

        for (int request = 0; request < MaxRequests || mirror?.IsSetUp == true; request++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            if (mirror?.IsSetUp != true)
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            int separator = buffer.AsSpan(0, count).IndexOf(HeaderEnd);
            while (separator < 0 && count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0)
                    return;
                count += read;
                separator = buffer.AsSpan(0, count).IndexOf(HeaderEnd);
                if (count > 0 && mirror?.IsSetUp == true)
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
            }

            if (separator < 0 || !TryParse(buffer.AsSpan(0, separator), out var header))
            {
                report("Malformed or oversized control request.");
                return;
            }

            int consumed = separator + HeaderEnd.Length;
            int bufferedBody = Math.Min(header.Length, count - consumed);
            int remainingBody = header.Length - bufferedBody;
            bool isFairPlay = header is { Method: "POST", Route: "/fp-setup", Protocol: "RTSP/1.0" };
            bool isSetup = mirror is not null && header is { Method: "SETUP", Protocol: "RTSP/1.0" };
            bool wantsBody = (isFairPlay && header.Length <= FairPlayProbe.MaxRequestSize) ||
                             isSetup ||
                             header.Method is "TEARDOWN" or "SET_PARAMETER" or "POST";
            byte[]? requestBody = wantsBody ? new byte[header.Length] : null;
            if (requestBody is not null)
                buffer.AsSpan(consumed, bufferedBody).CopyTo(requestBody);
            consumed += bufferedBody;
            count -= consumed;
            buffer.AsSpan(consumed, count).CopyTo(buffer);

            byte[]? discardBuffer = null;
            while (remainingBody > 0)
            {
                Memory<byte> destination;
                if (requestBody is not null)
                {
                    destination = requestBody.AsMemory(header.Length - remainingBody);
                }
                else
                {
                    discardBuffer ??= new byte[4096];
                    destination = discardBuffer.AsMemory(0, Math.Min(remainingBody, discardBuffer.Length));
                }
                int read = await stream.ReadAsync(destination, token).ConfigureAwait(false);
                if (read == 0)
                    return;
                remainingBody -= read;
            }

            if (header.Route != "/feedback")
                report($"{header.Protocol} {header.Method} {header.Route}");
            bool options = header.Method == "OPTIONS";
            byte[]? payload = (header.Method, header.Route) switch
            {
                ("GET", "/info") => identity.Info,
                ("GET", "/server-info") => identity.ServerInfo,
                ("POST", "/feedback") => BinaryPropertyList.Write(new Dictionary<string, object>()),
                _ => null,
            };
            string? diagnostic = null;
            string status = options || payload is not null ? "200 OK" : "501 Not Implemented";
            bool closeSession = false;
            if (isFairPlay)
                (status, payload, diagnostic) = fairPlay.Reply(requestBody, header.Length);
            else if (isSetup)
            {
                try
                {
                    payload = await mirror!.SetupAsync(requestBody!, fairPlay.KeyMessage, identity, token)
                        .ConfigureAwait(false);
                    status = "200 OK";
                }
                catch (InvalidDataException ex)
                {
                    mirror!.Fail(ex.Message);
                    status = "400 Bad Request";
                    diagnostic = $"Stream SETUP rejected. {ex.Message}";
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException or Win32Exception)
                {
                    mirror!.Fail(ex.Message);
                    status = "503 Service Unavailable";
                    diagnostic = $"Stream SETUP failed. {ex.Message}";
                }
                finally { CryptographicOperations.ZeroMemory(requestBody!); }
            }
            else if (mirror?.IsSetUp == true && header.Protocol == "RTSP/1.0")
            {
                switch (header.Method, header.Route)
                {
                    case ("GET_PARAMETER", _):
                        payload = Encoding.ASCII.GetBytes($"volume: {mirror.VolumeDb:F6}\r\n");
                        status = "200 OK";
                        break;
                    case ("SET_PARAMETER", _):
                        if (requestBody is { Length: > 0 }) mirror.HandleSetParameter(requestBody);
                        status = "200 OK";
                        break;
                    case ("POST", _):
                        if (requestBody is { Length: > 0 }) mirror.HandlePost(requestBody, header.Route);
                        status = "200 OK";
                        break;
                    case ("RECORD" or "FLUSH", _):
                        if (header.Method == "RECORD") mirror.HandleRecord();
                        status = "200 OK";
                        break;
                    case ("TEARDOWN", _):
                        status = "200 OK";
                        try { closeSession = await mirror.TeardownAsync(requestBody).ConfigureAwait(false); }
                        catch (InvalidDataException) { status = "400 Bad Request"; }
                        break;
                }
            }
            else if (header.Method == "SETUP")
                diagnostic = $"Stream SETUP received ({header.Length} bytes). Media key handling is pending.";

            if (isFairPlay && requestBody is not null)
                CryptographicOperations.ZeroMemory(requestBody);

            string sequence = header.Sequence is int value ? $"CSeq: {value}\r\n" : "";
            string session = (isSetup || mirror?.IsSetUp == true) && header.Protocol == "RTSP/1.0" ? "Session: 1\r\n" : "";
            string publicMethods = options && header.Protocol == "RTSP/1.0" ? "Public: OPTIONS, GET, POST\r\n" : "";
            string close = header.Protocol == "HTTP/1.1" ? "Connection: close\r\n" : "";
            string type = payload is null ? "" : (header.Method, header.Route) switch
            {
                ("POST", "/fp-setup") => "Content-Type: application/octet-stream\r\n",
                ("SETUP", _) or (_, "/info") or (_, "/feedback") => "Content-Type: application/x-apple-binary-plist\r\n",
                ("GET_PARAMETER", _) => "Content-Type: text/parameters\r\n",
                _ => "Content-Type: text/x-apple-plist+xml\r\n",
            };
            string server = header.Protocol == "RTSP/1.0" ? $"Server: AirTunes/{ReceiverIdentity.SourceVersion}\r\n" : "";
            string audio = isSetup && status == "200 OK" ? "Audio-Jack-Status: connected; type=digital\r\n" : "";
            byte[] response = Encoding.ASCII.GetBytes(
                $"{header.Protocol} {status}\r\n{sequence}{session}{publicMethods}{close}{server}{audio}{type}Content-Length: {payload?.Length ?? 0}\r\n\r\n");
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            if (payload is not null)
                await stream.WriteAsync(payload, token).ConfigureAwait(false);
            if (diagnostic is not null)
                report(diagnostic);
            if (closeSession || (status != "200 OK" && (isSetup || mirror?.IsSetUp != true)) || close.Length != 0)
                return;
        }
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, out RequestHeader header)
    {
        header = default;
        string[] lines = Encoding.ASCII.GetString(bytes).Split("\r\n");
        string[] request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || request[0].Length is 0 or > 24 ||
            !request[0].All(character => character is >= 'A' and <= 'Z' or '_' or '-') ||
            request[2] is not ("RTSP/1.0" or "HTTP/1.1"))
            return false;

        int? length = null;
        int? sequence = null;
        foreach (string line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line.AsSpan(0, colon).Trim();
            var value = line.AsSpan(colon + 1).Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length.HasValue)
                {
                    if (!TryParseNumber(value, out int second) || second != length.Value)
                        return false;
                }
                else
                {
                    if (!TryParseNumber(value, out int body) || body > MaxBodyBytes)
                        return false;
                    length = body;
                }
            }
            else if (name.Equals("CSeq", StringComparison.OrdinalIgnoreCase))
            {
                if (sequence.HasValue)
                {
                    if (!TryParseNumber(value, out int second) || second != sequence.Value)
                        return false;
                }
                else
                {
                    if (!TryParseNumber(value, out int parsed))
                        return false;
                    sequence = parsed;
                }
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Log known protocol endpoints only. Targets can contain device or session identifiers.
        string target = request[1].Split('?', 2)[0];
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            uri.Scheme is "rtsp" or "http")
            target = uri.AbsolutePath;
        string route = target switch
        {
            "*" or "/info" or "/server-info" or "/pair-pin-start" or "/pair-setup" or "/pair-verify" or
                "/fp-setup" or "/fp-setup2" or "/feedback" or "/command" or "/audioMode" or "/audio-metadata" => target,
            _ => "(other endpoint)",
        };
        header = new(request[0], route, request[2], length ?? 0, sequence);
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private readonly record struct RequestHeader(string Method, string Route, string Protocol, int Length, int? Sequence);
}
