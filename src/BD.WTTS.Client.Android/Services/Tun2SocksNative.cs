using System.Runtime.InteropServices;

namespace BD.WTTS.Android;

/// <summary>
/// libtun2socks.so 的 P/Invoke 封装。
/// native 侧导出：
///   int tun2socks_start(int tun_fd, const char* proxy_host, int proxy_port, const char* proxy_type);
///   void tun2socks_stop(void);
/// </summary>
public static unsafe class Tun2SocksNative
{
    const string Lib = "tun2socks";

    [DllImport(Lib, EntryPoint = "tun2socks_start")]
    static extern int StartNative(int tunFd, byte* proxyHost, int proxyPort, byte* proxyType);

    [DllImport(Lib, EntryPoint = "tun2socks_stop")]
    static extern void StopNative();

    public static void Start(int tunFd, string proxyHost, int proxyPort, string proxyType)
    {
        var h = AllocUtf8(proxyHost);
        var t = AllocUtf8(proxyType);
        try
        {
            var code = StartNative(tunFd, h, proxyPort, t);
            if (code != 0)
                throw new Exception($"tun2socks_start failed: {code}");
        }
        finally
        {
            Marshal.FreeHGlobal((nint)h);
            Marshal.FreeHGlobal((nint)t);
        }
    }

    public static void Stop()
    {
        try { StopNative(); } catch { /* 库缺失时忽略 */ }
    }

    static byte* AllocUtf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var p = (byte*)Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, (nint)p, bytes.Length);
        p[bytes.Length] = 0;
        return p;
    }
}
