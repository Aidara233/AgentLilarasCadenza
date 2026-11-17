# AgentLilarasCadenza · 拼合层

> **核心提供服务，头做中转与扩展。** 页面绝大多数时候连的是**已有的头**；排错或没有头时，
> 也可以**直连核心**。核心与头仍然是两个独立进程——只是这一段里，**头可以负责把核心起起来**。

这个仓**不是**第三个 agent 实现，它是「把两半拼成一条能跑的链」的那一层：

| 目录 / 项目 | 是什么 | 来源 |
|---|---|---|
| `core/` | 核心：`Lilara.Core` 内核 + `Lilara.Cli` 常驻程序（控制面 HTTP/WS，默认 17988） | 子模块 → [Aidara233/AgentLilaraCode](https://github.com/Aidara233/AgentLilaraCode) |
| `head/` | Cadenza：浏览器头（零构建静态页，`index.html` + `js/` + `css/`） | 子模块 → [Aidara233/Cadenza](https://github.com/Aidara233/Cadenza) |
| `src/Lilara.Relay/` | **本仓自己的项目**：中转节点（托管页面 + `/core/*` 反代 + WS 隧道 + 按需起核心） | 这里 |

三条纪律（都不是新发明的，是照既有裁定沿用）：

1. **核心不知道头在不在。** Relay 不引用 `Lilara.Core` 的任何代码，两半之间只有 HTTP/WS 与进程边界。
2. **头是可替换件。** 换掉 `src/Lilara.Relay` 或 `head/` 都不该逼核心改一行。
3. **凭据默认不进浏览器。** 经头这一层时，核心的访问 token 留在进程里（页面 `/config.json` 读到 `auth: "head"`）。

---

## 跑起来

```bash
# 第一次：把两个子模块拉下来
git submodule update --init --recursive

# 一条命令（Windows）
run.cmd
# 一条命令（macOS / Linux / WSL / Git Bash）
./run.sh
```

它做四件事：编 relay → 探核心（`/health`）→ 没有就按 `--core-exe` / 包里的 `lilara` / `core/src/Lilara.Cli` 的顺序起一个 → 起页面（默认 <http://127.0.0.1:17990>）并开浏览器。

**已经有核心在跑时只接入，不另起**（一个数据根只允许一个核心）。核心不在本机时也绝不去起它。

排错时想要的三种变体：

```bash
# 只接入，不许它起核心
lilara-relay --no-spawn

# 页面直连核心（旧 UI 的兜底 / 对照面）：旧 UI 在 core/src/Lilara.Head/wwwroot
lilara-relay --direct --root core/src/Lilara.Head/wwwroot

# 上游在别的机器 / 走了隧道
lilara-relay --core http://10.0.0.7:17988 --token <核心的 token>
```

### 网络开放程度（两边各管各的）

| 谁 | 开关 | 默认 |
|---|---|---|
| 头（本节点） | `--bind` / `LILARA_HEAD_BIND`、`--port` / `LILARA_HEAD_PORT` | `0.0.0.0:17990`（对局域网开放） |
| 核心 | `LILARA_BIND`、`LILARA_PORT`、`LILARA_AUTH=1` | `0.0.0.0:17988`、不校验 token |

两件事值得知道：

- **经头这一层时，核心的 `LILARA_ALLOWED_ORIGIN` 白名单根本用不上**：握手是本进程发的、不带 `Origin`。
  于是换设备、换地址、上隧道、上 https 都不用去改核心的配置。
- **Relay 走 Kestrel（原始套接字）**：老头的 `serve.ps1` 在 Windows 上绑 `0.0.0.0` 需要一条
  `netsh http add urlacl …`（而且要付出「`127.0.0.1` 从此绑不上」的代价），这里**不需要**那条 ACL。

---

## 和两个旧仓库的关系

两个子模块是**真引用**，不是拷贝：

```bash
git submodule update --remote core head     # 拉上游最新
git -C core  status / git -C head status    # 看各自有没有本地改动
```

⚠️ 本仓对 `head/` 的改动目前只有 `head/e2e/run.ps1`（把「起头」从 `serve.ps1` 换成 `lilara-relay`）。
那一条是**故意的**：e2e 从此覆盖的是新反代。合上游时留意这一处冲突。

⚠️ `head/serve.ps1` / `serve.py` / `serve.sh` 仍然留在 Cadenza 里（它们对别的平台仍然可用），
但**本仓的启动路径不再用它们**：反代是 `src/Lilara.Relay` 那个 C# 进程。
这确实推翻了 Cadenza 2026-10-23 那条「只做跨平台脚本、不另起 exe 宿主」的裁定——
用户 2026-11 重新裁定的方向是「头作为中转节点，可以负责起核心」，宿主只能是进程，脚本做不了这件事。

---

## 发布（一体包）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 [-Version 1.1.0] [-Rids win-x64,linux-x64]
```

产物 `publish/lilara-<ver>-<rid>/` 里是：

```
lilara.exe         核心（自包含单文件，目标机不需要 .NET）
lilara-relay.exe   中转节点（自包含单文件）
head/              Cadenza 的静态文件（index.html + css/ + js/）
run.cmd / run.sh   解压就能跑的那一条命令
```

再加一个 zip / tar.gz。**这是核心仓 `docs/实现现状.md` 里「没有统一的发布/安装/启动包」那条的落点。**

---

## Docker

```bash
docker compose up -d --build        # 也可以：docker build -t aidara233/agentlilarascadenza:1.1.0 .
# 打开 http://127.0.0.1:17990
```

容器里仍然是**两个进程**（核心 + 中转节点），拓扑与宿主机一致；差别只有三处，且都是刻意的：

| | 宿主机 | 容器 |
|---|---|---|
| 核心监听 | `0.0.0.0:17988` | **`127.0.0.1:17988`**（只有同一个容器里的头够得着；`-p` 只映射 17990） |
| 二进制 | 自包含单文件（目标机不装 .NET） | 框架依赖（镜像里本来就有 ASP.NET 运行时；自包含要多塞 ~100 MB） |
| 健康检查 | 自己看横幅 | `HEALTHCHECK` 跑 `lilara-relay --health`（探 `/core/health`，即「头在听 **且** 核心应答」） |

两个卷，职责不同：

- `/data` —— 数据根（会话、提供商 key、用量账本）。容器换了、重建了，它要留着。
- `/workspace` —— **核心拿进程的工作目录当项目键**，会话是按项目分目录存的。换成你自己的项目：
  `LILARA_WORKSPACE=/path/to/your/project docker compose up -d`。

想开访问令牌：`LILARA_AUTH=1 docker compose up -d`。token 打印在容器日志的启动横幅里，**头会自己接住**
并转给核心，页面一条凭据都不经手（`docker compose logs lilara | grep token`）。

想让核心也能被别的头 / 直连客户端连上：`LILARA_BIND=0.0.0.0` 并在 compose 里加 `- "17988:17988"`。
⚠️ 核心默认**不校验** token——把它露出去之前，先设 `LILARA_AUTH=1`。

`docker-compose.yml` 末尾另有一段注释掉的**分体拓扑**（核心一个容器、头一个容器，头用
`--core http://core:17988 --no-spawn` 指向它）——分体时 `--no-spawn` 不是可选项：头起不了另一个
容器里的核心，硬起只会得到一个撞上数据根锁的本机核心，而症状与「核心没起来」完全同形。

⚠️ 挂载宿主目录时属主可能对不上（镜像里的用户是 `10001`）。两种处理：把宿主目录
`chown 10001`，或者 `docker run --user $(id -u):$(id -g) …`（这时 `/data` 也要可写）。

---

## 验证边界（诚实版）

跑过：

- `dotnet build AgentLilarasCadenza.slnx`（核心 + 旧头 + relay + 两套测试项目）零警告零错误。
- `head/e2e/run.ps1` 在 relay 上：`1-boot`、`22-proxy`、`2-flow`、`3-asks-axes`、`4-views-layout`
  全部 `0 failed, 0 uncaught`。也就是说流式输出、工具调用、询问卡、轴与权限、打断、计划、
  用量账本这些走会话的路，都是**经新反代**跑通的。
- 手动：`/config.json`、`/build.json`、静态白名单（Cadenza 布局与旧 UI 布局各一遍：
  `serve.ps1` / `e2e/mock.cs` / `README.md` 一律 404）、`/core/health|info|axes|sessions` 转发、
  WS 握手（子协议 `cadenza`）+ 一次命令往返 + 正常关闭握手。
- 起核心：隔离数据根 + 空闲端口上，源码态（`dotnet run --project core/src/Lilara.Cli`）与
  发布态（包里的 `lilara.exe`）各验一次「由头起核心 → 从横幅抓到 token → `auth: "head"`」。
- 一体包：`publish.ps1 -Rids win-x64` 产出目录与 zip 完整，解包目录里 `run.cmd` 直接能跑。

**没有**验过：

- 真实模型端到端（需要真的 provider key 与账单）；
- `linux-x64` **自包含包在真机上跑**（本机是 Windows：只核了产物是 ELF、包里可执行位 755）；
- Ctrl+C 时「先给核心写 `quit`、5 秒不走再整棵进程树 kill」那条收尾路径
  （代码在 `CoreSupervisor.DisposeAsync`；目前只验到「被强制结束时不会把核心留成孤儿」这一反方向）。
- **Docker 镜像本身没构建过**：这台机器没装 docker。Dockerfile 里那两条 `dotnet publish`
  在本机按 `linux-x64` 逐字跑过（产物是 ELF apphost），`--health` 也正反两例验过，
  但「镜像能起来、卷能挂、HEALTHCHECK 真的变 healthy」这三件只能在你那边第一次 `docker compose up` 时见分晓。
- ⚠️ **子模块的版本指针**：本仓钉住的是 `core/` 与 `head/` 各自的提交，不是标签。
  1.1.0 那一版对应哪两个提交，看 `git submodule status`。


