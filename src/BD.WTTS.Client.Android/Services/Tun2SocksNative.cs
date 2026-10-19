using System.Diagnostics;

namespace WattToolkit;

/// <summary>
/// tun2socks 运行器（xjasonlyu/tun2socks v2.7.0，Go 静态二进制）。
/// 打包方式：将 linux-arm64/amd64 二进制重命名为 libtun2socks.so 放入
/// jniLibs/arm64-v8a 与 jniLibs/x86_64，安装后位于 nativeLibraryDir，可执行。
/// </summary>
public static class Tun2SocksNative
{
    const string Tag = "Tun2Socks";
    static Process? _process;

    public static void Start(int tunFd, string proxyHost, int proxyPort, string proxyType)
    {
        var ctx = global::Android.App.Application.Context;
        var libDir = ctx.ApplicationInfo!.NativeLibraryDir;
        var bin = Path.Combine(libDir!, "libtun2socks.so");
        if (!File.Exists(bin))
            throw new FileNotFoundException("libtun2socks.so not in nativeLibraryDir", bin);

        var proxyUrl = proxyType switch
        {
            "http" => $"http://{proxyHost}:{proxyPort}",
            "socks5" => $"socks5://{proxyHost}:{proxyPort}",
            _ => $"socks5://{proxyHost}:{proxyPort}",
        };

        var psi = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-device");
        psi.ArgumentList.Add($"fd:{tunFd}");
        psi.ArgumentList.Add("-proxy");
        psi.ArgumentList.Add(proxyUrl);
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("info");

        _process = Process.Start(psi) ?? throw new Exception("Failed to start tun2socks");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                global::Android.Util.Log.Info(Tag, e.Data);
        };
        _process.BeginErrorReadLine();
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                global::Android.Util.Log.Info(Tag, e.Data);
        };
        _process.BeginOutputReadLine();
    }

    public static bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// 停止 tun2socks 并确认进程完全退出（VPN 停止的关键：
    /// tun2socks 持有 TUN fd，进程不退出 Android 就认为 VPN 仍在运行）。
    /// </summary>
    public static void Stop()
    {
        var p = _process;
        if (p == null) return;

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, $"Kill error: {ex}");
        }

        try
        {
            // 关键：有异步输出读取线程时，必须先取消读取再等退出，
            // 否则 WaitForExit(timeout) 只等主进程信号量，stdout 管道未排空会永远等不到退出事件
            try { p.CancelOutputRead(); } catch { }
            try { p.CancelErrorRead(); } catch { }
            if (!p.WaitForExit(5000))
            {
                global::Android.Util.Log.Warn(Tag, "tun2socks not exited in 5s, SIGKILL");
                try { p.Kill(); } catch { }
                p.WaitForExit(3000);
            }
            global::Android.Util.Log.Info(Tag, $"tun2socks exited={p.HasExited}");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, $"Wait error: {ex}");
        }
        finally
        {
            p.Dispose();
            _process = null;
        }
    }
}
