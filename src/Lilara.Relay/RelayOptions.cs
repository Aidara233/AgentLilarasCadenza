using System.Globalization;

namespace Lilara.Relay;

/// <summary>
/// 中继节点的启动参数：**一份解析、一处默认值**。
///
/// <para>
/// ⚠️ 变量名沿用核心仓老头的这一套（<c>LILARA_HEAD_PORT</c> / <c>LILARA_HEAD_BIND</c> /
/// <c>LILARA_CORE</c> / <c>LILARA_CORE_TOKEN</c>），不是为了好看：老头的那些启动脚本、快捷方式、
/// 环境变量在用户机器上都已经落成习惯，换一套名字等于让「我知道怎么开」突然失效。
/// 命令行参数只是同一批开关的另一种写法，**同名同义**。
/// </para>
///
/// <para>
/// ⚠️ 这里是**头**的开关，核心的开关（<c>LILARA_PORT</c> / <c>LILARA_BIND</c> /
/// <c>LILARA_AUTH</c> / <c>LILARA_ALLOWED_ORIGIN</c>）一个都不复制：核心怎么听、对谁开放，
/// 是核心自己的事。头只负责「把页面的 <c>/core/*</c> 转给它」，以及「要不要替用户把它起起来」。
/// </para>
/// </summary>
internal sealed record RelayOptions
{
    /// <summary>页面这一侧的端口。默认与核心仓老头一致（17990，两侧默认值成对改）。</summary>
    public int Port { get; init; } = 17990;

    /// <summary>
    /// 页面这一侧的监听地址。默认 <c>0.0.0.0</c>（与核心同一裁定：手机/平板直接打开）。
    ///
    /// <para>
    /// ⚠️ 老头的 Windows 实现走 <c>HttpListener</c>，绑 <c>0.0.0.0</c> 要一条 URL ACL
    /// （<c>netsh http add urlacl …</c>）；本节点走 <b>Kestrel（原始套接字）</b>，
    /// **不需要那条 ACL**，也不会出现「加了 ACL 之后 127.0.0.1 再绑不上」那种反向坑。
    /// 这一条是换宿主顺带拿到的，不是刻意设计——写在这里，免得下一个人照抄旧文档。
    /// </para>
    /// </summary>
    public string Bind { get; init; } = "0.0.0.0";

    /// <summary>上游核心的地址。默认 <c>http://127.0.0.1:17988</c>。</summary>
    public Uri Core { get; init; } = new("http://127.0.0.1:17988");

    /// <summary>
    /// 核心的访问 token（核心开了 <c>LILARA_AUTH=1</c> 时才需要）。
    ///
    /// <para>
    /// ⚠️ <b>给了它就一条都不让页面带</b>（<c>/config.json</c> 的 <c>auth</c> 交 <c>"head"</c>）：
    /// 凭据留在这一侧，浏览器与 localStorage 都不经手——这是「由头的进程连核心」那条裁定的全部好处。
    /// 不给则是老形态（<c>auth: "page"</c>），页面自己弹闸、自己带。
    /// </para>
    /// </summary>
    public string? Token { get; init; }

    /// <summary>页面根目录（<c>index.html</c> 所在）。不给就从可执行文件旁边 / 仓根往上找。</summary>
    public string? Root { get; init; }

    /// <summary>启动后自动打开浏览器（<c>--no-open</c> 关掉；e2e 就是靠它不弹窗）。</summary>
    public bool Open { get; init; } = true;

    /// <summary>
    /// 退成「页面直连核心」（<c>/config.json</c> 交 <c>{ core }</c> 而不是 <c>{ proxy }</c>）。
    ///
    /// <para>
    /// 这一档存在有两个理由，两个都不是怀旧：①核心仓里头 <c>src/Lilara.Head</c> 的旧 UI 只认
    /// <c>{ core }</c>，要拿本节点当它的静态宿主就得交这个形状（<c>--root core/src/Lilara.Head/wwwroot --direct</c>）；
    /// ②排错时要一个「不经过我」的对照面。
    /// </para>
    /// </summary>
    public bool Direct { get; init; }

    /// <summary>
    /// 核心没在跑时，**由头把它起起来**（用户 2026-11 的方向：头可以负责起核心）。
    ///
    /// <para>
    /// ⚠️ 只在三种情况下起：上游是本机回环、<c>/health</c> 没应答、且找得到起法。
    /// 上游在别的机器上时**绝不去起**（那是别人的核心，起一个本机的只会让人对错端口）。
    /// </para>
    /// </summary>
    public bool Spawn { get; init; } = true;

    /// <summary>显式指定核心的可执行文件（<c>--core-exe</c>）。不给就按「可执行文件旁边 → 仓里的 CLI 项目」找。</summary>
    public string? CoreExe { get; init; }

    /// <summary>
    /// 当健康检查用：探自己那个端口上的 <c>/core/health</c>，通就退 0、不通退 1，**不起任何服务**。
    ///
    /// <para>
    /// ⚠️ 存在的理由很具体：Docker 的 <c>HEALTHCHECK</c> 需要一条命令，而基础镜像里
    /// 没有 curl 也没有 wget（<c>aspnet</c> 那个镜像不带）。为了健康检查往镜像里装一个 HTTP
    /// 客户端，比让程序自己多一个开关更贵——而这条路径本来就在它手里。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 它探的是**两层**：<c>/core/health</c> 要经头的反代到达核心，所以「头活着但核心死了」
    /// 会如实报不健康。这正是容器编排要的那个判据（页面列表能开、会话却跑不动，比整台挂掉更难查）。
    /// </para>
    /// </summary>
    public bool Health { get; init; }

    /// <summary>上游核心的地址，**不带尾斜杠**（横幅、`/config.json`、`/build.json` 用的是同一个字面量）。</summary>
    public string CoreText => Core.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public sealed record ParseResult(RelayOptions? Options, string? Error, bool Help);

    /// <summary>
    /// 解析命令行 + 环境变量。命令行优先。
    ///
    /// <para>⚠️ 解析保持**纯函数**（环境变量从参数注入），这样「哪个变量赢了」可以被单测钉住——
    /// 老头那套默认值与核心的白名单是两份副本，漂过一次就够难受了。</para>
    /// </summary>
    public static ParseResult Parse(string[] args, Func<string, string?> getEnv)
    {
        int? port = null;
        string? bind = null, core = null, token = null, root = null, coreExe = null;
        bool? open = null, direct = null, spawn = null;
        bool health = false;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            var arg = raw;
            string? inline = null;
            var eq = raw.IndexOf('=');
            if (raw.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                arg = raw[..eq];
                inline = raw[(eq + 1)..];
            }

            string Value()
            {
                if (inline is not null) return inline;
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{arg} 后面缺一个值。");
                return args[++i];
            }

            try
            {
                switch (arg)
                {
                    case "-h" or "--help":
                        return new ParseResult(null, null, Help: true);
                    case "--port":
                        port = ParsePort(Value());
                        break;
                    case "--bind":
                        bind = Value();
                        break;
                    case "--core":
                        core = Value();
                        break;
                    case "--token":
                        token = Value();
                        break;
                    case "--root":
                        root = Value();
                        break;
                    case "--core-exe":
                        coreExe = Value();
                        break;
                    case "--open":
                        open = true;
                        break;
                    case "--no-open":
                        open = false;
                        break;
                    case "--direct":
                        direct = true;
                        break;
                    case "--proxy":
                        direct = false;
                        break;
                    case "--no-spawn":
                        spawn = false;
                        break;
                    case "--spawn":
                        spawn = true;
                        break;
                    case "--health":
                        health = true;
                        break;
                    default:
                        throw new ArgumentException($"不认识的参数 {arg}。用 --help 看一遍。");
                }
            }
            catch (ArgumentException ex)
            {
                return new ParseResult(null, ex.Message, Help: false);
            }
        }

        // ---- 环境变量兜底（命令行 > 环境 > 默认）----
        port ??= int.TryParse(getEnv("LILARA_HEAD_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? p
            : null;

        // ⚠️ 上游地址有两种写法，`LILARA_CORE` 赢：它是「核心在别处 / 走了隧道」那条路。
        // 没设它时按 `LILARA_PORT` 推，推不出来才是 17988——与核心默认端口是同一个事实。
        core ??= getEnv("LILARA_CORE") is { Length: > 0 } explicitCore
            ? explicitCore.Trim()
            : int.TryParse(getEnv("LILARA_PORT"), out var corePort)
                ? $"http://127.0.0.1:{corePort}"
                : "http://127.0.0.1:17988";

        bind ??= getEnv("LILARA_HEAD_BIND") is { Length: > 0 } b ? b.Trim() : "0.0.0.0";
        token ??= getEnv("LILARA_CORE_TOKEN") is { Length: > 0 } t ? t.Trim() : null;
        root ??= getEnv("LILARA_HEAD_ROOT") is { Length: > 0 } r ? r.Trim() : null;
        coreExe ??= getEnv("LILARA_CORE_EXE") is { Length: > 0 } e ? e.Trim() : null;
        spawn ??= !IsTruthy(getEnv("LILARA_RELAY_NO_SPAWN"));

        if (!Uri.TryCreate(core, UriKind.Absolute, out var coreUri)
            || (coreUri.Scheme != Uri.UriSchemeHttp && coreUri.Scheme != Uri.UriSchemeHttps))
            return new ParseResult(null, $"上游核心地址看着不像 http(s) URL：{core}", Help: false);

        return new ParseResult(new RelayOptions
        {
            Port = port ?? 17990,
            Bind = bind,
            Core = coreUri,
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            Root = string.IsNullOrWhiteSpace(root) ? null : root.Trim(),
            Open = open ?? true,
            Direct = direct ?? false,
            Spawn = spawn ?? true,
            CoreExe = string.IsNullOrWhiteSpace(coreExe) ? null : coreExe.Trim(),
            Health = health,
        }, null, Help: false);
    }

    /// <summary>端口只做范围检查——**不许静默换端口**（核心仓 docs/05 §2.1 的同一条纪律）。</summary>
    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535
            ? p
            : throw new ArgumentException($"端口看着不像端口：{value}");

    internal static bool IsTruthy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "0" or "false" or "no" or "off" => false,
        _ => true,
    };

    public static void PrintHelp()
    {
        Console.WriteLine("""
            Lilara.Relay —— Lilara 的中转节点（头）

            用法：lilara-relay [开关]
              页面   http://<bind>:<port>          （Cadenza 静态文件 + /core/* 反代）
              核心   --core 指向的那台             （本机没在跑时，头会按需把它起起来）

            开关（命令行优先；括号里是等价的环境变量）
              --port <n>        页面端口，默认 17990            （LILARA_HEAD_PORT）
              --bind <addr>     页面监听地址，默认 0.0.0.0      （LILARA_HEAD_BIND）
              --core <url>      上游核心，默认 127.0.0.1:17988  （LILARA_CORE / LILARA_PORT）
              --token <token>   核心的访问 token（核心开了鉴权时）（LILARA_CORE_TOKEN）
              --root <dir>      页面根目录（默认自动找 head/）  （LILARA_HEAD_ROOT）
              --core-exe <path> 起核心用的可执行文件（默认自动找）（LILARA_CORE_EXE）
              --direct          退成「页面直连核心」（为旧 UI / 排错留着）
              --no-spawn        核心没在跑时不要去起它            （LILARA_RELAY_NO_SPAWN=1）
              --no-open         不自动开浏览器
              --health          当健康检查用：探 /core/health，通退 0、不通退 1
              -h, --help        这一屏

            两种连接方式（页面读 /config.json 决定）：
              默认   页面 → 头（同源 /core/*）→ 核心     头握着 token，换设备/换地址不用动核心白名单
              --direct 页面 → 核心（绝对地址）            排错对照面，旧 UI 只认这个形状
            """);
    }
}
