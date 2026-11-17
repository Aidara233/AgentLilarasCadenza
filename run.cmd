@echo off
rem One command: build the relay (incremental), then run it. The relay starts the core
rem itself when nothing answers on /health (an already-running core is only attached to).
rem
rem NOTE: this file is deliberately PURE ASCII. cmd.exe reads a .cmd in the OEM code page,
rem and switching it mid-file (chcp) garbles the rest of the parse - measured here: the lines
rem after the Chinese comment produced "not recognized as an internal or external command".
rem The console's UTF-8 output is set by the relay itself, so nothing is lost.
rem
rem NOTE: it does NOT use `dotnet run`. That adds a process layer, so Ctrl+C reaches dotnet
rem first and the shutdown path (write "quit" to the core, then fall back to a tree kill)
rem gets harder to reason about. Build, then exec the exe: the console's process IS the relay.
setlocal
set "ROOT=%~dp0"

rem Published layout (publish.ps1 output): the executables sit next to this file.
if exist "%ROOT%lilara-relay.exe" (
  "%ROOT%lilara-relay.exe" %*
  exit /b %ERRORLEVEL%
)

rem Source layout: one incremental build, then run it.
dotnet build "%ROOT%src\Lilara.Relay\Lilara.Relay.csproj" -c Debug -nologo -v q -clp:NoSummary > "%TEMP%\lilara-relay-build.log" 2>&1
if errorlevel 1 (
  rem Build failures must not be swallowed by this quiet step: dump the whole log.
  type "%TEMP%\lilara-relay-build.log"
  exit /b 1
)
"%ROOT%src\Lilara.Relay\bin\Debug\net10.0\lilara-relay.exe" %*
exit /b %ERRORLEVEL%
