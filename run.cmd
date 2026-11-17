@echo off
rem 一条命令：编 relay（增量）→ 起它。核心由 relay 按需起起来（已在跑则只接入）。
rem
rem ⚠️ 刻意**不用** `dotnet run`：那会多一层进程，于是 Ctrl+C 先到 dotnet、再到 relay，
rem    「先给核心写 quit、再兜底 kill」那条收尾就多绕一圈、也更难判断谁该负责。
rem    这里编完直接跑 exe，控制台里那个进程就是 relay 本身。
rem ⚠️ 字符集：下面的中文只是注释；控制台的中文由 relay 自己设成 UTF-8（它启动时会设）。
chcp 65001 >nul 2>&1
setlocal
set "ROOT=%~dp0"

rem 发布包里（publish.ps1 的产物）：可执行文件就在它旁边
if exist "%ROOT%lilara-relay.exe" (
  "%ROOT%lilara-relay.exe" %*
  exit /b %ERRORLEVEL%
)

rem 源码里：增量构建一次，再跑产物
dotnet build "%ROOT%src\Lilara.Relay\Lilara.Relay.csproj" -c Debug -nologo -v q
if errorlevel 1 exit /b 1
"%ROOT%src\Lilara.Relay\bin\Debug\net10.0\lilara-relay.exe" %*
exit /b %ERRORLEVEL%
