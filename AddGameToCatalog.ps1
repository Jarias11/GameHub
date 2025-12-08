param (
    [Parameter(Mandatory = $true)]
    [string]$GameKey,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName
)

$path = "GameContracts/GamesData.cs"

if (-not (Test-Path $path)) {
    throw "Could not find GameContracts/GamesData.cs. Run this from the src folder."
}

# Read all lines
$lines = Get-Content $path

# ===============================
# 1) ADD TO GameType ENUM
# ===============================

$enumStart = -1
$enumEnd   = -1

for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'public enum GameType') {
        $enumStart = $i
        continue
    }

    if ($enumStart -ge 0 -and $lines[$i].Trim() -eq "}") {
        $enumEnd = $i
        break
    }
}

if ($enumStart -lt 0 -or $enumEnd -lt 0) {
    throw "Could not locate GameType enum."
}

# Avoid duplicate enum values
for ($i = $enumStart; $i -lt $enumEnd; $i++) {
    if ($lines[$i] -match "^\s*$GameKey\s*,?\s*$") {
        throw "GameType.$GameKey already exists."
    }
}

# Insert new enum value as its own line before the closing brace.
$newEnumLine = "	$GameKey,"
$beforeEnum  = $lines[0..($enumEnd - 1)]
$afterEnum   = $lines[$enumEnd..($lines.Count - 1)]
$lines       = $beforeEnum + $newEnumLine + $afterEnum

$lines | Set-Content $path -Encoding UTF8

Write-Host "✔ Added GameType.$GameKey to enum in GamesData.cs"
Write-Host ""
Write-Host "Paste this into GameCatalog.All (inside the list, before the final '};'):"
Write-Host "────────────────────────────────────────────────────────────────────────────"
Write-Host @"
		new() {
			Type = GameType.$GameKey,
			Name = "$DisplayName",
			Category = GameCategory.Arcade,
			Emoji = "🎮",
			Tagline = "TODO",
			PlayersText = "1 Player offline",
			IsOnline = false
		},
"@
Write-Host "────────────────────────────────────────────────────────────────────────────"
