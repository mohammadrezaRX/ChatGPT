$ErrorActionPreference = 'Stop'

$file = Join-Path $PSScriptRoot 'MultiplayerCampaignEnhancements.cs'
if (-not (Test-Path $file)) {
    throw "File not found: $file"
}

$s = Get-Content -Raw -Encoding UTF8 $file

# Remove duplicate CampaignSystem using directives while keeping the first one.
$campaignUsing = 'using TaleWorlds.CampaignSystem;'
$first = $s.IndexOf($campaignUsing)
if ($first -ge 0) {
    $head = $s.Substring(0, $first + $campaignUsing.Length)
    $tail = $s.Substring($first + $campaignUsing.Length)
    $tail = [regex]::Replace($tail, '(?m)^using\s+TaleWorlds\.CampaignSystem;\s*\r?\n', '', 1)
    $s = $head + $tail
}

# MobileParty lives in TaleWorlds.CampaignSystem.Party.
if ($s -notmatch '(?m)^using\s+TaleWorlds\.CampaignSystem\.Party;\s*$') {
    $marker = "using TaleWorlds.CampaignSystem;`r`n"
    if ($s.Contains($marker)) {
        $s = $s.Replace($marker, $marker + "using TaleWorlds.CampaignSystem.Party;`r`n", 1)
    } else {
        $s = "using TaleWorlds.CampaignSystem.Party;`r`n" + $s
    }
}

# Resolve TaleWorlds.Library/System.IO BinaryReader and BinaryWriter ambiguity.
if ($s -notmatch '(?m)^using\s+BinaryWriter\s*=\s*System\.IO\.BinaryWriter;\s*$') {
    $s = $s.Replace("using System.Text;`r`n", "using System.Text;`r`nusing BinaryWriter = System.IO.BinaryWriter;`r`nusing BinaryReader = System.IO.BinaryReader;`r`n", 1)
}

# Make the nested world state visible to the outer helper code.
$s = $s.Replace('private sealed class WorldPartyState', 'internal sealed class WorldPartyState')

# Replace the nonexistent type with the actual nested state type.
$s = $s.Replace('WorldPartySyncState', 'MpcRebuildRuntimeV2.WorldPartyState')

Set-Content -Path $file -Value $s -Encoding UTF8
Write-Host 'Rebuild compile fixes applied.' -ForegroundColor Green
