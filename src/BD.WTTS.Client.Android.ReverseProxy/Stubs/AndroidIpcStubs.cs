// Android 平台 stub：替代 dotnetCampus.Ipc 的 IPC 服务。
// 上游 YarpReverseProxyServiceImpl / CertificateManagerImpl 构造函数依赖 IPCSubProcessService
// （主进程↔子进程模型），Android 单进程内不适用，改为本地直连 stub。

using dotnetCampus.Ipc;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

/// <summary>
/// IPCSubProcessService 的 Android 单进程 stub：
/// GetService&lt;T&gt;() 无法返回真实 IPC 代理，返回 null；
/// 调用方(YarpReverseProxyServiceImpl 等)需容忍 null 并走本地实现。
/// </summary>
public sealed class AndroidIPCSubProcessService : IPCSubProcessService
{
    public static readonly AndroidIPCSubProcessService Instance = new();

    Task IPCSubProcessService.RunAsync(string moduleName, TaskCompletionSource tcs, string pipeName,
        Action<IpcProvider>? configureIpcProvider)
    {
        tcs.TrySetResult();
        return Task.CompletedTask;
    }

    T? IPCSubProcessService.GetService<T>() where T : class => null;

    void IDisposable.Dispose()
    {
    }
}

/// <summary>
/// 平台服务 stub：证书信任由 App 层经 KeyChain 引导完成，这里保守返回未安装。
/// </summary>
public sealed class AndroidPlatformService : IPCPlatformService
{
    public static readonly AndroidPlatformService Instance = new();

    public bool IsCertificateInstalled(byte[] certificate2) => false;

    public bool? TrustRootCertificateAsync(string filePath) => false;

    public void RemoveCertificate(byte[] certificate2)
    {
    }
}
