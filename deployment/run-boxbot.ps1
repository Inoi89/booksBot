$ErrorActionPreference = 'Stop'

$deadline = [DateTime]::UtcNow.AddSeconds(60)
do {
    $listener = Get-NetTCPConnection `
        -LocalAddress 127.0.0.1 `
        -LocalPort 10888 `
        -State Listen `
        -ErrorAction SilentlyContinue

    if ($listener) {
        break
    }

    Start-Sleep -Seconds 1
} while ([DateTime]::UtcNow -lt $deadline)

if (-not $listener) {
    throw 'BookBot egress SOCKS listener is unavailable'
}

$env:HTTPS_PROXY = 'socks5://127.0.0.1:10888'
$env:ALL_PROXY = 'socks5://127.0.0.1:10888'

Set-Location 'G:\tgbot-v2'
& 'C:\Program Files\dotnet\dotnet.exe' 'G:\tgbot-v2\booksBot.dll' *>> 'G:\tgbot-v2\boxbot.log'
exit $LASTEXITCODE
