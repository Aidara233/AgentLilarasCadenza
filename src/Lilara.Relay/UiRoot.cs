using Microsoft.AspNetCore.Http;

namespace Lilara.Relay;

/// <summary>
/// 页面根目录：**找哪儿、放行哪些文件、不缓存**。
///
/// <para>
/// ⚠️ 这里刻意<b>不用 <c>UseStaticFiles</c></b>，而是自己放行一张白名单。理由是仓库布局：
/// Cadenza 的目录里除了页面还有 <c>serve.ps1</c> / <c>serve.py</c>（握 token 的启动脚本）、
/// <c>e2e/</c>（含 mock 上游与 trace 脚本）——把整个目录挂成静态站点，等于顺手把它们也发布出去。
/// 白名单只放 <c>index.html</c>、<c>css/*.css</c>、<c>js/*.js</c>，多一个文件都不给。
/// </para>
///
/// <para>
/// ⚠️ <b>不许被浏览器缓存</b>（拿核心仓那头 2026-10-06 的结论）：页面是「零构建」的，
/// <c>app.js</c> 这类文件名永远不换，浏览器完全有理由一直用旧的。于是「我改了、你说没效果」
/// 有四种成因，而它们在页面上长得一模一样。⇒ 两条一起上：这里发 <c>no-store</c>，
/// <c>/build.json</c> 把「你现在看的是哪一份」交出去。
/// </para>
/// </summary>
internal static class UiRoot
{
    /// <summary>
    /// 找页面根目录：显式给的 &gt; 可执行文件旁边的 <c>head/</c>（发布布局）&gt; 从可执行文件往上找到的
    /// <c>head/index.html</c>（源码布局）。
    ///
    /// <para>
    /// ⚠️ **找不到时说清三条路**，不要只说「找不到」：下一个人手里要么有 <c>--root</c>，
    /// 要么该去初始化子模块（<c>git submodule update --init</c>），要么是发布包缺了 <c>head/</c>。
    /// 这三种处置方式完全不同，而症状一模一样。
    /// </para>
    /// </summary>
    public static (string? Path, string? Error) Resolve(string? configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Verify(configured.Trim(), "（--root 指定的）");

        // ① 发布布局：可执行文件旁边就是 head/（publish.ps1 就是这么打包的）
        var beside = Path.Combine(baseDirectory, "head");
        if (File.Exists(Path.Combine(beside, "index.html")))
            return (Path.GetFullPath(beside), null);

        // ② 源码布局：本仓的 head/ 就在仓根。从 bin/Debug/net10.0 往上走几层就能见到。
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "head");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return (Path.GetFullPath(candidate), null);
        }

        return (null,
            "找不到页面（head/index.html）。三条路选一条：\n"
            + "  · 子模块还没拉下来：git submodule update --init --recursive\n"
            + "  · 页面在别处：--root <目录>（或 LILARA_HEAD_ROOT）\n"
            + "  · 发布包缺了 head/：重新解压，或把 head 目录放到可执行文件旁边。");
    }

    private static (string?, string?) Verify(string path, string whence)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(full, "index.html")))
            return (null, $"页面根 {full}{whence} 里没有 index.html。");
        return (full, null);
    }

    /// <summary>
    /// 静态这一层：白名单 + <c>no-store</c>。命中就发、没命中交给后面的中间件（<c>/config.json</c>、
    /// <c>/core/*</c>、端点）。
    /// </summary>
    public static Func<HttpContext, RequestDelegate, Task> Middleware(string root) =>
        async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                await next(ctx);
                return;
            }

            var path = ctx.Request.Path.Value is { Length: > 0 } p ? p : "/";
            if (path == "/") path = "/index.html";

            if (!TryResolve(root, path, out var file, out var contentType))
            {
                await next(ctx);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(file, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength = bytes.Length;
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        };

    /// <summary>
    /// 请求路径 → 磁盘文件。**只认白名单里那三类**，并且不许跳出根目录。
    ///
    /// <para>
    /// ⚠️ 判据里的两道都要：扩展名白名单挡「目录里别的产物」，路径规范化挡 <c>..</c> / 反斜杠 /
    /// 绝对路径（Windows 上 <c>..\</c> 与 <c>../</c> 都解得开，只查 <c>..</c> 这一种写法不够；
    /// 于是先拼、再规范化、再看前缀）。
    /// </para>
    /// </summary>
    public static bool TryResolve(string root, string requestPath, out string file, out string contentType)
    {
        file = "";
        contentType = "";

        var ext = Path.GetExtension(requestPath);
        // ⚠️ 放行的形状是「根下一层的页面文件」或「css/ 、js/ 里的一层」——不是「这些扩展名随便放」。
        // 旧 UI（core/src/Lilara.Head/wwwroot）用的是**根目录**的 index.html + app.js，
        // 而 Cadenza 是 index.html + css/ + js/：两种布局都要能服务，所以根下一层的
        // .html/.js/.css 也算白名单。子目录只认 css/ 与 js/ 这两个名字。
        var segments = requestPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var shallow = segments.Length == 1
                      || (segments.Length == 2 && segments[0] is "css" or "js");
        var allowed = (ext is ".html" or ".css" or ".js") && shallow;
        if (!allowed) return false;

        var full = Path.GetFullPath(Path.Combine(root, requestPath.TrimStart('/').Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
        var fence = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(fence, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;

        file = full;
        contentType = ext!.ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            _ => "application/octet-stream",
        };
        return true;
    }

    /// <summary>`/build.json` 用的那几个数：这份页面是从哪来的、什么时候写的、多大。</summary>
    public static (string ServedFrom, string? IndexWritten, long AppJsBytes, string? AppJsWritten) Describe(string root)
    {
        var index = Path.Combine(root, "index.html");
        var appJs = Path.Combine(root, "js", "app.js");
        return (
            root,
            File.Exists(index) ? File.GetLastWriteTime(index).ToString("yyyy-MM-dd HH:mm:ss") : null,
            File.Exists(appJs) ? new FileInfo(appJs).Length : 0,
            File.Exists(appJs) ? File.GetLastWriteTime(appJs).ToString("yyyy-MM-dd HH:mm:ss") : null);
    }
}
