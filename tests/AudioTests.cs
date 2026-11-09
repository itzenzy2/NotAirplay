using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using NotAirplay;

internal static class AudioTests
{
    public static async Task Run(string path)
    {
        byte[] key = Enumerable.Range(0,16).Select(x=>(byte)x).ToArray();
        byte[] iv = Enumerable.Range(16,16).Select(x=>(byte)x).ToArray();
        // Independent encryption and expected RFC3640 packet layout, across non-block-aligned payloads.
        using var gate = new SemaphoreSlim(1);
        var session = new MirrorSession(IPAddress.Loopback, IPAddress.Loopback, gate, _ => { }, _ => { });
        float reportedVol = -1;
        session.VolumeChanged += v => reportedVol = v;
        session.HandleSetParameter(System.Text.Encoding.ASCII.GetBytes("volume: -15.000000\r\n"));
        if (Math.Abs(session.VolumeDb - (-15f)) > 0.01f || Math.Abs(reportedVol - 0.5f) > 0.01f)
            throw new Exception($"Volume handling failed: {session.VolumeDb}, {reportedVol}");
        session.HandleSetParameter(System.Text.Encoding.ASCII.GetBytes("volume: 0.000000\r\n"));
        if (Math.Abs(session.VolumeDb - 0f) > 0.01f || Math.Abs(reportedVol - 1.0f) > 0.01f)
            throw new Exception($"Volume 100% handling failed: {session.VolumeDb}, {reportedVol}");
        session.HandleSetParameter(System.Text.Encoding.ASCII.GetBytes("volume: -144.000000\r\n"));
        if (Math.Abs(session.VolumeDb - (-144f)) > 0.01f || reportedVol != 0f)
            throw new Exception($"Mute volume handling failed: {session.VolumeDb}, {reportedVol}");

        string trackInfo = "";
        session.TrackChanged += t => trackInfo = t;
        byte[] metaPlist = BinaryPropertyList.Write(new Dictionary<string, object> { ["title"] = "Test Title", ["artist"] = "Test Artist" });
        session.HandlePost(metaPlist, "/audio-metadata");
        if (trackInfo != "Test Title — Test Artist")
            throw new Exception($"Metadata handling failed: {trackInfo}");

        // DMAP metadata test in SET_PARAMETER (as sent by iOS AirPlay)
        byte[] titleBytes = System.Text.Encoding.UTF8.GetBytes("Impulsive");
        byte[] artistBytes = System.Text.Encoding.UTF8.GetBytes("Thekidszn");
        using var ms = new MemoryStream();
        ms.Write("minm"u8);
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, titleBytes.Length);
        ms.Write(lenBuf);
        ms.Write(titleBytes);
        ms.Write("asar"u8);
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, artistBytes.Length);
        ms.Write(lenBuf);
        ms.Write(artistBytes);

        trackInfo = "";
        session.HandleSetParameter(ms.ToArray());
        if (trackInfo != "Impulsive — Thekidszn")
            throw new Exception($"DMAP metadata in SET_PARAMETER failed: '{trackInfo}'");

        byte[] largeArtwork = new byte[256 * 1024];
        largeArtwork[0] = 0xFF; largeArtwork[1] = 0xD8; largeArtwork[2] = 0xFF;
        session.HandleSetParameter(largeArtwork);

        byte[] audioTeardown = BinaryPropertyList.Write(new Dictionary<string, object>
        {
            ["streams"] = new object[] { new Dictionary<string, object> { ["type"] = 96UL } }
        });
        bool shouldStayOpen = !(await session.TeardownAsync(audioTeardown));
        if (!shouldStayOpen)
            throw new Exception("Audio stream pause should not close the persistent control connection.");
        if (trackInfo != "")
            throw new Exception($"Audio stream pause should clear track info; got: '{trackInfo}'");

        bool globalTeardownCloses = await session.TeardownAsync(null);
        if (!globalTeardownCloses)
            throw new Exception("Global teardown should close the session.");

        foreach (int length in new[] {1,15,16,17,511})
        {
            byte[] plain = Enumerable.Range(0,length).Select(x=>(byte)(x*7)).ToArray();
            byte[] packet = Packet(plain, key, iv, 17, 12345);
            byte[] output = MirrorAudio.DecodePacket(packet,key,iv);
            if (!output.AsSpan(16).SequenceEqual(plain) || output[1] != 97 ||
                BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(14)) != length*8)
                throw new Exception("Audio decryption/AU header mismatch.");
        }
        using (var alac = new NativeAlacDecoder(16, 2, 352))
        {
            byte[] dummyPcm = new byte[352 * 4];
            int outBytes = alac.Decode([], dummyPcm);
            if (outBytes != 0) throw new Exception("Unexpected decode on empty buffer");
        }

        var alacLogs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using (var alacAudio = new MirrorAudio(IPAddress.Loopback, IPAddress.Loopback, key, iv, 2, 0, alacLogs.Enqueue))
        {
            if (alacAudio.Codec != 2) throw new Exception("ALAC audio codec != 2");
            if (!alacLogs.Any(s => s.Contains("ALAC (lossless)"))) throw new Exception("ALAC log missing");
        }

        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var audio = new MirrorAudio(IPAddress.Loopback,IPAddress.Loopback,key,iv,4,0,logs.Enqueue);
        await Task.Delay(400); // Allow the separately launched playback device to open its socket.
        using var sender = new UdpClient();
        byte[] adts = File.ReadAllBytes(Path.Combine(path,"tone.aac"));
        int offset=0; ushort sequence=0;
        while (offset+7 <= adts.Length)
        {
            int size=((adts[offset+3]&3)<<11)|(adts[offset+4]<<3)|(adts[offset+5]>>5);
            if(size<7 || offset+size>adts.Length) throw new Exception("Invalid test AAC.");
            var packet=Packet(adts.AsSpan(offset+7,size-7).ToArray(),key,iv,sequence,(uint)sequence*1024);
            await sender.SendAsync(packet,new IPEndPoint(IPAddress.Loopback,audio.DataPort));
            sequence++; offset+=size;
            if (sequence == 5)
            {
                bool midVolOk = false;
                for (int r = 0; r < 5 && !midVolOk; r++)
                {
                    midVolOk = WasapiVolume.SetProcessVolume(audio.PlayerId, 0.75f);
                    if (!midVolOk) await Task.Delay(50);
                }
                if (!midVolOk) throw new Exception($"WASAPI SetProcessVolume failed mid-stream for PID {audio.PlayerId}");
            }
            await Task.Delay(23);
        }
        await Task.Delay(250);
        bool volOk = WasapiVolume.SetProcessVolume(audio.PlayerId, 0.75f);
        if (!volOk) throw new Exception($"WASAPI SetProcessVolume failed end-of-stream for PID {audio.PlayerId}");
        if (!logs.Any(s=>s.StartsWith("First audio packet")) || logs.Any(s=>s.Contains("Error",StringComparison.OrdinalIgnoreCase) || s.Contains("Invalid",StringComparison.OrdinalIgnoreCase)))
            throw new Exception(string.Join("\n",logs));
        Console.WriteLine("PASS AES-CBC audio tails, RTP/AU layout, encrypted UDP AAC-LC to FFplay (audible output and ELD need device validation)");
    }
    private static byte[] Packet(byte[] plain,byte[] key,byte[] iv,ushort sequence,uint stamp)
    {
        byte[] result=new byte[12+plain.Length]; result[0]=0x80; result[1]=96;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2),sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4),stamp);
        int count=plain.Length&~15;
        using var aes=Aes.Create(); aes.Key=key;
        if(count>0) aes.EncryptCbc(plain.AsSpan(0,count),iv,result.AsSpan(12,count),PaddingMode.None);
        plain.AsSpan(count).CopyTo(result.AsSpan(12+count));
        return result;
    }
}
