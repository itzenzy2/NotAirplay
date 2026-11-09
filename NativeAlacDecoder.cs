using System.Runtime.InteropServices;

namespace NotAirplay;

internal sealed class NativeAlacDecoder : IDisposable
{
    private nint handle;

    public NativeAlacDecoder(int sampleSize = 16, int numChannels = 2, int frameSize = 352)
    {
        handle = na_alac_create(sampleSize, numChannels, frameSize);
        if (handle == 0)
            throw new InvalidOperationException("Failed to allocate native ALAC decoder.");
    }

    public int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (handle == 0) throw new ObjectDisposedException(nameof(NativeAlacDecoder));
        unsafe
        {
            fixed (byte* inPtr = input)
            fixed (byte* outPtr = output)
            {
                return na_alac_decode(handle, inPtr, input.Length, outPtr, output.Length);
            }
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref handle, 0);
        if (h != 0) na_alac_destroy(h);
    }

    [DllImport("notairplay-video.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint na_alac_create(int sampleSize, int numChannels, int frameSize);

    [DllImport("notairplay-video.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int na_alac_decode(nint handle, byte* inBuffer, int inBytes, byte* outBuffer, int maxOutBytes);

    [DllImport("notairplay-video.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void na_alac_destroy(nint handle);
}

