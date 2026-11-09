using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NotAirplay;

internal sealed class DiscoveryRegistration : IAsyncDisposable
{
    private const uint RequestPending = 9506;
    private static readonly CompletionCallback Callback = Complete;
    private nint instance;

    private DiscoveryRegistration(nint instance) => this.instance = instance;

    public static async Task<DiscoveryRegistration> RegisterAsync(DiscoveryService service, ushort port)
    {
        var properties = service.Properties;
        var instance = DnsServiceConstructInstance(
            service.FullName, $"{Environment.MachineName}.local", 0, 0,
            port, 0, 0, (uint)properties.Count, properties.Keys.ToArray(), properties.Values.ToArray());
        if (instance == 0)
            throw new InvalidOperationException("Windows could not allocate an mDNS service record.");

        try
        {
            await ChangeRegistrationAsync(instance, register: true).ConfigureAwait(false);
            return new DiscoveryRegistration(instance);
        }
        catch
        {
            DnsServiceFreeInstance(instance);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var registered = Interlocked.Exchange(ref instance, 0);
        if (registered == 0)
            return;

        try
        {
            await ChangeRegistrationAsync(registered, register: false).ConfigureAwait(false);
        }
        finally
        {
            DnsServiceFreeInstance(registered);
        }
    }

    private static async Task ChangeRegistrationAsync(nint service, bool register)
    {
        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = GCHandle.Alloc(completion);
        var request = new RegisterRequest
        {
            Version = 1,
            Service = service,
            Callback = Marshal.GetFunctionPointerForDelegate(Callback),
            Context = GCHandle.ToIntPtr(context),
        };
        var requestPointer = Marshal.AllocHGlobal(Marshal.SizeOf<RegisterRequest>());
        try
        {
            Marshal.StructureToPtr(request, requestPointer, false);
            uint status = register
                ? DnsServiceRegister(requestPointer, 0)
                : DnsServiceDeRegister(requestPointer, 0);
            if (status == RequestPending)
                status = await completion.Task.ConfigureAwait(false);
            if (status != 0)
                throw new Win32Exception((int)status);
        }
        finally
        {
            Marshal.FreeHGlobal(requestPointer);
            context.Free();
        }
    }

    private static void Complete(uint status, nint context, nint copy)
    {
        var completion = (TaskCompletionSource<uint>)GCHandle.FromIntPtr(context).Target!;
        if (copy != 0)
            DnsServiceFreeInstance(copy);
        completion.TrySetResult(status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CompletionCallback(uint status, nint context, nint copy);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint Service;
        public nint Callback;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint DnsServiceConstructInstance(string service, string host, nint ipv4, nint ipv6,
        ushort port, ushort priority, ushort weight, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 7)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 7)] string[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsServiceFreeInstance(nint service);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceRegister(nint request, nint cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceDeRegister(nint request, nint cancel);
}
