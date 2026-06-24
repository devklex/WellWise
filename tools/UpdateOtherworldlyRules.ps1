[CmdletBinding()]
param(
    [string]$RepoeDataPath,
    [string]$SelfPoeDataPath,
    [string]$WellDataPath,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DefaultExRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot "..\..\.."))

if ([string]::IsNullOrWhiteSpace($RepoeDataPath)) {
    $RepoeDataPath = Join-Path $DefaultExRoot "Data\repoe_data"
}

if ([string]::IsNullOrWhiteSpace($SelfPoeDataPath)) {
    $decodedSelfPoePath = Join-Path $DefaultExRoot "Data\selfpoe_data_decoded"
    if (Test-Path -LiteralPath (Join-Path $decodedSelfPoePath "balance\mods.json")) {
        $SelfPoeDataPath = $decodedSelfPoePath
    }
    else {
        $SelfPoeDataPath = Join-Path $DefaultExRoot "Data\selfpoe_data\data"
    }
}

if ([string]::IsNullOrWhiteSpace($WellDataPath)) {
    $WellDataPath = Join-Path $ProjectRoot "data\well_of_souls_tiers.json"
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing JSON file: $Path"
    }

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) {
        $text = $text.Substring(1)
    }

    return ($text | ConvertFrom-Json)
}

function Get-KeyIndex {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $match = [regex]::Match([string]$Value, '^Key\((\d+)\)$')
    if (-not $match.Success) {
        return $null
    }

    $raw = [uint64]$match.Groups[1].Value
    if ($raw -eq [uint64]::MaxValue -or $raw -gt [uint64][int]::MaxValue) {
        return $null
    }

    return [int]$raw
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-StatId {
    param(
        $Stats,
        $Key
    )

    $index = Get-KeyIndex $Key
    if ($null -eq $index) {
        return $null
    }

    if ($index -lt 0 -or $index -ge $Stats.Count) {
        return $null
    }

    return $Stats[$index].Id
}

function Get-DecodedSelfStats {
    param(
        [Parameter(Mandatory = $true)]$Mod,
        [Parameter(Mandatory = $true)]$Stats,
        [switch]$IncludeZero
    )

    $result = @()
    for ($i = 1; $i -le 6; $i++) {
        $statKey = Get-PropertyValue $Mod "StatsKey$i"
        if ($null -eq $statKey) {
            $statKey = Get-PropertyValue $Mod "Stat$i"
        }

        $statId = Get-StatId $Stats $statKey
        if ([string]::IsNullOrWhiteSpace($statId)) {
            continue
        }

        $value = Get-PropertyValue $Mod "Stat${i}Value"
        if ($null -ne $value -and $null -ne $value.PSObject.Properties["min"] -and $null -ne $value.PSObject.Properties["max"]) {
            $min = $value.min
            $max = $value.max
        }
        else {
            $min = Get-PropertyValue $Mod "Stat${i}Min"
            $max = Get-PropertyValue $Mod "Stat${i}Max"
        }

        if ($null -eq $min -or $null -eq $max) {
            continue
        }

        if ($min -eq -33554432 -or $max -eq -33554432) {
            continue
        }

        if (-not $IncludeZero -and $min -eq 0 -and $max -eq 0) {
            continue
        }

        $result += [pscustomobject]@{
            id = [string]$statId
            min = [double]$min
            max = [double]$max
        }
    }

    return @($result)
}

function Get-RepoVisibleStats {
    param([Parameter(Mandatory = $true)]$Mod)

    $result = @()
    foreach ($stat in @($Mod.stats)) {
        if ($null -eq $stat) {
            continue
        }

        if ($stat.min -eq 0 -and $stat.max -eq 0) {
            continue
        }

        $result += [pscustomobject]@{
            id = [string]$stat.id
            min = [double]$stat.min
            max = [double]$stat.max
        }
    }

    return @($result)
}

function Get-StatSignature {
    param($Stats)

    $parts = @()
    foreach ($stat in @($Stats)) {
        if ($null -eq $stat -or $null -eq $stat.PSObject.Properties["id"]) {
            continue
        }

        $parts += "{0}:{1}:{2}" -f $stat.id, ([double]$stat.min).ToString("0.####", [Globalization.CultureInfo]::InvariantCulture), ([double]$stat.max).ToString("0.####", [Globalization.CultureInfo]::InvariantCulture)
    }

    return $parts -join "|"
}

function Get-TagName {
    param(
        [Parameter(Mandatory = $true)]$Tags,
        [Parameter(Mandatory = $true)]$Index
    )

    if ($Index -lt 0 -or $Index -ge $Tags.Count) {
        return "#$Index"
    }

    return [string]$Tags[$Index].Id
}

function Get-DecodedSelfSpawnTags {
    param(
        [Parameter(Mandatory = $true)]$Mod,
        [Parameter(Mandatory = $true)]$Tags
    )

    $tagKeys = Get-PropertyValue $Mod "SpawnWeight_TagsKeys"
    if ($null -eq $tagKeys) {
        $tagKeys = Get-PropertyValue $Mod "SpawnWeight_Tags"
    }

    return @($tagKeys | ForEach-Object { Get-TagName $Tags $_ })
}

function Get-TagSignature {
    param($Tags)

    return (@($Tags) | Sort-Object) -join "|"
}

function Clean-DisplayText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    $clean = [regex]::Replace($Text, '\[([^\]|]+)\|([^\]]+)\]', '$2')
    $clean = [regex]::Replace($clean, '\[([^\]]+)\]', '$1')
    $clean = [regex]::Replace($clean, '\s+', ' ')
    return $clean.Trim()
}

function Get-ImportantWords {
    param([string]$Text)

    $stopWords = @(
        "and", "the", "to", "of", "per", "for", "you", "your",
        "with", "from", "increased", "reduced"
    )

    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([StringComparer]::OrdinalIgnoreCase)
    $words = @()
    foreach ($match in [regex]::Matches($Text, '[A-Za-z]+')) {
        $word = $match.Value
        if ($word.Length -le 2 -or $stopWords -contains $word.ToLowerInvariant()) {
            continue
        }

        if ($seen.Add($word)) {
            $words += $word
        }
    }

    return @($words)
}

function Get-VisibleValueRanges {
    param([string]$Text)

    $result = @()
    $pattern = '(?<prefix>[+-]?)\s*(?:\((?<rangeMin>[+-]?\d+(?:\.\d+)?)[\-–](?<rangeMax>[+-]?\d+(?:\.\d+)?)\)|(?<single>[+-]?\d+(?:\.\d+)?))\s*(?<suffix>%)?'
    foreach ($match in [regex]::Matches($Text, $pattern)) {
        $prefix = $match.Groups["prefix"].Value
        $suffix = if ($match.Groups["suffix"].Success) { "%" } else { "" }

        if ($match.Groups["single"].Success) {
            $raw = $match.Groups["single"].Value
            if ([string]::IsNullOrWhiteSpace($prefix)) {
                if ($raw.StartsWith("+")) {
                    $prefix = "+"
                }
                elseif ($raw.StartsWith("-")) {
                    $prefix = "-"
                }
            }

            $value = [math]::Abs([double]::Parse($raw, [Globalization.CultureInfo]::InvariantCulture))
            $min = $value
            $max = $value
        }
        else {
            $left = [math]::Abs([double]::Parse($match.Groups["rangeMin"].Value, [Globalization.CultureInfo]::InvariantCulture))
            $right = [math]::Abs([double]::Parse($match.Groups["rangeMax"].Value, [Globalization.CultureInfo]::InvariantCulture))
            $min = [math]::Min($left, $right)
            $max = [math]::Max($left, $right)
        }

        $result += [pscustomobject][ordered]@{
            min = $min
            max = $max
            prefix = $prefix
            suffix = $suffix
        }
    }

    return @($result)
}

function Get-AccessoryClasses {
    param([Parameter(Mandatory = $true)]$RepoMod)

    $tagMap = @{
        amulet = "Amulet"
        ring = "Ring"
        belt = "Belt"
    }

    $allTags = @("amulet", "ring", "belt")
    $weights = @($RepoMod.spawn_weights)
    $included = @($weights | Where-Object { $tagMap.ContainsKey([string]$_.tag) -and [int]$_.weight -gt 0 } | ForEach-Object { [string]$_.tag })
    $excluded = @($weights | Where-Object { $tagMap.ContainsKey([string]$_.tag) -and [int]$_.weight -eq 0 } | ForEach-Object { [string]$_.tag })

    if ($included.Count -eq 0) {
        $included = @($allTags | Where-Object { $excluded -notcontains $_ })
    }

    $classes = @($included | ForEach-Object { $tagMap[$_] })
    if ($classes.Count -eq 0) {
        throw "Could not infer accessory class for $($RepoMod.name)"
    }

    return @($classes)
}

function New-WellRule {
    param(
        [Parameter(Mandatory = $true)][string]$ModId,
        [Parameter(Mandatory = $true)]$RepoMod
    )

    $label = Clean-DisplayText $RepoMod.text
    if ([string]::IsNullOrWhiteSpace($label)) {
        throw "Breach mod $ModId has no display text."
    }

    $values = @(Get-VisibleValueRanges $label)
    $tiers = @()
    $rulePrefix = ""
    $ruleSuffix = ""
    if ($values.Count -gt 0) {
        $rulePrefix = [string]$values[0].prefix
        $ruleSuffix = [string]$values[0].suffix
        $tiers += [ordered]@{
            tier = 1
            itemLevel = [int]$RepoMod.required_level
            min = [double]$values[0].min
            max = [double]$values[0].max
            values = @($values)
        }
    }

    return [ordered]@{
        id = "repoe_desecrated_$ModId"
        label = $label
        itemClassContains = @(Get-AccessoryClasses $RepoMod)
        applicabilityStatus = "resolved"
        applicabilitySource = "breach_desecration_spawn_tags"
        textContainsAll = @(Get-ImportantWords $label)
        generation = [string]$RepoMod.generation_type
        prefix = $rulePrefix
        suffix = $ruleSuffix
        desecratedOnly = $true
        tiers = @($tiers)
    }
}

function Set-OrAddNote {
    param(
        [Parameter(Mandatory = $true)]$Database,
        [Parameter(Mandatory = $true)][string]$Note
    )

    $notes = @($Database.notes)
    if ($notes -notcontains $Note) {
        $Database.notes = @($notes + $Note)
    }
}

function Set-ValidationProperty {
    param(
        [Parameter(Mandatory = $true)]$Validation,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )

    $property = $Validation.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Validation | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

Write-Host "Loading repoe data from $RepoeDataPath"
Write-Host "Loading selfpoe data from $SelfPoeDataPath"

$repoModsPath = Join-Path $RepoeDataPath "mods.json"
$selfModsPath = Join-Path $SelfPoeDataPath "balance\mods.json"
$selfTagsPath = Join-Path $SelfPoeDataPath "balance\tags.json"
$selfStatsPath = Join-Path $SelfPoeDataPath "balance\stats.json"

$repoMods = Read-JsonFile $repoModsPath
$selfMods = @(Read-JsonFile $selfModsPath)
$selfTags = @(Read-JsonFile $selfTagsPath)
$selfStats = @(Read-JsonFile $selfStatsPath)
$database = Read-JsonFile $WellDataPath

$selfById = @{}
foreach ($mod in $selfMods) {
    $selfById[[string]$mod.Id] = $mod
}

$repoProperties = @($repoMods.PSObject.Properties)
$breachProperties = @($repoProperties | Where-Object {
    $hasBreach = $false
    foreach ($weight in @($_.Value.spawn_weights)) {
        if ([string]$weight.tag -eq "breach_desecration") {
            $hasBreach = $true
            break
        }
    }
    $hasBreach
})

if ($breachProperties.Count -eq 0) {
    throw "No breach_desecration records found in repoe mods.json."
}

$breachMismatches = @()
foreach ($property in $breachProperties) {
    $id = [string]$property.Name
    $repoMod = $property.Value
    if (-not $selfById.ContainsKey($id)) {
        $breachMismatches += "$id missing from selfpoe_data"
        continue
    }

    $selfMod = $selfById[$id]
    $selfStatsSig = Get-StatSignature (Get-DecodedSelfStats $selfMod $selfStats)
    $repoStatsSig = Get-StatSignature (Get-RepoVisibleStats $repoMod)
    $selfTagSig = Get-TagSignature (Get-DecodedSelfSpawnTags $selfMod $selfTags)
    $repoTagSig = Get-TagSignature (@($repoMod.spawn_weights) | ForEach-Object { [string]$_.tag })
    $expectedGeneration = if ([string]$repoMod.generation_type -eq "prefix") { 1 } elseif ([string]$repoMod.generation_type -eq "suffix") { 2 } else { 0 }

    if ($selfStatsSig -ne $repoStatsSig) {
        $breachMismatches += "$id stat mismatch self=[$selfStatsSig] repoe=[$repoStatsSig]"
    }
    if ($selfTagSig -ne $repoTagSig) {
        $breachMismatches += "$id tag mismatch self=[$selfTagSig] repoe=[$repoTagSig]"
    }
    if ([int]$selfMod.Domain -ne 28) {
        $breachMismatches += "$id expected self domain 28, got $($selfMod.Domain)"
    }
    if ($expectedGeneration -ne 0 -and [int]$selfMod.GenerationType -ne $expectedGeneration) {
        $breachMismatches += "$id generation mismatch self=$($selfMod.GenerationType) repoe=$($repoMod.generation_type)"
    }
    if ([int]$selfMod.Level -ne [int]$repoMod.required_level) {
        $breachMismatches += "$id level mismatch self=$($selfMod.Level) repoe=$($repoMod.required_level)"
    }
}

if ($breachMismatches.Count -gt 0) {
    throw "Breach validation failed:`n$($breachMismatches -join "`n")"
}

$broadVisibleMismatches = @()
foreach ($property in $repoProperties) {
    $id = [string]$property.Name
    $repoMod = $property.Value
    if (-not $selfById.ContainsKey($id)) {
        continue
    }

    if (@("item", "desecrated") -notcontains [string]$repoMod.domain) {
        continue
    }

    if (@("prefix", "suffix") -notcontains [string]$repoMod.generation_type) {
        continue
    }

    $selfSig = Get-StatSignature (Get-DecodedSelfStats $selfById[$id] $selfStats)
    $repoSig = Get-StatSignature (Get-RepoVisibleStats $repoMod)
    if ($selfSig -ne $repoSig) {
        $broadVisibleMismatches += [pscustomobject]@{
            id = $id
            self = $selfSig
            repoe = $repoSig
        }
    }
}

$generatedRules = @()
foreach ($property in $breachProperties | Sort-Object Name) {
    $generatedRules += New-WellRule $property.Name $property.Value
}

$existingRules = @($database.rules)
$generatedIds = @($generatedRules | ForEach-Object { [string]$_.id })
$keptRules = @($existingRules | Where-Object { $generatedIds -notcontains [string]$_.id })
$database.rules = @($keptRules + $generatedRules)

$database.source = "local repoe PoE2 data rip with selfpoe-validated breach_desecration rows"
Set-OrAddNote $database "Breach/Otherworldly GenesisTree accessory rules are validated against selfpoe_data breach_desecration rows before insertion."
Set-OrAddNote $database "Raw selfpoe_data is not used as a full replacement source because broad validation can reveal display-normalization mismatches."
Set-ValidationProperty $database.validation "totalRules" @($database.rules).Count
Set-ValidationProperty $database.validation "breachOtherworldlyRules" $generatedRules.Count
Set-ValidationProperty $database.validation "selfPoeBreachRowsValidated" $breachProperties.Count
Set-ValidationProperty $database.validation "selfPoeBroadVisibleMismatches" $broadVisibleMismatches.Count

Write-Host "Validated $($breachProperties.Count) breach_desecration rows against selfpoe_data."
Write-Host "Generated $($generatedRules.Count) Breach/Otherworldly WellWise rules."
Write-Host "Broad item/desecrated self-vs-repoe visible mismatches: $($broadVisibleMismatches.Count). Full selfpoe replacement remains disabled."
if ($broadVisibleMismatches.Count -gt 0) {
    Write-Host "First broad mismatch details:"
    foreach ($mismatch in @($broadVisibleMismatches | Select-Object -First 10)) {
        Write-Host ("  {0}: self=[{1}] repoe=[{2}]" -f $mismatch.id, $mismatch.self, $mismatch.repoe)
    }
}

if ($CheckOnly) {
    Write-Host "CheckOnly set; not writing $WellDataPath"
    return
}

$json = ($database | ConvertTo-Json -Depth 100).Replace('\u0027', "'")
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[IO.File]::WriteAllText($WellDataPath, $json + [Environment]::NewLine, $utf8Bom)
Write-Host "Updated $WellDataPath"
