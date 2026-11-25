# syntax=docker/dockerfile:1
#
# 一体镜像：核心 + 中转节点 + Cadenza 页面，**一个容器里两个进程**（与宿主机上跑的是同一个拓扑）。
#
#   docker build -t aidara233/agentlilarascadenza:1.2.0 .
#   docker run --rm -p 17990:17990 -v lilara-data:/data aidara233/agentlilarascadenza:1.2.0
#
# 三条与宿主机**不一样**、且都是刻意的选择：
#
# ⚠️ ① **镜像里不装 curl / wget**。HEALTHCHECK 走 `lilara-relay --health`
#      （它探自己端口上的 /core/health，通就退 0）。为一条健康检查往镜像里塞一个 HTTP 客户端
#      不划算，而那条路径本来就在程序手里。
#
# ⚠️ ② **核心在容器内只绑 loopback**（LILARA_BIND=127.0.0.1）：能穿过容器边界进来的只有头的
#      17990。核心的 17988 故意不暴露——「外面谁能驱动核心」这个决定，在这一档里被收成一个口子。
#      要让别的头 / 直连客户端连进来，就把 LILARA_BIND 设成 0.0.0.0 并自己加一条 -p 17988:17988
#      （核心默认**不校验** token，见 README 的「网络开放程度」）。
#
# ⚠️ ③ **这里发的是框架依赖版**（镜像里本来就有 ASP.NET 运行时），不是宿主机那种自包含单文件：
#      同样一份代码，自包含要往镜像里多塞 ~100 MB 运行时副本。

# ---------- 构建 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只抄项目文件，让 restore 这一层能被缓存住：改代码不必重拉 NuGet。
COPY core/src/Lilara.Core/Lilara.Core.csproj ./core/src/Lilara.Core/
COPY core/src/Lilara.Cli/Lilara.Cli.csproj ./core/src/Lilara.Cli/
COPY src/Lilara.Relay/Lilara.Relay.csproj ./src/Lilara.Relay/
RUN dotnet restore ./core/src/Lilara.Cli/Lilara.Cli.csproj \
 && dotnet restore ./src/Lilara.Relay/Lilara.Relay.csproj

COPY core/ ./core/
COPY src/ ./src/

# TARGETARCH 由 BuildKit 提供（amd64 / arm64）：RID 那一维要手动映射，
# 因为 .NET 叫 `linux-x64` 而 Docker 叫 `amd64`。
ARG TARGETARCH=amd64
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && echo "target RID: $RID" \
 && dotnet publish ./core/src/Lilara.Cli/Lilara.Cli.csproj -c Release -r "$RID" \
      --no-self-contained -p:PublishSingleFile=false -o /out \
 && dotnet publish ./src/Lilara.Relay/Lilara.Relay.csproj -c Release -r "$RID" \
      --no-self-contained -p:PublishSingleFile=false -o /out

# ---------- 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# 非 root：数据根与工作区都要它可写。
# ⚠️ 挂卷之后宿主目录的属主可能不是 10001 —— 那种情况在 README 的 Docker 一节里说了怎么处理
#    （`--user $(id -u):$(id -g)` 或把宿主目录 chown 成 10001）。
RUN useradd --create-home --uid 10001 lilara \
 && mkdir -p /data /workspace \
 && chown lilara:lilara /data /workspace

COPY --from=build /out/ /app/

# 页面：只放能发出去的那三个东西。serve.ps1 / serve.py / e2e 由 .dockerignore 挡在上下文之外
# （与 relay 的静态白名单、与 publish.ps1 的打包范围是同一个决定的三处落地）。
COPY head/index.html /app/head/
COPY head/css/ /app/head/css/
COPY head/js/ /app/head/js/

ENV LILARA_HOME=/data \
    LILARA_BIND=127.0.0.1 \
    LILARA_HEAD_BIND=0.0.0.0 \
    LILARA_HEAD_PORT=17990 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# 数据根（会话、提供商 key、账本）与工作区（核心拿它当项目键）分开挂：
# 换一个工作区不该把整个数据根一起换掉。
VOLUME ["/data", "/workspace"]
EXPOSE 17990

USER lilara

# 起得来 ≠ 能用：这条探的是「头在听 且 核心应答」。
HEALTHCHECK --interval=30s --timeout=5s --start-period=25s --retries=3 \
  CMD ["/app/lilara-relay", "--health"]

# 中转节点是 PID 1：SIGTERM 由它接（.NET 的优雅关闭），它会先给核心写 `quit`、5 秒不走再收树。
ENTRYPOINT ["/app/lilara-relay"]
