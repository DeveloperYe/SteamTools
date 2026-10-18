using AActivity = Android.App.Activity;
using AToast = Android.Widget.Toast;
using global::BD.WTTS;
using global::BD.WTTS.Models;
using global::BD.WTTS.Services;
using global::BD.WTTS.Services.Implementation;
using Microsoft.Extensions.DependencyInjection;
using NLog;

namespace WattToolkit;

/// <summary>
/// 在 Android 单进程内直接运行上游 YarpReverseProxyServiceImpl 的适配层。
/// 通过 AndroidIPCSubProcessService stub 绕过 dotnetCampus.Ipc 子进程模型。
/// </summary>
public static class ReverseProxyHost
{
    const string Tag = "ReverseProxyHost";
    static Task? _startTask;
    static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 启动反代（VPN 模式 = Kestrel ListenHttpProxy @ 0.0.0.0:26501）
    /// </summary>
    public static async Task StartAsync(global::BD.WTTS.Enums.ProxyMode mode)
    {
        await _gate.WaitAsync();
        try
        {
            if (_startTask != null) return;

            _startTask = Task.Run(async () =>
            {
                try
                {
                    // 初始化 Ioc（上游代码大量使用 Ioc.Get<T>）
                    Ioc.ConfigureServices(services =>
                    {
                        services.AddLogging(l =>
                        {
                            
                        });
                        services.AddSingleton<IPCSubProcessService>(AndroidIPCSubProcessService.Instance);
                        services.AddSingleton<IPCPlatformService>(AndroidPlatformService.Instance);
                        services.AddDnsAnalysisService();
                        services.AddReverseProxyService();
                        services.AddSingleton<ICertificateManager, CertificateManagerImpl>();
                    });

                    IReverseProxyService impl = Ioc.Get<IReverseProxyService>();

                    var settings = new ReverseProxySettings(
                        GetAccelerateProjects(),
                        null,                       // scripts
                        false,                      // isEnableScript
                        false,                      // isOnlyWorkSteamBrowser
                        26501,                      // proxyPort
                        "0.0.0.0",                  // proxyIp
                        mode,                       // proxyMode
                        false,                      // isProxyGOG
                        false,                      // onlyEnableProxyScript
                        false,                      // enableHttpProxyToHttps
                        false,                      // socks5ProxyEnable
                        26502,                      // socks5ProxyPortId
                        false,                      // twoLevelAgentEnable
                        ExternalProxyType.Socks5,   // twoLevelAgentProxyType
                        null,                       // twoLevelAgentIp
                        0,                          // twoLevelAgentPortId
                        null,                       // twoLevelAgentUserName
                        null,                       // twoLevelAgentPassword
                        null,                       // proxyDNS
                        false,                      // isSupportIpv6
                        true,                       // useDoh
                        null,                       // customDohAddres
                        null);                      // proxyToken

                    var result = await impl.StartProxyAsync(Serializable.SMP2(settings));
                    global::Android.Util.Log.Info(Tag, $"StartProxy: {result.Code}");
                }
                catch (System.Exception ex)
                {
                    global::Android.Util.Log.Error(Tag, ex.ToString());
                }
            });
            await _startTask;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        try
        {
            IReverseProxyService impl = Ioc.Get<IReverseProxyService>();
            await impl.StopProxyAsync();
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"StopProxy: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 导出根证书 DER 供 KeyChain 安装
    /// </summary>
    public static byte[]? GetRootCertificateDer()
    {
        try
        {
            var cm = Ioc.Get<ICertificateManager>();
            var bytes = cm.RootCertificatePackable;
            if (bytes == null) return null;
            var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(bytes);
            return cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, ex.ToString());
            return null;
        }
    }

    static IReadOnlyList<AccelerateProjectDTO> GetAccelerateProjects()
    {
        // TODO: 从上游加速项配置数据源加载（远程 JSON / 本地缓存）
        // 首版硬编码 Steam 社区 + GitHub 最小集验证链路
        return new List<AccelerateProjectDTO>();
    }
}
