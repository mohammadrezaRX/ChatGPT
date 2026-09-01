$ErrorActionPreference = 'Stop'

$path = Join-Path $PWD 'rebuild/MpcUI.cs'
if (!(Test-Path $path)) {
    throw "rebuild/MpcUI.cs not found"
}

$text = Get-Content $path -Raw

$startMarker = '    internal static class MpcCharacterSlots'
$endMarker = '    internal static class MultiplayerUIStateManager'

$start = $text.IndexOf($startMarker, [System.StringComparison]::Ordinal)
if ($start -lt 0) {
    Write-Host 'MpcCharacterSlots wrapper not present; nothing to remove.'
    exit 0
}

$end = $text.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
if ($end -lt 0) {
    throw 'Could not find MultiplayerUIStateManager marker after MpcCharacterSlots.'
}

$text = $text.Remove($start, $end - $start)
Set-Content -Path $path -Value $text -Encoding UTF8

$count = ([regex]::Matches($text, '\bMpcCharacterSlots\b')).Count
Write-Host "Removed duplicate MpcCharacterSlots wrapper. Remaining references in MpcUI.cs: $count"

$core = Get-Content (Join-Path $PWD 'rebuild/MpcCore.cs') -Raw
if ($core -notmatch '\binternal static class MpcCharacterSlots\b') {
    throw 'Shared MpcCharacterSlots implementation is missing from MpcCore.cs.'
}
