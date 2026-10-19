using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using AToast = Android.Widget.Toast;

namespace WattToolkit;

[Activity(Label = "Watt Toolkit", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTask)]
public class MainActivity : Activity
{
    Button? _btnToggle;
    TextView? _status;
    Button? _btnCert;
    bool _running;

    const int VpnRequestCode = 0x01;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 极简布局：状态文本 + 开关按钮 + 证书安装按钮（后续再换正式 UI）
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 96, 48, 48);

        _status = new TextView(this) { TextSize = 18 };
        _status.SetPadding(0, 0, 0, 32);
        layout.AddView(_status);

        _btnToggle = new Button(this) { Text = "开启加速" };
        _btnToggle.Click += OnToggleClick;
        layout.AddView(_btnToggle);

        _btnCert = new Button(this) { Text = "安装根证书" };
        _btnCert.Click += OnInstallCertClick;
        layout.AddView(_btnCert);

        SetContentView(layout);
        UpdateUi();
    }

    void OnToggleClick(object? sender, System.EventArgs e)
    {
        if (!_running)
        {
            var intent = WattVpnService.Prepare(this);
            if (intent != null)
            {
                StartActivityForResult(intent, VpnRequestCode);
                return;
            }
            StartVpn();
        }
        else
        {
            StopService(new Intent(this, typeof(WattVpnService)));            _running = false;
            UpdateUi();
        }
    }

    void OnInstallCertClick(object? sender, System.EventArgs e)
    {
        // 从 CertificateManager 导出 CA 证书并调起系统安装
        try
        {
            var certBytes = ReverseProxyHost.GetRootCertificateDer();
            if (certBytes == null)
            {
                AToast.MakeText(this, "证书尚未生成，先开启一次加速", AToastLength.Long)?.Show();
                return;
            }
            var install = Android.Security.KeyChain.CreateInstallIntent();
            install.PutExtra(Android.Security.KeyChain.ExtraCertificate, certBytes);
            StartActivity(install);
        }
        catch (System.Exception ex)
        {
            AToast.MakeText(this, ex.Message, AToastLength.Long)?.Show();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == VpnRequestCode && resultCode == Result.Ok)
        {
            StartVpn();
        }
    }

    void StartVpn()
    {
        StartForegroundService(new Intent(this, typeof(WattVpnService)));
        _running = true;
        UpdateUi();
    }

    void UpdateUi()
    {
        _status?.Text = _running ? "加速运行中" : "未运行";
        _btnToggle?.Text = _running ? "停止加速" : "开启加速";
    }
}
