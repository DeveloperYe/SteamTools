using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using AToast = Android.Widget.Toast;
using ScrollView = Android.Widget.ScrollView;

namespace WattToolkit;

[Activity(Label = "Watt Toolkit", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTask)]
public class MainActivity : Activity
{
    Button? _btnToggle;
    TextView? _status;
    Button? _btnTest;
    TextView? _testOutput;
    bool _running;
    bool _testing;

    const int VpnRequestCode = 0x01;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new ScrollView(this);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 96, 48, 48);

        _status = new TextView(this) { TextSize = 18 };
        _status.SetPadding(0, 0, 0, 32);
        layout.AddView(_status);

        _btnToggle = new Button(this) { Text = "开启加速" };
        _btnToggle.Click += OnToggleClick;
        layout.AddView(_btnToggle);

        _btnTest = new Button(this) { Text = "连通性测试" };
        _btnTest.Click += OnTestClick;
        layout.AddView(_btnTest);

        _testOutput = new TextView(this)
        {
            TextSize = 14,
            Text = "（点击「连通性测试」检查加速是否生效）",
        };
        _testOutput.SetPadding(0, 24, 0, 0);
        _testOutput.SetTextIsSelectable(true);
        layout.AddView(_testOutput);

        root.AddView(layout);
        SetContentView(root);
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
            var stopIntent = new Intent(this, typeof(WattVpnService));
            stopIntent.PutExtra("stop", true);
            StartService(stopIntent);   // 触发 OnStartCommand 停止分支 → StopSelf
            StopService(new Intent(this, typeof(WattVpnService)));
            _running = false;
            UpdateUi();
        }
    }

    async void OnTestClick(object? sender, System.EventArgs e)
    {
        if (_testing) return;
        _testing = true;
        _btnTest!.Enabled = false;
        _testOutput!.Text = "测试中…";

        void AppendLine(string line)
        {
            RunOnUiThread(() =>
            {
                _testOutput!.Text = _testOutput!.Text + line + "\n";
                global::Android.Util.Log.Info("ConnTest", line);
            });
        }

        AppendLine(_running
            ? "【VPN 运行中】以下端到端测试走真实加速路径"
            : "【VPN 未开启】端到端测试走的是直连（对比基准）");

        var tcs = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await ConnectivityTester.RunAllAsync(r =>
                {
                    var mark = r.Ok ? "✓" : "✗";
                    AppendLine($"{mark} {r.Name}  {r.ElapsedMs}ms  {r.Detail}");
                });
                tcs.TrySetResult();
            }
            catch (System.Exception ex)
            {
                AppendLine($"测试异常: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        await tcs.Task;

        AppendLine("—— 测试完成 ——");
        _btnTest.Enabled = true;
        _testing = false;
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
        if (_status != null) _status.Text = _running ? "加速运行中" : "未运行";
        if (_btnToggle != null) _btnToggle.Text = _running ? "停止加速" : "开启加速";
    }
}
