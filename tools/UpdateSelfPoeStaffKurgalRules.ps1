[CmdletBinding()]
param(
    [string]$SelfPoeDecodedPath = "C:\Users\Klexen\Documents\ex\Data\selfpoe_data_decoded",
    [string]$InputPath = "data\well_of_souls_tiers.json",
    [string]$OutputPath,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AllowedModId = "AbyssModStaffKurgalSuffixPuppetMasterStacks"
$TargetRuleId = "repoe_desecrated_AbyssModStaffKurgalSuffixPuppetMasterStacks"
$MinimumInputRules = 1393
$ExpectedStatId = "max_puppet_master_stacks_+"
$ExpectedModTypeName = "MaximumPuppeteerStacks"
$ExpectedSpawnWeights = @{
    staff = 1
    default = 0
    kurgal_mod = 1
}
$ExpectedImplicitTags = @("unveiled_mod", "kurgal_mod", "minion")

function Resolve-ProjectPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $ProjectRoot $Path))
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

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) {
        throw "$Context is missing required property '$Name'."
    }

    return $value
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Actual -ne $Expected) {
        throw "$Name mismatch. Expected '$Expected', got '$Actual'."
    }
}

function Assert-ExactStringSet {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $actualSorted = @($Actual | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $actualSignature = $actualSorted -join "|"
    $expectedSignature = $expectedSorted -join "|"

    if ($actualSignature -ne $expectedSignature) {
        throw "$Name mismatch. Expected '$expectedSignature', got '$actualSignature'."
    }
}

function Get-ItemCount {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [System.Array]) {
        return $Value.Length
    }

    if ($Value -is [System.Collections.ICollection]) {
        return $Value.Count
    }

    return 1
}

function Get-KeyIndex {
    param(
        $Value,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($null -eq $Value) {
        throw "$Context is missing a key value."
    }

    if ($Value -is [int] -or $Value -is [long]) {
        return [int]$Value
    }

    $text = [string]$Value
    $match = [regex]::Match($text, '^Key\((\d+)\)$')
    if ($match.Success) {
        $raw = [uint64]$match.Groups[1].Value
        if ($raw -eq [uint64]::MaxValue -or $raw -gt [uint64][int]::MaxValue) {
            throw "$Context points at an empty or out-of-range key: $text"
        }

        return [int]$raw
    }

    $parsed = 0
    if ([int]::TryParse($text, [ref]$parsed)) {
        return $parsed
    }

    throw "$Context is not a numeric key reference: $text"
}

function Get-IndexedMappingRecord {
    param(
        $Mapping,
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($null -eq $Mapping) {
        throw "$Context mapping was not loaded."
    }

    if ($Mapping -is [System.Collections.IList]) {
        if ($Index -lt 0 -or $Index -ge $Mapping.Count) {
            throw "$Context index $Index is out of range."
        }

        return $Mapping[$Index]
    }

    $property = $Mapping.PSObject.Properties[[string]$Index]
    if ($null -eq $property) {
        $property = $Mapping.PSObject.Properties["Key($Index)"]
    }

    if ($null -eq $property) {
        throw "$Context index $Index was not found."
    }

    return $property.Value
}

function Resolve-StatId {
    param(
        $Stats,
        $Key
    )

    $index = Get-KeyIndex $Key "stat key"
    $record = Get-IndexedMappingRecord $Stats $index "stats"
    return [string](Get-RequiredPropertyValue $record "Id" "stats[$index]")
}

function Resolve-ModTypeName {
    param(
        $ModTypes,
        $Key
    )

    $index = Get-KeyIndex $Key "mod type key"
    $record = Get-IndexedMappingRecord $ModTypes $index "modtype"
    return [string](Get-RequiredPropertyValue $record "Name" "modtype[$index]")
}

function Resolve-TagId {
    param(
        $Tags,
        $Key
    )

    $index = Get-KeyIndex $Key "tag key"
    $record = Get-IndexedMappingRecord $Tags $index "tags"
    return [string](Get-RequiredPropertyValue $record "Id" "tags[$index]")
}

function Find-ModById {
    param(
        [Parameter(Mandatory = $true)]$Mods,
        [Parameter(Mandatory = $true)][string]$ModId
    )

    $property = $Mods.PSObject.Properties[$ModId]
    if ($null -ne $property) {
        return $property.Value
    }

    foreach ($mod in @($Mods)) {
        $id = Get-PropertyValue $mod "Id"
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = Get-PropertyValue $mod "id"
        }

        if ([string]$id -eq $ModId) {
            return $mod
        }
    }

    return $null
}

function Get-ResolvedTagIds {
    param(
        [Parameter(Mandatory = $true)]$Items,
        $Tags,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $ids = @()
    foreach ($item in @($Items)) {
        if ($null -eq $item) {
            throw "$Context contains a null tag entry."
        }

        if ($item -is [int] -or $item -is [long]) {
            $ids += Resolve-TagId $Tags $item
            continue
        }

        if ($item -is [string]) {
            if ($item -match '^Key\(\d+\)$' -or $item -match '^\d+$') {
                $ids += Resolve-TagId $Tags $item
            }
            else {
                $ids += $item
            }
            continue
        }

        $id = Get-PropertyValue $item "id"
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = Get-PropertyValue $item "Id"
        }

        if ([string]::IsNullOrWhiteSpace($id)) {
            throw "$Context tag entry has no id."
        }

        $ids += [string]$id
    }

    return @($ids)
}

function Assert-SpawnWeights {
    param(
        [Parameter(Mandatory = $true)]$Mod,
        $Tags
    )

    $actual = @{}
    $spawnWeights = Get-PropertyValue $Mod "SpawnWeights"

    if ($null -ne $spawnWeights) {
        foreach ($weight in @($spawnWeights)) {
            $id = [string](Get-RequiredPropertyValue $weight "id" "$AllowedModId spawn weight")
            $value = [int](Get-RequiredPropertyValue $weight "weight" "$AllowedModId spawn weight '$id'")

            if ($actual.ContainsKey($id)) {
                throw "$AllowedModId has duplicate spawn weight tag '$id'."
            }

            $actual[$id] = $value
        }
    }
    else {
        $spawnWeightTags = @(Get-RequiredPropertyValue $Mod "SpawnWeight_Tags" $AllowedModId)
        $spawnWeightValues = @(Get-RequiredPropertyValue $Mod "SpawnWeight_Values" $AllowedModId)

        if ($spawnWeightTags.Count -ne $spawnWeightValues.Count) {
            throw "$AllowedModId spawn weight tag/value counts differ."
        }

        for ($i = 0; $i -lt $spawnWeightTags.Count; $i++) {
            $id = Resolve-TagId $Tags $spawnWeightTags[$i]
            $value = [int]$spawnWeightValues[$i]

            if ($actual.ContainsKey($id)) {
                throw "$AllowedModId has duplicate spawn weight tag '$id'."
            }

            $actual[$id] = $value
        }
    }

    Assert-ExactStringSet $actual.Keys $ExpectedSpawnWeights.Keys "$AllowedModId spawn weight tags"

    foreach ($key in $ExpectedSpawnWeights.Keys) {
        Assert-Equal $actual[$key] $ExpectedSpawnWeights[$key] "$AllowedModId spawn weight '$key'"
    }
}

function Assert-SelfPoeCandidate {
    param(
        [Parameter(Mandatory = $true)]$Mod,
        $Stats,
        $ModTypes,
        $Tags
    )

    Assert-Equal ([string](Get-RequiredPropertyValue $Mod "Id" $AllowedModId)) $AllowedModId "$AllowedModId Id"

    $statId = Get-PropertyValue $Mod "Stat1Id"
    if ([string]::IsNullOrWhiteSpace($statId)) {
        $statId = Resolve-StatId $Stats (Get-RequiredPropertyValue $Mod "Stat1" $AllowedModId)
    }
    Assert-Equal ([string]$statId) $ExpectedStatId "$AllowedModId Stat1"

    $modTypeName = Get-PropertyValue $Mod "ModTypeName"
    if ([string]::IsNullOrWhiteSpace($modTypeName)) {
        $modTypeName = Resolve-ModTypeName $ModTypes (Get-RequiredPropertyValue $Mod "ModType" $AllowedModId)
    }
    Assert-Equal ([string]$modTypeName) $ExpectedModTypeName "$AllowedModId ModType"

    Assert-Equal ([int](Get-RequiredPropertyValue $Mod "Domain" $AllowedModId)) 28 "$AllowedModId Domain"

    $generationType = Get-PropertyValue $Mod "GenerationType"
    $generation = Get-PropertyValue $Mod "Generation"
    if ($null -ne $generationType) {
        Assert-Equal ([int]$generationType) 2 "$AllowedModId GenerationType"
    }
    elseif ([string]::IsNullOrWhiteSpace($generation)) {
        throw "$AllowedModId is missing GenerationType or Generation."
    }

    if (-not [string]::IsNullOrWhiteSpace($generation)) {
        Assert-Equal ([string]$generation) "suffix" "$AllowedModId Generation"
    }

    Assert-Equal ([int](Get-RequiredPropertyValue $Mod "Level" $AllowedModId)) 65 "$AllowedModId Level"

    $stat1Value = Get-RequiredPropertyValue $Mod "Stat1Value" $AllowedModId
    Assert-Equal ([int](Get-RequiredPropertyValue $stat1Value "min" "$AllowedModId Stat1Value")) 3 "$AllowedModId Stat1Value.min"
    Assert-Equal ([int](Get-RequiredPropertyValue $stat1Value "max" "$AllowedModId Stat1Value")) 4 "$AllowedModId Stat1Value.max"

    Assert-SpawnWeights $Mod $Tags

    $implicitTags = Get-ResolvedTagIds (Get-RequiredPropertyValue $Mod "ImplicitTags" $AllowedModId) $Tags "$AllowedModId implicit tag"
    Assert-ExactStringSet $implicitTags $ExpectedImplicitTags "$AllowedModId implicit tags"

    $explicitTagsRaw = Get-PropertyValue $Mod "Tags"
    $explicitTagCount = Get-ItemCount $explicitTagsRaw
    if ($explicitTagCount -ne 0) {
        throw "$AllowedModId expected no explicit Tags, got $explicitTagCount."
    }
}

function New-PuppetMasterRule {
    return [ordered]@{
        id = $TargetRuleId
        label = "+(3-4) maximum stacks of Puppet Master"
        itemClassContains = @("Staff")
        applicabilityStatus = "resolved"
        applicabilitySource = "selfpoe_decoded_spawn_tags"
        textContainsAll = @("maximum", "stacks", "Puppet", "Master")
        generation = "suffix"
        prefix = "+"
        suffix = ""
        desecratedOnly = $true
        tiers = @(
            [ordered]@{
                tier = 1
                itemLevel = 65
                min = 3
                max = 4
                values = @(
                    [ordered]@{
                        min = 3
                        max = 4
                        prefix = "+"
                        suffix = ""
                    }
                )
            }
        )
    }
}

function Assert-NoDuplicateRuleIds {
    param([Parameter(Mandatory = $true)]$Rules)

    $duplicates = @(
        @($Rules | ForEach-Object { [string]$_.id }) |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $_.Name }
    )

    if ($duplicates.Count -gt 0) {
        throw "Duplicate rule ids would result: $($duplicates -join ', ')"
    }
}

function Set-ValidationProperty {
    param(
        [Parameter(Mandatory = $true)]$Validation,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Value
    )

    $property = $Validation.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Validation | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

if ($Apply -and -not [string]::IsNullOrWhiteSpace($OutputPath)) {
    throw "Use either -Apply or -OutputPath, not both."
}

$resolvedInputPath = Resolve-ProjectPath $InputPath
$resolvedSelfPoeDecodedPath = [IO.Path]::GetFullPath($SelfPoeDecodedPath)
$resolvedOutputPath = $null

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = Resolve-ProjectPath $OutputPath
    if ($resolvedOutputPath -eq $resolvedInputPath) {
        throw "-OutputPath must not be the same as -InputPath. Use -Apply to overwrite the input file."
    }
}

$modsPath = Join-Path $resolvedSelfPoeDecodedPath "balance\mods.json"
$statsPath = Join-Path $resolvedSelfPoeDecodedPath "balance\stats.json"
$modTypesPath = Join-Path $resolvedSelfPoeDecodedPath "balance\modtype.json"
$tagsPath = Join-Path $resolvedSelfPoeDecodedPath "balance\tags.json"

Write-Host "Loading selfpoe decoded mods from $modsPath"
Write-Host "Loading WellWise rules from $resolvedInputPath"

$mods = Read-JsonFile $modsPath
$stats = if (Test-Path -LiteralPath $statsPath) { Read-JsonFile $statsPath } else { $null }
$modTypes = if (Test-Path -LiteralPath $modTypesPath) { Read-JsonFile $modTypesPath } else { $null }
$tags = if (Test-Path -LiteralPath $tagsPath) { Read-JsonFile $tagsPath } else { $null }
$database = Read-JsonFile $resolvedInputPath
$candidate = Find-ModById $mods $AllowedModId

if ($null -eq $candidate) {
    throw "Could not find allowlisted mod id $AllowedModId in $modsPath."
}

Assert-SelfPoeCandidate $candidate $stats $modTypes $tags

$existingRules = @($database.rules)
$inputCount = $existingRules.Count

if ($inputCount -lt $MinimumInputRules) {
    throw "Input rule count $inputCount is less than required minimum $MinimumInputRules."
}

Assert-NoDuplicateRuleIds $existingRules

$existingIds = @($existingRules | ForEach-Object { [string]$_.id })
$existingTarget = @($existingRules | Where-Object { [string]$_.id -eq $TargetRuleId })

if ($existingTarget.Count -gt 1) {
    throw "Input contains duplicate target rule id $TargetRuleId."
}

$newRule = New-PuppetMasterRule
$outputRules = @()
$action = if ($existingTarget.Count -eq 0) { "add" } else { "replace" }

foreach ($rule in $existingRules) {
    if ([string]$rule.id -eq $TargetRuleId) {
        $outputRules += $newRule
    }
    else {
        $outputRules += $rule
    }
}

if ($existingTarget.Count -eq 0) {
    $outputRules += $newRule
}

$outputIds = @($outputRules | ForEach-Object { [string]$_.id })
$missingExistingIds = @($existingIds | Where-Object { $outputIds -notcontains $_ })
if ($missingExistingIds.Count -gt 0) {
    throw "Existing rule ids would disappear: $($missingExistingIds -join ', ')"
}

$addedIds = @($outputIds | Where-Object { $existingIds -notcontains $_ })
if ($addedIds.Count -gt 1 -or ($addedIds.Count -eq 1 -and $addedIds[0] -ne $TargetRuleId)) {
    throw "Unexpected rule ids would be added: $($addedIds -join ', ')"
}

Assert-NoDuplicateRuleIds $outputRules

$outputCount = $outputRules.Count
if ($outputCount -ne $inputCount -and $outputCount -ne ($inputCount + 1)) {
    throw "Output rule count $outputCount must be input count $inputCount or input count + 1."
}

$database.rules = @($outputRules)
if ($null -ne $database.PSObject.Properties["validation"] -and $null -ne $database.validation) {
    Set-ValidationProperty $database.validation "totalRules" $outputCount
}

Write-Host "Validated selfpoe candidate $AllowedModId."
Write-Host "Action: $action rule $TargetRuleId."
Write-Host "Input rules: $inputCount"
Write-Host "Output rules: $outputCount"

if (-not $Apply -and [string]::IsNullOrWhiteSpace($resolvedOutputPath)) {
    Write-Host "Dry run/check-only mode; no files written."
    return
}

$json = ($database | ConvertTo-Json -Depth 100).Replace('\u0027', "'")
$utf8Bom = New-Object System.Text.UTF8Encoding $true

if (-not [string]::IsNullOrWhiteSpace($resolvedOutputPath)) {
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory | Out-Null
    }

    [IO.File]::WriteAllText($resolvedOutputPath, $json + [Environment]::NewLine, $utf8Bom)
    Write-Host "Wrote candidate JSON to $resolvedOutputPath"
    return
}

[IO.File]::WriteAllText($resolvedInputPath, $json + [Environment]::NewLine, $utf8Bom)
Write-Host "Updated $resolvedInputPath"
