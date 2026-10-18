using System.Reflection;

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
    public static async Task StartAsync(ProxyMode mode)
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
                            l.AddSimpleConsole(o => { o.SingleLine = true; });
                        });
                        services.AddSingleton<IPCSubProcessService>(AndroidIPCSubProcessService.Instance);
                        services.AddSingleton<IPCPlatformService>(AndroidPlatformService.Instance);
                        services.AddDnsAnalysisService();
                        services.AddReverseProxyService();
                        services.AddSingleton<ICertificateManager, CertificateManagerImpl>();
                    });

                    var impl = Ioc.Get<YarpReverseProxyServiceImpl>();

                    var settings = new ReverseProxySettings(
                        GetAccelerateProjects(),
                        scripts: null,
                        isEnableScript: false,
                        isOnlyWorkSteamBrowser: false,
                        proxyPort: 26501,
                        proxyIp: "0.0.0.0",
                        proxyMode: mode,
                        isProxyGOG: false,
                        onlyEnableProxyScript: false,
                        enableHttpProxyToHttps: false,
                        socks5ProxyEnable: false,
                        socks5ProxyPortId: 26502,
                        twoLevelAgentEnable: false,
                        twoLevelAgentProxyType: ExternalProxyType.Socks5,
                        twoLevelAgentIp: null,
                        twoLevelAgentPortId: 0,
                        twoLevelAgentUserName: null,
                        twoLevelAgentPassword: null,
                        proxyDNS: null,
                        isSupportIpv6: false,
                        useDoh: true,
                        customDohAddres: null,
                        proxyToken: null);

                    var result = await impl.StartProxyAsync(Serializable.SMP2(settings));
                    global::Android.Util.Log.Info(Tag, $"StartProxy: {result.Code}");
                }
                catch (Exception ex)
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
            var impl = Ioc.Get<YarpReverseProxyServiceImpl>();
            await impl.StopProxyAsync();
        }
        catch (Exception ex)
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
        catch (Exception ex)
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
