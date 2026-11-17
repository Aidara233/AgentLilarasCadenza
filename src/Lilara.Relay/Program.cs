using System.Diagnostics;
using System.Net.Sockets;
using Lilara.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Lilara.Relay —— Lilara 的中转节点（头）。
//
// 这一层干的只有三件事，一件都不多：
//   ① 把页面（Cadenza 的静态文件）发出去；
//   ② 把页面的 `/core/*`（HTTP + WS）转给核心；
//   ③ 核心没在跑时，**按需把它起起来**，退出时收掉自己起的那个。
//
// ⚠️ 核心的代码一行都不在这里（`Lilara.Relay.csproj` 不引用 `Lilara.Core`）：
// 「头是可替换件」那条设计（core 仓 docs/05 §2）靠的就是这条边界。要是哪天想换一个头，
// 换掉这个项目即可，核心照旧能被别的头（甚至没有头）驱动。
//
// ⚠️ 但它**仍然值得存在**（用户 2026-11-14）：页面直连核心时，凭据在浏览器里、
// 换设备/换地址/上隧道都要动核心的白名单；经这一层之后，凭据留在进程里，
// 页面只跟一个源说话，核心的 `LILARA_ALLOWED_ORIGIN` 也就用不上了。

const string RelayVersion = "1.1.0";

// 控制台显式设成 UTF-8：横幅是中文，而 Windows 的重定向/管道默认按机器代码页走——
// 日志被重定向到文件时（本机实测）会变成一串乱码，等于横幅白印（核心仓 CLI 同一条教训）。
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // 有些宿主（无控制台的 CI）不让改：不该因此起不来。
}

var parsed = RelayOptions.Parse(args, Environment.GetEnvironmentVariable);
if (parsed.Help)
{
    RelayOptions.PrintHelp();
    return 0;
}
if (parsed.Error is not null)
{
    Console.Error.WriteLine($"✗ {parsed.Error}");
    return 2;
}

var options = parsed.Options!;

// `--health`：只探一层就退——Docker HEALTHCHECK / compose / k8s 用的就是这条命令，
// 于是镜像里不用为了健康检查装 curl（基础镜像本来也没有）。
if (options.Health) return await HealthProbeAsync(options);

var (root, rootError) = UiRoot.Resolve(options.Root, AppContext.BaseDirectory);
if (root is null)
{
    Console.Error.WriteLine($"✗ {rootError}");
    return 1;
}

Console.WriteLine($"LILARA 中转节点 — Lilara.Relay {RelayVersion}");
Console.WriteLine($"  页面 {root}");

// ---- 核心：在就接入、不在就起 --------------------------------------------------
await using var supervisor = new CoreSupervisor(options);
await supervisor.EnsureAsync(CancellationToken.None);
if (!supervisor.Ready)
{
    // ⚠️ 核心没起来**不是**这个进程的死因：页面照样发得出去，它会显示「已断开·重连中」，
    // 而用户能把核心起来之后刷新。硬退出只会在「我先开页面、再开核心」这种用法里帮倒忙。
    Console.Error.WriteLine("  继续起头——页面能打开；核心起来之后刷新即可。");
}

string? Token() => supervisor.Token;

// ---- 起 Web 宿主 --------------------------------------------------------------
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // content root 必须在 Build 之前钉住：它默认是当前目录，而这里要的是可执行文件旁边。
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls($"http://{HostForUrl(options.Bind)}:{options.Port}");
// 一行一个请求的日志会盖掉核心转回来的那些行 ⇒ 只留警告与错误。
// ⚠️ **不能清成零个 provider**：反代里一处未处理异常会变成「500 + 空响应 + 控制台一个字都没有」，
// 那正是本机实测栽过的形状（处理器被提前释放）；留着这一条，同样的错会自己说出来。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// **响应压缩**：页面首屏那几份 JS 与 `/sessions`、`/history` 这些 JSON 快照都高度重复
// （同一批键名、大量中文），实测压掉七成上下。级别取 `Optimal`：单用户、每次启动只压几次，
// 那几毫秒 CPU 换掉的是手机上多等的半秒（这条结论来自核心仓头的性能批）。
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Optimal);

var app = builder.Build();

// 中间件顺序 = 执行顺序：压缩要套住后面所有响应；静态在前面，反代只在 `/core` 那一支兜住。
app.UseResponseCompression();
app.Use(UiRoot.Middleware(root));

// ⚠️ **`UseWebSockets()` 不能省**（本机实测）：没有它，`/core/ws` 那一路的
// `ctx.WebSockets.IsWebSocketRequest` 一律是 false，于是每条握手都被我们自己的「这不是握手」
// 那一支挡成 400——而客户端只会报「服务器回了 400，期待 101」，一句话也指不到这里。
app.UseWebSockets();

// ---- /config.json：页面启动时读它，决定「怎么连」------------------------------
//
// 两种形状，就是这么简单的一份契约（Cadenza 的 `js/core.js` 里逐字实现）：
//   · 代理（默认） { proxy: "/core", upstream, auth, wsProtocol }  → 页面只跟本节点说话
//   · 直连（--direct） { core: "http://…" }                        → 旧 UI / 排错对照面
// ⚠️ 直连那一档的值要跟着**请求的 Host** 走：写死 127.0.0.1 的话，手机打开页面会去连
// **手机自己**，而症状与「核心没起来」一模一样（核心仓头在 2026-11-05 栽过这一次）。
app.MapGet("/config.json", (HttpRequest request) =>
{
    if (!options.Direct)
    {
        return Results.Json(new
        {
            proxy = "/core",
            upstream = options.CoreText,
            auth = supervisor.Token is null ? "page" : "head",
            wsProtocol = "cadenza",
        });
    }

    var host = request.Host.Host;
    var core = options.Core.IsLoopback && !string.IsNullOrEmpty(host)
        ? $"{options.Core.Scheme}://{HostForUrl(host)}:{options.Core.Port}"
        : options.CoreText;
    return Results.Json(new { core });
});

// ---- /build.json：这份页面是哪一份 -------------------------------------------
//
// ⚠️ 这一条是为了终结「我改了、你说没效果」那类对话：页面的底栏（旧 UI）与本端点显示的都是
// **现在正被发出去的那份文件**的属性，和源码目录一比对，答案立刻出来（缓存 / 跑错构建 /
// 改错副本，四种成因里能一次排掉三种）。
app.MapGet("/build.json", () =>
{
    var (servedFrom, indexWritten, appJsBytes, appJsWritten) = UiRoot.Describe(root);
    return Results.Json(new
    {
        servedFrom,
        mode = options.Direct ? "direct" : "proxy",
        upstream = options.CoreText,
        indexPath = Path.Combine(servedFrom, "index.html"),
        indexWritten,
        appJsBytes,
        appJsWritten,
        relayVersion = RelayVersion,
        coreReady = supervisor.Ready,
        coreSpawnedByRelay = supervisor.Spawned,
    });
});

// ---- /core/*：反代（HTTP + WS 隧道）------------------------------------------
CoreProxy.Map(app, options, Token);

try
{
    await app.StartAsync();
}
catch (Exception ex) when (ex is SocketException or IOException)
{
    // ⚠️ 绑定失败要说人话（核心仓头 2026-10-06 的同一条纪律）：裸的栈把「端口被占」
    // 与「落在 Windows 保留段」混成一段天书，而这两者的处置完全不同。
    Console.Error.WriteLine($"✗ 头起不来（http://{HostForUrl(options.Bind)}:{options.Port}）：{ex.Message}");
    Console.Error.WriteLine("  端口被占**或**落在 Windows 保留段（`netsh int ipv4 show excludedportrange protocol=tcp`）。");
    Console.Error.WriteLine("  换个端口：--port <n>（或 LILARA_HEAD_PORT）。页面是按这个端口打开的，核心那边不用跟着改——");
    Console.Error.WriteLine("  经头这一层时，核心的 Origin 白名单根本用不上。");
    return 1;
}

PrintBanner(options, root, supervisor);

if (options.Open) OpenBrowser($"http://127.0.0.1:{options.Port}");

await app.WaitForShutdownAsync();
return 0;

static void PrintBanner(RelayOptions options, string root, CoreSupervisor supervisor)
{
    var open = options.Bind is "127.0.0.1" or "localhost" ? "仅本机" : "对局域网开放";
    Console.WriteLine($"  听 http://{HostForUrl(options.Bind)}:{options.Port}（{open}）");
    Console.WriteLine($"  核心 {options.CoreText}"
                      + (supervisor.Spawned ? "（**由本节点起的**，退出时一并收掉）"
                          : supervisor.Ready ? "（已在跑，只接入）" : "（没应答）"));
    Console.WriteLine(supervisor.Token is null
        ? "  凭据 页面自己带（/config.json 的 auth = page）"
        : "  凭据 本节点带（页面一条都不经手）");
    Console.WriteLine(options.Direct
        ? "  连接方式：直连（页面直接打核心——排错/旧 UI 那一档）"
        : "  连接方式：经本节点（页面只跟这一个源说话）");

    if (options.Bind is "0.0.0.0" or "*" or "::" or "[::]")
    {
        // 默认就是对外 ⇒ 每次都说一句代价（与核心仓那两句横幅同一个口径）。
        Console.Error.WriteLine("  ⚠️ 这个页面端口对局域网开放：同一网络里的设备都能打开它、并用它驱动核心。");
        Console.Error.WriteLine("     要收回本机：--bind 127.0.0.1（或 LILARA_HEAD_BIND=127.0.0.1）。");
        foreach (var address in LanAddresses(options.Port)) Console.WriteLine($"     可达地址 {address}");
    }

    if (supervisor.Ready)
        Console.WriteLine($"  打开 http://127.0.0.1:{options.Port} （Ctrl+C 退出）");
}

static void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        // 打不开浏览器不该影响服务：地址已经印在横幅上了。
        Console.Error.WriteLine($"  ⚠️ 没自动打开浏览器（{ex.Message}）：自己打开 {url}。");
    }
}

static IEnumerable<string> LanAddresses(int port)
{
    try
    {
        return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => $"http://{a}:{port}")
            .Distinct()
            .ToList();
    }
    catch (SocketException)
    {
        return [];
    }
}

// IPv6 字面量在 URL 里必须带方括号（`http://::1:17990` 解析不出来）；
// 而 `Request.Host.Host` 交出来的 IPv6 是**不带**方括号的。
static string HostForUrl(string address)
{
    var a = address.Trim();
    if (a.Length > 2 && a[0] == '[' && a[^1] == ']') return a;
    return a.Contains(':') ? $"[{a}]" : a;
}

// 健康检查：探**自己这个端口**上的 `/core/health`。
// 为什么不直接探核心：这条路径是经反代回来的——它同时证明了「页面端口在听」与「核心能应答」。
// ⚠️ 固定走 127.0.0.1：容器里那个进程总是听见 loopback（就算 `--bind 0.0.0.0` 也包含它）。
static async Task<int> HealthProbeAsync(RelayOptions options)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await http.GetAsync($"http://127.0.0.1:{options.Port}/core/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}
