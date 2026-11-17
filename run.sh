#!/bin/sh
# 一条命令：编 relay（增量）→ 起它。核心由 relay 按需起起来（已在跑则只接入）。
#
# ⚠️ 与 run.cmd 是同一个契约的两个实现（参数名、默认值、行为对齐；改一个必须改另一个）。
# ⚠️ 用 exec 而不是 `dotnet run`：让控制台里那个进程就是 relay 本身，Ctrl+C 的收尾只绕一圈
#    （先给核心写 quit，5 秒不走再整棵进程树 kill）。
set -eu

ROOT=$(cd "$(dirname "$0")" && pwd)

# 发布包里（publish.ps1 的产物）：可执行文件就在它旁边
if [ -x "$ROOT/lilara-relay" ]; then
    exec "$ROOT/lilara-relay" "$@"
fi

dotnet build "$ROOT/src/Lilara.Relay/Lilara.Relay.csproj" -c Debug -nologo -v q -clp:NoSummary
exec "$ROOT/src/Lilara.Relay/bin/Debug/net10.0/lilara-relay" "$@"
