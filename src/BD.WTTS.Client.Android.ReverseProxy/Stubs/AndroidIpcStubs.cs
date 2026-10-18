// Android 平台 stub：提供 IPCPlatformService 的本地实现与 IPCSubProcessService 的空实现。
// 上游类型定义来自 BD.WTTS.Client.IPC 拼装源码（真实接口），这里只补 Android 行为。

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

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
