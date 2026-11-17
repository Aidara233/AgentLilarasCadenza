using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace Lilara.Relay;

/// <summary>
/// 核心的看护：**在就接入、不在就起、起了就收尾**（用户 2026-11 的方向：头可以负责起核心）。
///
/// <para>
/// ⚠️ <b>两个进程不变</b>。这里做的事只是「谁把核心拉起来」和「退出时谁去收它」，
/// 不是把核放进同一个进程——核心的生命周期、数据根锁、控制台命令面都还是它自己的。
/// 头一旦把核合进来，核心就不再能被单独重启、单独当纯手动头用，而那些都是既有裁定。
/// </para>
///
/// <para>
/// ⚠️ <b>「已有核心就不起」不是优化，是硬约束</b>：核心在数据根上有一把锁（一设备一核心），
/// 第二个核心起来会被挡回去。与其让用户看到一段「锁被占」的报错，不如先探 <c>/health</c>：
/// 有就接入、把它的地址印出来。
/// </para>
/// </summary>
internal sealed class CoreSupervisor(RelayOptions options) : IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(90);
    private const int TailLines = 12;

    private readonly Queue<string> _tail = new();
    private Process? _process;

    /// <summary>核心的访问 token：命令行给的，或者从它启动横幅里抓到的。</summary>
    public string? Token { get; private set; } = options.Token;

    /// <summary>核心是不是**这个头起的**。是的话退出时要负责收掉它。</summary>
    public bool Spawned => _process is not null;

    /// <summary>核心此刻答不答 <c>/health</c>。</summary>
    public bool Ready { get; private set; }

    public async Task EnsureAsync(CancellationToken ct)
    {
        if (await HealthyAsync(ct))
        {
            Ready = true;
            Console.WriteLine($"  核心已在跑：{options.Core}（头只接入，不另起——一个数据根只允许一个核心）");
            await ReportAuthAsync(ct);
            return;
        }

        if (!options.Spawn)
        {
            Warn("核心没应答，而 --no-spawn 说了不要去起它。页面打开后会一直「已断开·重连中」。");
            return;
        }

        // ⚠️ 上游不在本机时**绝不去起**：那是别人的核心，起一个本机的只会让人对着错的端口排错。
        if (!IsLoopback(options.Core))
        {
            Warn($"上游 {options.Core} 不在本机，头不去启动它。");
            return;
        }

        var launcher = ResolveLauncher();
        if (launcher is null)
        {
            Warn("核心没应答，也没找到起法。两条路：\n"
                 + "  · 起发布版/已构建的核心，再让头接入：--core-exe <path to lilara(.exe)>\n"
                 + "  · 本仓还没有核心产物：dotnet build core/src/Lilara.Cli/Lilara.Cli.csproj（或先跑一次 publish.ps1）");
            return;
        }

        Console.WriteLine($"  起核心：{launcher.Value.File} {string.Join(' ', launcher.Value.Args)}");
        if (!TryStart(launcher.Value, out var why))
        {
            Warn($"核心起不来：{why}");
            return;
        }

        Console.WriteLine($"  等核心应答 /health（最多 {SpawnTimeout.TotalSeconds:0} 秒）…");
        var started = Stopwatch.StartNew();
        var lastTick = 0;
        while (started.Elapsed < SpawnTimeout)
        {
            if (_process!.HasExited)
            {
                Warn($"核心在起来之前就退了（退出码 {_process.ExitCode}）。它最后几行：\n{Tail()}");
                return;
            }

            if (await HealthyAsync(ct))
            {
                Ready = true;
                Console.WriteLine($"  ✓ 核心已应答（{started.Elapsed.TotalSeconds:0.0} 秒）");
                await ReportAuthAsync(ct);
                return;
            }

            if ((int)started.Elapsed.TotalSeconds / 10 > lastTick)
            {
                lastTick = (int)started.Elapsed.TotalSeconds / 10;
                Console.WriteLine($"    …还在等（{started.Elapsed.TotalSeconds:0} 秒）");
            }

            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }
        }

        Warn($"等待核心超时（{SpawnTimeout.TotalSeconds:0} 秒）。它最后几行：\n{Tail()}");
    }

    private async Task ReportAuthAsync(CancellationToken ct)
    {
        var required = await AuthRequiredAsync(ct);
        if (!required) return;

        if (Token is null)
        {
            Console.WriteLine("  ⚠️ 核心开了鉴权（LILARA_AUTH=1），而头手里没有 token：");
            Console.WriteLine("     页面会在第一次连接时要你填——token 在核心控制台的启动横幅里（`访问 token …`）。");
            Console.WriteLine("     让头替你带：把那条 token 交给头（--token / LILARA_CORE_TOKEN），页面就一条凭据都不经手。");
            return;
        }

        Console.WriteLine("  鉴权：头带着 token 转给核心，页面一条凭据都不经手。");
    }

    private bool TryStart((string File, string[] Args) launcher, out string error)
    {
        var psi = new ProcessStartInfo(launcher.File)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = false,
        };
        foreach (var a in launcher.Args) psi.ArgumentList.Add(a);

        // 核心的端口以头的上游地址为准：两边不许各听各的（那是「页面连不上」最常见的形状）。
        psi.Environment["LILARA_PORT"] = options.Core.Port.ToString();
        // ⚠️ 不设 `LILARA_BIND`：核心怎么对局域网开放是它自己的开关，头不替它决定。

        try
        {
            _process = Process.Start(psi);
            if (_process is null) { error = "进程没起来（Process.Start 交了 null）"; return false; }

            _process.OutputDataReceived += (_, e) => OnLine(e.Data);
            _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnLine(string? line)
    {
        if (line is null) return;

        Console.WriteLine($"[核心] {line}");
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }

        // ⚠️ token 只出现在核心的启动横幅里（它是**每次启动生成**的，不是一个配置文件里的常量）。
        // 头起了核心，就能在这一行经过时顺手接住它 —— 于是「开了鉴权」也不必让用户把 token
        // 从核心的控制台抄到头的命令行里。
        const string Marker = "访问 token ";
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return;
        var value = line[(at + Marker.Length)..].Trim().Split(' ', '\t')[0];
        if (value.Length == 0) return;

        Token = value;
        Console.WriteLine("  （已从核心横幅拿到访问 token：头会替页面带着它转过去）");
    }

    private string Tail()
    {
        lock (_tail) return _tail.Count == 0 ? "  （它还没输出任何东西）" : string.Join("\n", _tail.Select(l => "  " + l));
    }

    private (string File, string[] Args)? ResolveLauncher()
    {
        // ① 显式指定
        if (options.CoreExe is { Length: > 0 } explicitExe)
            return Describe(explicitExe);

        // ② 发布布局：核心单文件就在本节点旁边
        var beside = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "lilara.exe" : "lilara");
        if (File.Exists(beside)) return (beside, []);

        // ③ 源码布局：子模块里的 CLI 项目，dotnet run（开发时最省事：改完核心直接重启这个头）
        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var project = Path.Combine(repoRoot, "core", "src", "Lilara.Cli", "Lilara.Cli.csproj");
            if (File.Exists(project))
                return ("dotnet", ["run", "--project", project, "-c", "Debug"]);
        }

        return null;

        (string, string[])? Describe(string path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return null;
            return full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", ["run", "--project", full, "-c", "Debug"])
                : (full, []);
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".gitmodules")))
                return dir.FullName;
        return null;
    }

    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = ProbeTimeout };
            using var response = await http.GetAsync(new Uri(options.Core, "/health"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    private async Task<bool> AuthRequiredAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = ProbeTimeout };
            using var response = await http.GetAsync(new Uri(options.Core, "/info"), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("authRequired", out var flag)
                   && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception) { return false; }
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static void Warn(string message) => Console.Error.WriteLine($"  ⚠️ {message}");

    /// <summary>
    /// 收尾：先礼后兵——往核心的 stdin 写 <c>quit</c>（那是它命令面上的正常退出），等 5 秒，
    /// 还不走就整棵进程树杀掉。
    ///
    /// <para>
    /// ⚠️ 头**只收自己起的那个**。接入别人已经在跑的核心时，退出不许去动它——用户可能正靠着
    /// 它跑任务、或者把它当纯手动头用。
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process is null) return;
        _process = null;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("quit");
                    await process.StandardInput.FlushAsync();
                }
                catch (Exception) { /* stdin 已经断了：直接走兜底 */ }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { /* 兜底在下面 */ }
            }

            if (!process.HasExited)
            {
                Console.Error.WriteLine("  ⚠️ 核心没在 5 秒内退出，强制结束它。");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ⚠️ 收核心时出错：{ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
