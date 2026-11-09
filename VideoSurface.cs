using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NotAirplay;

// One native child window hosts the swap chain. WPF never handles GPU-path pixels.
public sealed class VideoSurface : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        nint window = CreateWindowEx(0, "static", "", 0x50000000 | 0x04000000 | 0x02000000 | 0x08000000,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, window);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string name, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
