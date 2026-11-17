# 一体包：核心 + 中转节点 + Cadenza 页面，解压就能跑。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 [-Version 1.0.0] [-Rids win-x64,linux-x64]
#
# 产物（publish\）：
#   lilara-<v>-<rid>\         lilara(.exe) + lilara-relay(.exe) + head\ + run.cmd/run.sh
#   lilara-<v>-<rid>.zip      （win）
#   lilara-<v>-<rid>.tar.gz   （linux，包里的可执行位已置 755）
#   SHA256SUMS
#
# ⚠️ 这一条正是核心仓 docs/实现现状.md 里「没有统一的发布/安装/启动包；使用者需要分别构建并启动
# 核心和头部」那条的落点：两个单文件 exe + 一份静态页面 + 一条命令。
#
# ⚠️ 两个 exe 都必须 `--self-contained` + `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`：
# 核心托管 Kestrel/WS、中转节点托管 Kestrel，原生库不内嵌的话，单文件版第一次跑会在临时目录里
# 找不到它们，而报的错与真因隔着一层（核心仓 2026-10-23 的结论，照抄）。
param(
    [string]$Version = "1.0.0",
    [string[]]$Rids = @("win-x64", "linux-x64")
)
$ErrorActionPreference = "Stop"

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = $PSScriptRoot
$out = Join-Path $root "publish"
$coreProj = Join-Path $root "core\src\Lilara.Cli\Lilara.Cli.csproj"
$relayProj = Join-Path $root "src\Lilara.Relay\Lilara.Relay.csproj"
$headSrc = Join-Path $root "head"

if (-not (Test-Path (Join-Path $headSrc "index.html"))) {
    throw "缺少 head\index.html —— 先跑 git submodule update --init --recursive"
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($rid in $Rids) {
    $dir = Join-Path $out "lilara-$Version-$rid"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }

    $common = @(
        "-c", "Release", "-r", $rid, "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:Version=$Version",
        "-o", $dir
    )

    Write-Host "=== publish $rid 核心" -ForegroundColor Cyan
    dotnet publish $coreProj @common
    if ($LASTEXITCODE -ne 0) { throw "核心发布失败：$rid" }

    Write-Host "=== publish $rid 中转节点" -ForegroundColor Cyan
    dotnet publish $relayProj @common
    if ($LASTEXITCODE -ne 0) { throw "中转节点发布失败：$rid" }

    # 页面：只带「能被发出去的那三个东西」。
    # ⚠️ **不整目录复制**：Cadenza 里还有 serve.ps1 / serve.py（握着 token 的启动脚本）与 e2e\
    #（含 mock 上游、trace 脚本）。它们不该出现在发布包里——中转节点那边也只放行这三个白名单路径，
    # 两处是同一个决定的两半。
    $headOut = Join-Path $dir "head"
    New-Item -ItemType Directory -Force -Path (Join-Path $headOut "css"), (Join-Path $headOut "js") | Out-Null
    Copy-Item (Join-Path $headSrc "index.html") $headOut -Force
    Copy-Item (Join-Path $headSrc "css\*.css") (Join-Path $headOut "css") -Force
    Copy-Item (Join-Path $headSrc "js\*.js") (Join-Path $headOut "js") -Force

    Copy-Item (Join-Path $root "run.cmd") $dir -Force
    Copy-Item (Join-Path $root "run.sh") $dir -Force
    Remove-Item (Join-Path $dir "*.pdb") -Force -ErrorAction SilentlyContinue

    # 判据：两个可执行文件与页面都在，少一个就直接失败（不许「发布成功但跑不起来」）。
    $coreExe = if ($rid -like "win-*") { Join-Path $dir "lilara.exe" } else { Join-Path $dir "lilara" }
    $relayExe = if ($rid -like "win-*") { Join-Path $dir "lilara-relay.exe" } else { Join-Path $dir "lilara-relay" }
    foreach ($required in @($coreExe, $relayExe, (Join-Path $headOut "index.html"), (Join-Path $headOut "js\app.js"), (Join-Path $dir "run.sh"))) {
        if (-not (Test-Path $required)) { throw "产物不全：缺 $required" }
    }

    $size = [math]::Round(((Get-Item $coreExe).Length + (Get-Item $relayExe).Length) / 1MB, 1)
    Write-Host ("  {0} + {1} — 两个可执行文件共 {2} MB" -f (Split-Path $coreExe -Leaf), (Split-Path $relayExe -Leaf), $size)
}

foreach ($rid in $Rids) {
    $dir = Join-Path $out "lilara-$Version-$rid"
    if ($rid -like "win-*") {
        $zip = Join-Path $out "lilara-$Version-$rid.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $dir "*") -DestinationPath $zip -CompressionLevel Optimal
        Write-Host ("  {0} — {1:N1} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
    } else {
        # ⚠️ 用 tar 而不是 Compress-Archive：后者在 Windows 上**存不住 unix 可执行位**，
        # 解到 Linux 上还得手动 chmod +x —— 而「解压就能跑」正是这个包的全部意义。
        # ⚠️ 用**相对路径 + --mode=755**：PATH 上的 tar 是 MSYS 的 GNU tar，它把 `E:\…` 里的冒号
        # 当成「远端主机」（`Cannot connect to E: resolve failed`）。
        $tgz = Join-Path $out "lilara-$Version-$rid.tar.gz"
        Remove-Item $tgz -Force -ErrorAction SilentlyContinue
        Push-Location $out
        try {
            tar --mode=755 -czf "lilara-$Version-$rid.tar.gz" "lilara-$Version-$rid"
            if ($LASTEXITCODE -ne 0) { throw "tar 失败：$rid" }
        }
        finally { Pop-Location }
        Write-Host ("  {0} — {1:N1} MB" -f (Split-Path $tgz -Leaf), ((Get-Item $tgz).Length / 1MB))
    }
}

# 校验和：发布包里两个可执行文件 + 整个压缩包，一行一个。
$sums = Join-Path $out "SHA256SUMS"
$lines = foreach ($file in (Get-ChildItem $out -File | Where-Object { $_.Extension -in ".zip", ".gz" } | Sort-Object Name)) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
$lines | Set-Content -Path $sums -Encoding ascii
Write-Host ("SHA256SUMS — {0} 行" -f @($lines).Count)
Write-Host "产物在 $out"
