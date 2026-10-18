using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using Service = Android.App.Service;

namespace WattToolkit;

[Service(Name = "watttoolkit.WattVpnService", Exported = false,
    Permission = "android.permission.BIND_VPN_SERVICE",
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeSpecialUse)]
public class WattVpnService : Android.Net.VpnService
{
    const string Tag = "WattVpn";
    const int NotifId = 1001;
    const string ChannelId = "watt_vpn";

    ParcelFileDescriptor? _tunInterface;

    public static Intent? Prepare(Context context) => Android.Net.VpnService.Prepare(context);

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // 前台服务通知
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Watt Toolkit")
            .SetContentText("网络加速运行中")
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .Build();
        StartForeground(NotifId, notification);

        // 1) 启动进程内反代（Kestrel HTTP 代理 @127.0.0.1:26501）
        _ = ReverseProxyHost.StartAsync(global::BD.WTTS.Enums.ProxyMode.VPN);

        // 2) 建立 TUN 并用 tun2socks 把流量转发到本地代理
        try
        {
            StartTun();
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, ex.ToString());
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        return StartCommandResult.Sticky;
    }

    void StartTun()
    {
        var builder = new Builder(this)
            .SetSession("Watt Toolkit Accelerator")
            .SetMtu(1500)
            .AddAddress("26.26.26.1", 24)
            .AddDnsServer("26.26.26.2");

        // 排除自身 App，避免回环
        builder.AddDisallowedApplication(PackageName!);

        // 劫持全部 IPv4 默认路由（分段 1/1 + 128/1 覆盖 0.0.0.0/0）
        builder.AddRoute("0.0.0.0", 1);
        builder.AddRoute("128.0.0.0", 1);

        _tunInterface = builder.Establish()
            ?? throw new System.Exception("Establish() 返回 null：VPN 权限被拒绝");

        global::Android.Util.Log.Info(Tag, "TUN established");
        StartTun2Socks(_tunInterface);
    }

    void StartTun2Socks(ParcelFileDescriptor tun)
    {
        // tun2socks 独立进程：tun fd → 127.0.0.1:26501 (HTTP 代理)
        var fd = tun.DetachFd();
        try
        {
            Tun2SocksNative.Start(fd, "127.0.0.1", 26501, "http");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error(Tag, $"tun2socks start failed: {ex}");
        }
    }

    public override void OnDestroy()
    {
        try
        {
            Tun2SocksNative.Stop();
            ReverseProxyHost.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
        _tunInterface?.Close();
        base.OnDestroy();
    }

    public override void OnRevoke()
    {
        StopSelf();
    }

    void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, "加速服务", NotificationImportance.Low);
            var nm = GetSystemService(NotificationService) as NotificationManager;
            nm?.CreateNotificationChannel(channel);
        }
    }
}
