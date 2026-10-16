# แพ็ก Reverse X เป็น zip (self-contained win-x64 — เครื่องปลายทางไม่ต้องลง .NET) แบบเดียวกับ Anti X
#   powershell -ExecutionPolicy Bypass -File deploy\publish.ps1 [-Version 1.3.0]
# เซ็นแพ็กเกจให้ระบบอัปเดตในโปรแกรมรับได้ ด้วยกุญแจจาก $env:REVERSEX_UPDATE_SIGNING_KEY
# (ถ้าไม่ตั้งไว้ ใช้ไฟล์ %USERPROFILE%\.xman-signing\reversex-update-signing-key.txt) — ไม่มีกุญแจ = zip ใช้ได้ แต่อัปเดตอัตโนมัติไม่รับ
# ผลลัพธ์: publish\ReverseX-X.Y.Z-win-x64.zip (+ .sig ลายเซ็น ECDSA ของ zip ทั้งไฟล์ และ .sha256)
param([string]$Version, [switch]$RequireSignature)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not X.Y.Z" }
$publish = Join-Path $root 'publish'
$app = Join-Path $publish 'ReverseX'
$zip = Join-Path $publish "ReverseX-$Version-win-x64.zip"
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$zip*" -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app | Out-Null

# -p:Version ตั้งเลขรุ่นที่แอปใช้ตรวจอัปเดต (ต้องตรงกับ tag)
dotnet publish (Join-Path $root 'src\ReverseX.App') -c Release -r win-x64 --self-contained -o $app -p:DebugType=none "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $app

if (-not $env:REVERSEX_UPDATE_SIGNING_KEY) {
    $keyFile = Join-Path $env:USERPROFILE '.xman-signing\reversex-update-signing-key.txt'
    if (Test-Path $keyFile) { $env:REVERSEX_UPDATE_SIGNING_KEY = (Get-Content $keyFile -Raw).Trim() }
}
$signed = $false
if ($env:REVERSEX_UPDATE_SIGNING_KEY) {
    # ทุกไฟล์ในโฟลเดอร์ต้องอยู่ก่อนเซ็น — ไฟล์ที่เพิ่มทีหลังทำให้ตัวอัปเดตปฏิเสธทั้งแพ็กเกจ
    dotnet run --project (Join-Path $root 'tools\ReleaseSigner') -c Release -- $app $Version
    if ($LASTEXITCODE -ne 0) { throw 'signing failed' }
    $signed = $true
} elseif ($RequireSignature) {
    throw 'REVERSEX_UPDATE_SIGNING_KEY is not set'
} else {
    Write-Warning 'ไม่ได้เซ็นแพ็กเกจ (ไม่มี REVERSEX_UPDATE_SIGNING_KEY) — ใช้งานได้ แต่ระบบอัปเดตในโปรแกรมจะไม่รับ'
}

Compress-Archive -Path $app -DestinationPath $zip -Force
if ($signed) {
    dotnet run --project (Join-Path $root 'tools\ReleaseSigner') -c Release -- sign-file $zip
    if ($LASTEXITCODE -ne 0) { throw 'signing the zip failed' }
}
Write-Host ('OK: ' + $zip)
