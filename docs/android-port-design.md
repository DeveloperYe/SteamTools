# Watt Toolkit Android 移植 — 技术设计（只做网络加速）

## 上游架构（桌面端，develop @ d042131）

```
UI 进程 (Avalonia, ProxyService)
   │  IPC (dotnetCampus.Ipc, 子进程)
   ▼
ReverseProxy 子进程 (BD.WTTS.Client.Plugins.Accelerator.ReverseProxy, 独立 Exe, net10.0)
   ├─ YarpReverseProxyServiceImpl: WebApplication(Kestrel) 进程内跑
   │    ├─ ProxyMode.Hosts:     监听 443 HTTPS 反代 + 改 hosts 文件（域名→127.0.0.1）
   │    ├─ ProxyMode.System/PAC: 监听 HTTP 代理端口(26501) + 设系统代理
   │    ├─ ProxyMode.DNSIntercept: WinDivert 透明拦截 (仅 Windows)
   │    └─ ProxyMode.VPN:       监听 HTTP 代理（同 System），预期由外部 VPN 提供流量  ← 关键！
   ├─ 证书: CertificateManagerImpl 自签 CA + 动态签发域名证书
   ├─ DNS: DnsAnalysisServiceImpl / DoH
   └─ 出站: ReverseProxyHttpClientHandler（支持二级代理 Socks5/HTTP）
```

**关键发现**：
1. `ProxyMode.VPN` 模式下 Kestrel 走 `ListenHttpProxy()`（HTTP/CONNECT 正向代理，26501 端口），上游代码已经预留了 Android VPN 的钩子（`ProxyService.Operate.cs` L57：`OperatingSystem.IsAndroid() && ProxyModeValue == VPN → socks5ProxyEnable=true`，注释写明 "Android VPN 模式使用 tun2socks"）。
2. ReverseProxy 是独立 net10.0 控制台程序，不依赖 Windows API（WinDivert 仅 `#if WINDOWS`），**可以直接改为类库在 Android 进程内运行**。
3. 它依赖 `ref/` 下的多个 git 子模块（Common、dotnetCampus.Ipc、WTTS.MicroServices.ClientSDK 等），均为纯 C#，可跨平台。

## Android 目标架构

```
Xamarin/MAUI? 否 → net9.0-android (dotnet android workload) + 原生 Kotlin/Java VpnService 壳
┌──────────────────────────────────────────────┐
│ Android App (单进程)                          │
│  ├─ MainActivity (最小 UI: 开关/域名选择/证书) │
│  ├─ WattVpnService : Android.Net.VpnService   │
│  │    ├─ Builder.AddAddress(26.26.26.1,24)    │
│  │    │        .AddDnsServer(26.26.26.2)      │
│  │    │        .AddRoute("0.0.0.0", 1) ...    │  ← 默认路由抢占，App 包名排除自身
│  │    └─ TUN fd ──► tun2socks (native)        │
│  │           SOCKS5/HTTP → 127.0.0.1:26501    │
│  ├─ ReverseProxyHost (进程内运行 Yarp 反代)    │
│  │    └─ Kestrel ListenHttpProxy @26501       │
│  │       + HttpsReverseProxy @443(可选 Hosts) │
│  └─ 证书安装: KeyChain.createIntent() 用户确认 │
└──────────────────────────────────────────────┘
```

### 分流策略
- **DNS**：VpnService 的 DNS 指向 26.26.26.2，由 tproxy/dns 层接管，加速域名解析到 127.0.0.1 或经反代；非加速域名走 DoH/普通 DNS 直连解析。
- **TCP**：tun2socks 全部转给本地 HTTP 代理，代理内部按 SNI/Host 判断是否走加速上游（`HttpProxyMiddleware` 已实现 CONNECT 解析 + `TunnelMiddleware` 处理隧道），非加速域名直接原样转发（等价于无加速），加速域名走 Yarp 的 re-route/证书伪造逻辑。

### 端口约定（避免与系统冲突）
| 端口 | 用途 |
|---|---|
| 26501 | HTTP/CONNECT 正向代理（tun2socks 目标） |
| 26502 | (备用) SOCKS5 |
| 26503 | DNS 劫持监听 (UDP 53 → DnsAnalysis) |

## 目录规划（fork 仓库内新增）
```
src/BD.WTTS.Client.Android/                 # net9.0-android 主工程
  ├─ BD.WTTS.Client.Android.csproj
  ├─ AndroidManifest.xml
  ├─ Activities/MainActivity.cs
  ├─ Services/WattVpnService.cs
  ├─ Services/ReverseProxyHost.cs           # 进程内跑 YarpReverseProxyServiceImpl 的适配层
  ├─ Services/Tun2Socks.cs                  # P/Invoke 封装 (libtun2socks.so)
  └─ jni/ (或 native/下面放预编译 so)
src/BD.WTTS.Client.Plugins.Accelerator.ReverseProxy/
  └─ (小改：TFM 多目标 + 平台宏，加 net-android 目标；关闭 WinDivert/Console 特定代码)
.github/workflows/android-build.yml
```

## 构建链（GitHub Actions）
- setup-dotnet 9.x + `dotnet workload install android`
- 还原子模块（actions/checkout with submodules: recursive）
- `dotnet publish src/BD.WTTS.Client.Android -f net9.0-android -c Release`
- 产物：unsigned APK → apksigner debug 签名（或 secrets 里 release keystore）

## 风险与对策
| 风险 | 对策 |
|---|---|
| tun2socks native 库获取 | 用 heelhook/tun2socks 或 xjasonlyu/tun2socks 预编译产物，或用 evilbinary/tun2socks 的 Android 预编译版；实在不行 C# 里写极简 TCP/IP 栈（工作量大，最后手段） |
| tun2socks 输出协议是 SOCKS5 而反代监听的是 HTTP CONNECT | tun2socks -proxy 协议可选 socks5/http，直接选 http 对接 26501 |
| Android 上 Kestrel 监听 443 无 root 不允许 | VPN 模式不需要监听 443，只用 26501 正向代理；域名分流在代理内部做 |
| 用户证书信任：Android 7+ 应用默认不信任用户 CA | 仅加速域名走「伪证书」路径；非加速流量不劫持 TLS。加速生效范围=用户在 UI 勾选的域名 |
| IPC 子进程模型在 Android 不适用 | 单进程内直接 new YarpReverseProxyServiceImpl + 手工 DI，绕过 dotnetCampus.Ipc |
| Xamarin 遗留代码不兼容 net9.0-android | 不复用 ST.Client.Android 遗留工程，全新 net9.0-android 工程只引用需要的库 |
