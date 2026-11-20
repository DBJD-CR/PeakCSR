# PeakCSR Ladder Admin CLI (PowerShell)
#
# NOTE: This file is intentionally ASCII-only so Windows PowerShell 5.1
#       parses it correctly regardless of file encoding.
#
# Setup:
#   $env:PCSR_ADMIN_TOKEN = '<your ADMIN_TOKEN>'
#
# Usage (run from repo root):
#   .\tools\pcsr-admin.ps1 -Action recent
#   .\tools\pcsr-admin.ps1 -Action void -MatchKey "ABC123|123456789" -Note "opponent拔线"
#   .\tools\pcsr-admin.ps1 -Action void -InviteCode ABC123 -Seed 123456789
#   .\tools\pcsr-admin.ps1 -Action set-elo -SteamId 7656119xxxxxxxxxx -Elo 1500
#   .\tools\pcsr-admin.ps1 -Action reset
#   .\tools\pcsr-admin.ps1 -Action wipe

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("recent", "void", "set-elo", "reset", "wipe")]
    [string]$Action,

    # Worker root URL, e.g. https://peakcsr-ladder.xxx.workers.dev
    [string]$BaseUrl = "https://peakcsr-ladder.3405689090.workers.dev",

    # Admin token; falls back to env var PCSR_ADMIN_TOKEN
    [string]$Token = $env:PCSR_ADMIN_TOKEN,

    # void: give match_key directly (inviteCode|seed)
    [string]$MatchKey,

    # void: or give room code + seed
    [string]$InviteCode,
    [long]$Seed = 0,

    # set-elo
    [string]$SteamId,
    [int]$Elo = 0,

    # recent
    [int]$Limit = 20,

    # audit note
    [string]$Note = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "Missing ADMIN_TOKEN." -ForegroundColor Red
    Write-Host "Set it first:  `$env:PCSR_ADMIN_TOKEN = '<your token>'" -ForegroundColor Yellow
    Write-Host "Or pass -Token '<your token>' on the command line." -ForegroundColor Yellow
    exit 1
}

$BaseUrl = $BaseUrl.TrimEnd("/")
$uri = "$BaseUrl/api/admin"

$payload = @{ action = $Action; note = $Note }

switch ($Action) {
    "recent" {
        $payload.limit = $Limit
    }
    "void" {
        if (-not [string]::IsNullOrWhiteSpace($MatchKey)) {
            $payload.matchKey = $MatchKey.Trim()
        }
        elseif (-not [string]::IsNullOrWhiteSpace($InviteCode)) {
            $payload.inviteCode = $InviteCode.Trim().ToUpperInvariant()
            $payload.seed = $Seed
        }
        else {
            Write-Host "void requires -MatchKey, or -InviteCode plus -Seed" -ForegroundColor Red
            exit 1
        }
    }
    "set-elo" {
        if ([string]::IsNullOrWhiteSpace($SteamId) -or $Elo -le 0) {
            Write-Host "set-elo requires -SteamId and -Elo" -ForegroundColor Red
            exit 1
        }
        $payload.steamId = $SteamId.Trim()
        $payload.elo = $Elo
    }
    "reset" { }
    "wipe" {
        Write-Host "WARNING: wipe deletes ALL players and match records." -ForegroundColor Red
        $answer = Read-Host "Type WIPE to confirm"
        if ($answer -ne "WIPE") {
            Write-Host "Cancelled." -ForegroundColor Yellow
            exit 0
        }
    }
}

$body = $payload | ConvertTo-Json -Compress
Write-Host "POST $uri" -ForegroundColor DarkGray
Write-Host "body: $body" -ForegroundColor DarkGray

try {
    $resp = Invoke-RestMethod -Uri $uri -Method Post `
        -ContentType "application/json; charset=utf-8" `
        -Headers @{ "X-Admin-Token" = $Token } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -TimeoutSec 20
}
catch {
    Write-Host ("Request failed: " + $_.Exception.Message) -ForegroundColor Red
    try {
        $stream = $_.Exception.Response.GetResponseStream()
        if ($stream) {
            $reader = New-Object System.IO.StreamReader($stream)
            $detail = $reader.ReadToEnd()
            if ($detail) { Write-Host ("Server said: " + $detail) -ForegroundColor Red }
        }
    }
    catch { }
    exit 1
}

if ($Action -eq "recent" -and $resp.matches) {
    $epoch = [datetime]"1970-01-01T00:00:00Z"
    $modeNames = @("FullPeak", "Hardcore", "Sprint")

    Write-Host ""
    Write-Host ("Recent " + $resp.count + " settled matches:") -ForegroundColor Cyan
    $resp.matches | ForEach-Object {
        [pscustomobject]@{
            Time      = $epoch.AddMilliseconds($_.settledAt).ToLocalTime().ToString("MM-dd HH:mm:ss")
            MatchKey  = $_.matchKey
            Winner    = $_.winner
            Loser     = $_.loser
            WinDelta  = $_.winnerDelta
            LoseDelta = $_.loserDelta
            Mode      = $modeNames[$_.format]
            Verify    = $_.verifyMethod
            IsVoid    = $(if ($_.isVoid) { "YES" } else { "" })
        }
    } | Format-Table -AutoSize
}
else {
    $resp | ConvertTo-Json -Depth 6
}
