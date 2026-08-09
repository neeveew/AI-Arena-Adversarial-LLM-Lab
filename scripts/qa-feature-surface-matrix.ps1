$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:AIArenaQaFeatureSurfaceKeys = @(
    'matrix',
    'fork',
    'packs',
    'rubrics',
    'claims',
    'context-prompt-inspector',
    'agent-memory-debugger',
    'fault-injection',
    'routing-optimizer',
    'in-app-qa-inspector'
)
$script:AIArenaQaFeatureSurfaceIdentities = [ordered]@{
    'matrix' = 'MatrixPanel'
    'fork' = 'ForkPanel'
    'packs' = 'PacksPanel'
    'rubrics' = 'RubricPanel'
    'claims' = 'ClaimPanel'
    'context-prompt-inspector' = 'PromptFeatureRoot'
    'agent-memory-debugger' = 'MemoryFeatureRoot'
    'fault-injection' = 'FaultLabRoot'
    'routing-optimizer' = 'RoutingOptimizerRoot'
    'in-app-qa-inspector' = 'QaInspectorRoot'
}
$script:AIArenaQaMigrationEvidenceIds = [ordered]@{
    'ai_arena.scenario_pack.v1' = 'evidence.schema.migration.ai-arena.scenario-pack.v1'
    'ai_arena.benchmark_pack.v1' = 'evidence.schema.migration.ai-arena.benchmark-pack.v1'
}

function Get-AIArenaQaMigrationEvidenceId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('ai_arena.scenario_pack.v1', 'ai_arena.benchmark_pack.v1')]
        [string]$Schema
    )

    return [string]$script:AIArenaQaMigrationEvidenceIds[$Schema]
}

function Get-AIArenaQaFeatureSurfaceKeys {
    [CmdletBinding()]
    param()

    return @($script:AIArenaQaFeatureSurfaceKeys)
}

function Get-AIArenaQaFeatureSurfacePassCells {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Cells,

        [Parameter(Mandatory)]
        [ValidateRange(1, 5)]
        [int]$PassNumber
    )

    $passPrefix = "p$($PassNumber.ToString('D2')).feature."
    return @($Cells | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties['key'] -and
        ([string]$_.key).StartsWith($passPrefix, [StringComparison]::Ordinal)
    })
}

function Get-AIArenaQaFeatureSurfaceIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet(
            'matrix',
            'fork',
            'packs',
            'rubrics',
            'claims',
            'context-prompt-inspector',
            'agent-memory-debugger',
            'fault-injection',
            'routing-optimizer',
            'in-app-qa-inspector')]
        [string]$FeatureKey
    )

    return [string]$script:AIArenaQaFeatureSurfaceIdentities[$FeatureKey]
}

function Get-AIArenaQaFeatureSelectionTimeoutMilliseconds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet(
            'matrix',
            'fork',
            'packs',
            'rubrics',
            'claims',
            'context-prompt-inspector',
            'agent-memory-debugger',
            'fault-injection',
            'routing-optimizer',
            'in-app-qa-inspector')]
        [string]$FeatureKey
    )

    # The Inspector performs a fresh isolated restore/build/currentness validation
    # over the latest bounded evidence bundle. Historical successful cells run
    # close to 30 seconds, so retain the ordinary bound for every other feature
    # while giving this independently verified boundary deterministic headroom.
    if ($FeatureKey -eq 'in-app-qa-inspector') {
        return 90000
    }

    return 30000
}
