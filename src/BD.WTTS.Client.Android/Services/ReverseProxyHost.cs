using AToast = Android.Widget.Toast;

namespace WattToolkit;

/// <summary>
/// 在 Android 上运行 WattProxyServer（轻量 HTTP CONNECT 代理 + DoH 分流）。
/// 架构说明见 docs/android-port-design.md：Android 无 ASP.NET Core runtime pack，
/// 无法运行上游 Kestrel 反代，改为自写 TcpListener 代理复用上游加速域名数据与分流思想。
/// </summary>
public static class ReverseProxyHost
{
    const string Tag = "ReverseProxyHost";

    /// <summary>
    /// 启动加速代理（VPN 模式，监听 0.0.0.0:26501 供 tun2socks 转发）
    /// </summary>
    public static Task StartAsync(global::BD.WTTS.Enums.ProxyMode mode)
    {
        try
        {
            // TODO: 加速项从上游配置源加载；首版内置 Steam/GitHub 常用域名
            global::BD.WTTS.Services.WattProxyServer.Start(26501, GetAccelerateProjects());
            global::Android.Util.Log.Info(Tag, "WattProxy started");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, ex.ToString());
        }
        return Task.CompletedTask;
    }

    public static Task StopAsync()
    {
        try
        {
            global::BD.WTTS.Services.WattProxyServer.Stop();
            global::Android.Util.Log.Info(Tag, "WattProxy stopped");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"StopProxy: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 导出根证书 DER 供 KeyChain 安装（当前架构为直连分流，不做 MITM，返回 null 跳过）
    /// </summary>
    public static byte[]? GetRootCertificateDer() => null;

    /// <summary>
    /// 内置加速域名（首版）：Steam 社区/商店 + GitHub。
    /// 与上游 AccelerateProjectDTO.MatchDomainNames 格式一致（空格分隔，支持 *.example.com）。
    /// </summary>
    static IReadOnlyList<global::BD.WTTS.Models.AccelerateProjectDTO> GetAccelerateProjects()
    {
        const string steamDomains = "steamcommunity.com steampowered.com steamstatic.com steamusercontent.com steamcontent.com steamserver.net";
        const string githubDomains = "github.com githubusercontent.com githubassets.com github.io";
        const string pixivDomains = "pixiv.net pximg.net";

        return new List<global::BD.WTTS.Models.AccelerateProjectDTO>
        {
            new()
            {
                Name = "Steam 社区",
                MatchDomainNames = steamDomains,
                Order = 0,
            },
            new()
            {
                Name = "GitHub",
                MatchDomainNames = githubDomains,
                Order = 1,
            },
            new()
            {
                Name = "Pixiv",
                MatchDomainNames = pixivDomains,
                Order = 2,
            },
        };
    }
}
