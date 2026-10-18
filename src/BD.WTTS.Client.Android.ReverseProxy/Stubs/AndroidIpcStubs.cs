// Android 平台 stub：提供 IPCPlatformService 的本地直连实现。
// 上游接口定义来自 BD.WTTS.Client.IPC 拼装源码（真实接口），
// 这里把所有 IPC 远程调用改为本机直接执行（Android 单进程）。

using System.Text.RegularExpressions;

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;
// stub 类放在独立子命名空间避免与上游冲突

/// <summary>
/// Android 平台服务：IPCPlatformService 的本机直连实现。
/// 文件操作直接执行；证书信任由 App 层经 KeyChain 引导，此处保守返回未安装。
/// </summary>
public sealed class AndroidPlatformService : IPCPlatformService
{
    public static readonly AndroidPlatformService Instance = new();

    // ===== 证书 =====

    public bool IsCertificateInstalled(byte[] certificate2) => false;

    public bool? TrustRootCertificateAsync(string filePath) => false;

    public void RemoveCertificate(byte[] certificate2)
    {
    }

    // ===== 文件 IO（本机直连，签名与上游接口一致） =====

    public void CopyFile(string source, string dest, bool overwrite = true) =>
        File.Copy(source, dest, overwrite);

    public bool CopyFilesRecursive(string? inputFolder, string outputFolder, bool overwrite = true)
    {
        try
        {
            if (string.IsNullOrEmpty(inputFolder) || !Directory.Exists(inputFolder)) return false;
            Directory.CreateDirectory(outputFolder);
            foreach (var file in Directory.GetFiles(inputFolder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(inputFolder, file);
                var dest = Path.Combine(outputFolder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite);
            }
            return true;
        }
        catch { return false; }
    }

    public string ReadAllText(string path, int? encoding = null) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path, int? encoding, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public bool FileTryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            return true;
        }
        catch { return false; }
    }

    public bool WriteAllText(string path, string? contents, int? encoding = null)
    {
        File.WriteAllText(path, contents ?? string.Empty);
        return true;
    }

    public async Task<bool> WriteAllTextAsync(string path, string? contents, int? encoding = null, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(path, contents ?? string.Empty, cancellationToken);
        return true;
    }

    public bool WriteAllLines(string path, IEnumerable<string> contents, int? encoding = null)
    {
        File.WriteAllLines(path, contents);
        return true;
    }

    public async Task<bool> WriteAllLinesAsync(string path, IEnumerable<string> contents, int? encoding = null, CancellationToken cancellationToken = default)
    {
        await File.WriteAllLinesAsync(path, contents, cancellationToken);
        return true;
    }

    public bool WriteAllBytes(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        return true;
    }

    public async Task<bool> WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return true;
    }

    public string RegexSearchFile(string filePath, string pattern)
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(filePath), pattern);
            return m.Success ? m.Value : string.Empty;
        }
        catch { return string.Empty; }
    }

    public string RegexSearchFolder(string dirPath, string pattern, string wildcard = "")
    {
        try
        {
            foreach (var file in Directory.GetFiles(dirPath, string.IsNullOrEmpty(wildcard) ? "*" : wildcard, SearchOption.AllDirectories))
            {
                var r = RegexSearchFile(file, pattern);
                if (!string.IsNullOrEmpty(r)) return r;
            }
        }
        catch { }
        return string.Empty;
    }
}

/// <summary>
/// IPCSubProcessService 的 Android 单进程 stub：
/// GetService&lt;T&gt;() 无法返回真实 IPC 代理，返回 null。
/// </summary>
public sealed class AndroidIPCSubProcessService : IPCSubProcessService
{
    public static readonly AndroidIPCSubProcessService Instance = new();

    Task IPCSubProcessService.RunAsync(string moduleName, TaskCompletionSource tcs, string pipeName,
        Action<dotnetCampus.Ipc.Pipes.IpcProvider>? configureIpcProvider)
    {
        tcs.TrySetResult();
        return Task.CompletedTask;
    }

    T? IPCSubProcessService.GetService<T>() where T : class
    {
        if (typeof(T) == typeof(IPCPlatformService))
            return (T)(object)AndroidPlatformService.Instance;
        if (typeof(T) == typeof(IPCToastService))
            return (T)(object)AndroidToastService.Instance;
        return null;
    }

    void IDisposable.Dispose()
    {
    }
}

/// <summary>
/// Android 侧的公开工厂：上游实现类是 internal，App 工程无法直接 new。
/// </summary>
public static class AndroidServiceFactory
{
    public static ICertificateManager CreateCertificateManager() => new CertificateManagerImpl(
        AndroidIPCSubProcessService.Instance);

    public static YarpReverseProxyServiceImpl CreateReverseProxyService(
        DnsAnalysisServiceImpl dnsAnalysisServiceImpl,
        DnsDohAnalysisService dnsDohAnalysisService,
        ICertificateManager certificateManager) => new YarpReverseProxyServiceImpl(
        AndroidIPCSubProcessService.Instance,
        dnsAnalysisServiceImpl,
        dnsDohAnalysisService,
        certificateManager);
}

/// <summary>
/// Android 的 Toast stub：记录日志（UI Toast 由 App 层自行实现）。
/// </summary>
public sealed class AndroidToastService : IPCToastService
{
    public static readonly AndroidToastService Instance = new();

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, int? duration = null)
        => NLog.LogManager.GetLogger("AndroidToast").Info("Toast: {Icon} {Text}", icon, text);

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, ToastLength duration)
        => NLog.LogManager.GetLogger("AndroidToast").Info("Toast: {Icon} {Text}", icon, text);

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, int? duration = null, params string?[] args)
        => NLog.LogManager.GetLogger("AndroidToast").Info("Toast: {Icon} {Text} {Args}", icon, text, args);

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, ToastLength duration, params string?[] args)
        => NLog.LogManager.GetLogger("AndroidToast").Info("Toast: {Icon} {Text} {Args}", icon, text, args);

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, params string?[] args)
        => NLog.LogManager.GetLogger("AndroidToast").Info("Toast: {Icon} {Text} {Args}", icon, text, args);
}
