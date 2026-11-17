using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Lilara.Relay;

/// <summary>
/// 反代：页面的 <c>/core/*</c> → 核心。
///
/// <para>
/// ⚠️ <b>为什么值得绕这一层</b>（用户 2026-11-14 的裁定，Cadenza README 里也写着）：
/// ① token 留在这一侧，浏览器与 localStorage 都不经手；② 页面只有**一个源**，
/// 于是没有 CORS / 混合内容 / <c>Origin</c> 白名单这些「同源以外」的坑；
/// ③ 换设备、换地址、走隧道、上 https，都不用再去改核心的 <c>LILARA_ALLOWED_ORIGIN</c>——
/// 核心只在握手**带了 Origin** 时才较真，而本进程不是浏览器、不发 Origin。
/// </para>
///
/// <para>
/// ⚠️ <b>两条路明显不一样</b>：HTTP 用 <c>HttpClient</c> 逐个字段地转；WebSocket 是
/// **两条泵**（客户端↔上游），不解帧、不懂协议——一条消息多少字节就转多少字节。
/// 后者才是它比「静态服务器 + 一点点路由」难写的那一处，也是它唯一不可替代的能力。
/// </para>
/// </summary>
internal static class CoreProxy
{
    /// <summary>上游要的 token 查询参数名——与核心 <c>ControlAuth.QueryParam</c> 是同一个字面量。</summary>
    private const string TokenQueryParam = "token";

    /// <summary>页面在代理模式下请求的 WS 子协议（<c>/config.json</c> 的 <c>wsProtocol</c> 就是它）。</summary>
    private const string WsSubProtocol = "cadenza";

    /// <summary>
    /// 不往上游带的请求头：逐跳的、由 HttpClient 自己管的，以及两个**故意**不带的。
    ///
    /// <para>
    /// ⚠️ <c>Origin</c> 不带：核心存在这条头时会拿它去比对白名单，而本进程**不是浏览器**——
    /// 带一条假 Origin 只是让核心的判据多一个说谎的输入。
    /// </para>
    /// <para>
    /// ⚠️ <c>Accept-Encoding</c> 不带：压缩只在这一层做（<c>UseResponseCompression</c>），
    /// 否则会出现「核心压一遍、头再压一遍」或者「头的缓存里躺着两份不同编码」这类形状。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "transfer-encoding", "upgrade", "origin", "accept-encoding", "content-length",
    };

    /// <summary>
    /// 不回给页面的响应头：逐跳的、以及长度与编码（本层会重算/重压，照抄会打架）。
    /// </summary>
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer-encoding", "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "trailer", "upgrade", "content-length", "content-encoding",
    };

    /// <summary>
    /// 把 <c>/core</c> 这一支接上。<paramref name="token"/> 每次请求都问一次：核心可能是头刚起的，
    /// token 是从它的启动横幅里**后**抓到的。
    /// </summary>
    public static void Map(WebApplication app, RelayOptions options, Func<string?> token)
    {
        // 一条 HttpClient 用到底：反代不需要 per-request 的处理器配置，而反复 new 会耗尽套接字。
        // `AutomaticDecompression = None` 与「不带 Accept-Encoding」是同一个决定的两半。
        // ⚠️ 处理器**不包 using**：它的寿命跟这个进程一样长，提前释放会让每条请求 500
        //（实测栽过：`using var handler` 在 Map 返回时就释放了，`/core/health` 一律 500、且
        // Kestrel 一个字都不说——日志 provider 也被清掉了）。
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            AllowAutoRedirect = false,
        })
        {
            // 大对象与长连接都从这条路走（`GET /store/{sha256}`、`POST /files`），不设总超时。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/core", out var rest))
            {
                await next();
                return;
            }

            if (string.Equals(rest.Value, "/ws", StringComparison.Ordinal))
            {
                await TunnelAsync(ctx, options.Core, token());
                return;
            }

            await ForwardAsync(ctx, http, options.Core, rest.Value ?? "", token());
        });
    }

    private static async Task ForwardAsync(HttpContext ctx, HttpClient http, Uri core, string rest, string? token)
    {
        var target = BuildUri(core, rest, ctx.Request.QueryString.Value);

        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        var hasBody = ctx.Request.ContentLength is > 0
                      || ctx.Request.Headers.ContainsKey("Transfer-Encoding");
        if (hasBody && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            request.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentLength is { } length)
                request.Content.Headers.ContentLength = length;
            if (ctx.Request.ContentType is { Length: > 0 } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (var header in ctx.Request.Headers)
        {
            if (SkipRequestHeaders.Contains(header.Key)) continue;
            var values = header.Value.ToArray();
            if (!request.Headers.TryAddWithoutValidation(header.Key, values))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }

        // ⚠️ 头握着 token 时，**页面带来的那条一律盖掉**：凭据只有一个来源，
        // 否则「谁在交凭据」在排错时说不清（页面那份可能是空的、也可能是过期的）。
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
                if (!SkipResponseHeaders.Contains(header.Key))
                    ctx.Response.Headers[header.Key] = header.Value.ToArray();
            foreach (var header in response.Content.Headers)
                if (!SkipResponseHeaders.Contains(header.Key))
                    ctx.Response.Headers[header.Key] = header.Value.ToArray();

            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // ⚠️ 响应已经开始就改不了状态码了：只能断开，让页面走它自己的「已断开·重连中」。
            if (ctx.Response.HasStarted)
            {
                ctx.Abort();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                $"上游核心没应答（{ex.GetType().Name}：{ex.Message}）。\n"
                + $"上游是 {core}。核心没起来 / 端口不对 / 它自己报错了——这三种在这一层看起来一样，\n"
                + "去核心那侧的控制台看它到底怎么了（头起的核心，它的输出会在头的控制台上带 [核心] 前缀）。\n");
        }
    }

    private static Uri BuildUri(Uri core, string rest, string? query)
    {
        var baseUrl = core.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var path = rest.StartsWith('/') ? rest : "/" + rest;
        return new Uri(baseUrl + path + (query ?? ""));
    }

    /// <summary>
    /// WS 隧道：先连上游、再接受页面——顺序不能反。
    ///
    /// <para>
    /// ⚠️ 一旦 <c>AcceptWebSocketAsync</c> 成功，HTTP 状态码就定死了（101）。
    /// 先接受再发现上游连不上，页面只会看到「连上了又立刻断」，而头这一侧一句原因都写不出去。
    /// 先连上游，失败还能老老实实回 502 + 一句人话。
    /// </para>
    /// </summary>
    private static async Task TunnelAsync(HttpContext ctx, Uri core, string? token)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("/core/ws 只收 WebSocket 握手。");
            return;
        }

        var upstreamUrl = BuildWsUri(core, token);
        using var upstream = new ClientWebSocket();
        try
        {
            await upstream.ConnectAsync(upstreamUrl, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                $"转不上上游 WS（{ex.GetType().Name}：{ex.Message}）。\n上游是 {upstreamUrl.GetLeftPart(UriPartial.Path)}。\n");
            return;
        }

        // ⚠️ 子协议只在页面**请求过**时才接受：`AcceptWebSocketAsync("cadenza")` 对没请求它的客户端
        // 会直接抛。浏览器这一侧按 `/config.json` 的 `wsProtocol` 带它；排错用的裸客户端不带。
        var offered = ctx.WebSockets.WebSocketRequestedProtocols;
        using var client = offered.Contains(WsSubProtocol, StringComparer.Ordinal)
            ? await ctx.WebSockets.AcceptWebSocketAsync(WsSubProtocol)
            : await ctx.WebSockets.AcceptWebSocketAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var clientToCore = PumpAsync(client, upstream, cts.Token);
        var coreToClient = PumpAsync(upstream, client, cts.Token);

        await Task.WhenAny(clientToCore, coreToClient);
        // ⚠️ 一侧结束了**不立刻取消另一侧**：先给对方一点时间把关闭握手走完。
        // 客户端先发 Close 时，它在等我们回一个 Close——那一下正是「另一条泵」转的。
        // 直接断开会让它看到「对面没有完成关闭握手」（本机实测：`ClientWebSocket` 当场抛
        // `The remote party closed the WebSocket connection without completing the close handshake`）。
        await Task.WhenAny(Task.WhenAll(clientToCore, coreToClient), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

        cts.Cancel();
        try { await Task.WhenAll(clientToCore, coreToClient); } catch (Exception) { /* 断开即正常结局 */ }
    }

    private static Uri BuildWsUri(Uri core, string? token)
    {
        var builder = new UriBuilder(core)
        {
            Scheme = core.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/ws",
            Query = string.IsNullOrEmpty(token)
                ? ""
                : $"{TokenQueryParam}={Uri.EscapeDataString(token)}",
        };
        return builder.Uri;
    }

    /// <summary>
    /// 一条泵：把 <paramref name="from"/> 收到的每一帧原样交给 <paramref name="to"/>。
    ///
    /// <para>
    /// ⚠️ 逐帧转，<b>不攒消息</b>：<c>ReceiveAsync</c> 返回的可能是半条消息（<c>EndOfMessage == false</c>），
    /// 照着那个标志往下传，对面看到的边界就还是原来的边界。攒起来再发会把「大帧」变成
    /// 「一次巨大的分配」，而这层本来就不该理解里面是什么。
    /// </para>
    /// </summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (from.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
            {
                var received = await from.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // 关帧要**转给对面**：客户端先发 Close 时它在等这一下（不回就是「对面粗暴切断」）。
                    // 对面也已经在关（CloseSent）时这里会抛——那说明两侧几乎同时关，握手交给超时收场。
                    try
                    {
                        await to.CloseOutputAsync(from.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            from.CloseStatusDescription, CancellationToken.None);
                    }
                    catch (Exception) { /* 对面已经发过 Close */ }
                    return;
                }

                // 目标已在关闭 ⇒ 数据帧不该再塞过去（塞了会抛，而这不是错误，是对面先走了）。
                if (to.State != WebSocketState.Open) return;

                await to.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType,
                    received.EndOfMessage, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 对面先断了 ⇒ 本侧被取消：这是正常结局，不是错误。
        }
        catch (WebSocketException)
        {
            // 连接被对端粗暴关掉（刷新页面、杀进程）——同样不需要往控制台刷一段栈。
        }
    }
}
