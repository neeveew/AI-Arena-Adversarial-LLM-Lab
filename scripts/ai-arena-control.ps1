param()

$script:AIArenaPipeName = 'ai-arena-wpf-control'
$script:AIArenaTokenPath = Join-Path ([System.IO.Path]::GetTempPath()) ("ai-arena-wpf-control-{0}.token" -f [Environment]::UserName)

function Get-AIArenaControlToken {
    [CmdletBinding()]
    param(
        [Parameter()]
        [string]$Token
    )

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        return $Token.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($env:AI_ARENA_CONTROL_TOKEN)) {
        return $env:AI_ARENA_CONTROL_TOKEN.Trim()
    }

    if (Test-Path -LiteralPath $script:AIArenaTokenPath) {
        return (Get-Content -LiteralPath $script:AIArenaTokenPath -Raw).Trim()
    }

    throw "AI Arena control-plane token not found. Enable the control plane in AI Arena, pass -Token, or set AI_ARENA_CONTROL_TOKEN."
}

function Invoke-AIArena {
    [CmdletBinding(DefaultParameterSetName = 'Command')]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Command,

        [Parameter()]
        [hashtable]$Args = @{},

        [Parameter()]
        [string]$Prompt,

        [Parameter()]
        [string]$Path,

        [Parameter()]
        [string]$View,

        [Parameter()]
        [string]$Theme,

        [Parameter()]
        [string]$Route,

        [Parameter()]
        [string]$Id,

        [Parameter()]
        [switch]$Always,

        [Parameter()]
        [string]$Model,

        [Parameter()]
        [switch]$RefreshModels,

        [Parameter()]
        [string]$BaseUrl,

        [Parameter()]
        [string]$State,

        [Parameter()]
        [string]$Preset,

        [Parameter()]
        [bool]$Enabled,

        [Parameter()]
        [switch]$ConfirmReset,

        [Parameter()]
        [ValidateRange(100, 2147483647)]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $mergedArgs = @{}
    foreach ($key in $Args.Keys) {
        $mergedArgs[$key] = $Args[$key]
    }

    if ($PSBoundParameters.ContainsKey('Prompt')) {
        $mergedArgs['prompt'] = $Prompt
    }

    if ($PSBoundParameters.ContainsKey('Path')) {
        $mergedArgs['path'] = $Path
    }

    if ($PSBoundParameters.ContainsKey('View')) {
        $mergedArgs['view'] = $View
    }

    if ($PSBoundParameters.ContainsKey('Theme')) {
        $mergedArgs['theme'] = $Theme
    }

    if ($PSBoundParameters.ContainsKey('Route')) {
        $mergedArgs['route'] = $Route
    }

    if ($PSBoundParameters.ContainsKey('Id')) {
        $mergedArgs['id'] = $Id
    }

    if ($PSBoundParameters.ContainsKey('Always')) {
        $mergedArgs['always'] = [bool]$Always
    }

    if ($PSBoundParameters.ContainsKey('Model')) {
        $mergedArgs['model'] = $Model
    }

    if ($PSBoundParameters.ContainsKey('RefreshModels')) {
        $mergedArgs['refreshModels'] = [bool]$RefreshModels
    }

    if ($PSBoundParameters.ContainsKey('BaseUrl')) {
        $mergedArgs['baseUrl'] = $BaseUrl
    }

    if ($PSBoundParameters.ContainsKey('State')) {
        $mergedArgs['state'] = $State
    }

    if ($PSBoundParameters.ContainsKey('Preset')) {
        $mergedArgs['preset'] = $Preset
    }

    if ($PSBoundParameters.ContainsKey('Enabled')) {
        $mergedArgs['enabled'] = $Enabled
    }

    if ($PSBoundParameters.ContainsKey('ConfirmReset')) {
        $mergedArgs['confirm'] = [bool]$ConfirmReset
    }

    $request = @{
        id = [guid]::NewGuid().ToString('N')
        command = $Command
        args = $mergedArgs
        token = Get-AIArenaControlToken -Token $Token
    } | ConvertTo-Json -Depth 12 -Compress

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $script:AIArenaPipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    try {
        $pipe.Connect($TimeoutMs)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        try {
            $writer.NewLine = "`n"
            $writer.AutoFlush = $true
            $writer.WriteLine($request)
            $readTask = $reader.ReadLineAsync()
            $completedTask = [System.Threading.Tasks.Task]::WhenAny(
                $readTask,
                [System.Threading.Tasks.Task]::Delay($TimeoutMs)).GetAwaiter().GetResult()
            if (-not [object]::ReferenceEquals($completedTask, $readTask)) {
                throw "Timed out after $TimeoutMs ms waiting for AI Arena to finish '$Command'."
            }

            $line = $readTask.GetAwaiter().GetResult()
            if ([string]::IsNullOrWhiteSpace($line)) {
                throw 'AI Arena returned an empty response.'
            }

            return $line | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
            $writer.Dispose()
        }
    }
    finally {
        $pipe.Dispose()
    }
}

function Invoke-AIArenaAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('state', 'send', 'approve', 'reject', 'stop', 'stage.next', 'stage.verify', 'stage.artifact', 'command.state', 'work.brief', 'build.evidence', 'outputs', 'runbook.state', 'runbook.resume', 'runbook.checkpoint')]
        [string]$Action,

        [Parameter()]
        [string]$Prompt,

        [Parameter()]
        [string]$Summary,

        [Parameter()]
        [string]$Kind = 'operator',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $command = "agent.$Action"
    if ($Action -eq 'send') {
        return Invoke-AIArena -Command $command -Prompt $Prompt -TimeoutMs $TimeoutMs -Token $Token
    }

    if ($Action -eq 'runbook.checkpoint') {
        return Invoke-AIArena -Command $command -Args @{ summary = $Summary; kind = $Kind } -TimeoutMs $TimeoutMs -Token $Token
    }

    return Invoke-AIArena -Command $command -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaRunbook {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'agent.runbook.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaSession {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'session.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaMatchSetup {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.setup.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Open-AIArenaMatchSetup {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [ValidateSet('scenario', 'cast', 'matrix', 'saved')]
        [string]$Section = 'scenario',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.setup.open' -Args @{ section = $Section } -TimeoutMs $TimeoutMs -Token $Token
}

function Close-AIArenaMatchSetup {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.setup.close' -TimeoutMs $TimeoutMs -Token $Token
}

function Export-AIArenaMatchSetup {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [ValidateScript({
            if ([string]::IsNullOrWhiteSpace($_)) {
                throw 'Export path cannot be blank.'
            }

            if (-not [System.IO.Path]::GetExtension($_).Equals('.json', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Export path must end in .json.'
            }

            return $true
        })]
        [string]$Path,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $response = Invoke-AIArena -Command 'match.setup.export' -TimeoutMs $TimeoutMs -Token $Token
    if ($response.ok -and $PSBoundParameters.ContainsKey('Path')) {
        $fullPath = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
        $parent = [System.IO.Path]::GetDirectoryName($fullPath)
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            [System.IO.Directory]::CreateDirectory($parent) | Out-Null
        }

        [System.IO.File]::WriteAllText(
            $fullPath,
            [string]$response.data.state.json,
            [System.Text.UTF8Encoding]::new($false))
        $response | Add-Member -NotePropertyName ExportPath -NotePropertyValue $fullPath -Force
    }

    return $response
}

function Import-AIArenaMatchSetup {
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory = $true, Position = 0, ParameterSetName = 'Path')]
        [ValidateScript({
            if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) {
                throw "Match Setup package was not found: $_"
            }

            if (-not [System.IO.Path]::GetExtension($_).Equals('.json', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Match Setup package path must end in .json.'
            }

            return $true
        })]
        [string]$Path,

        [Parameter(Mandatory = $true, ParameterSetName = 'Json')]
        [ValidateNotNullOrEmpty()]
        [string]$Json,

        [Parameter()]
        [string]$Name,

        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    $importArgs = @{}
    $temporaryPackagePath = $null
    try {
        if ($PSCmdlet.ParameterSetName -eq 'Json') {
            # Keep the package outside the bounded named-pipe envelope. The app
            # reads the temporary file synchronously and this block always removes it.
            $temporaryPackagePath = Join-Path ([System.IO.Path]::GetTempPath()) ("ai-arena-match-setup-{0}.json" -f [Guid]::NewGuid().ToString('N'))
            [System.IO.File]::WriteAllText($temporaryPackagePath, $Json, [System.Text.UTF8Encoding]::new($false))
            $importArgs['path'] = $temporaryPackagePath
        }
        else {
            $importArgs['path'] = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
        }

        if ($PSBoundParameters.ContainsKey('Name')) {
            $importArgs['name'] = $Name
        }

        Invoke-AIArena -Command 'match.setup.import' -Args $importArgs -TimeoutMs $TimeoutMs -Token $Token
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($temporaryPackagePath)) {
            Remove-Item -LiteralPath $temporaryPackagePath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Set-AIArenaMatchRoster {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateRange(1, 8)]
        [int]$Count,

        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.roster.set' -Args @{ count = $Count } -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaMatchMatrix {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.matrix.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaMatchMatrix {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('round_robin_challenge', 'mutual_rivals', 'evidence_ladder', 'support_chain', 'deescalation_ring', 'devils_triangle', 'skeptic_sweep', 'paired_crossfire', 'spotlight_defense', 'off')]
        [string]$Pattern,

        [Parameter()]
        [bool]$Enabled = $true,

        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.matrix.set' -Args @{ pattern = $Pattern; enabled = $Enabled } -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaMatchGeneration {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'match.generation.state' -TimeoutMs $TimeoutMs -Token $Token
}

function New-AIArenaMatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('random', 'ai', 'current', 'wild')]
        [string]$Mode,

        [Parameter()]
        [string]$Style,

        [Parameter()]
        [string]$Intensity,

        [Parameter()]
        [string]$RolePack,

        [Parameter()]
        [string]$Absurdity,

        [Parameter()]
        [string]$Seed,

        [Parameter()]
        [string]$Prompt,

        [Parameter()]
        [string]$Query,

        [Parameter()]
        [switch]$ConfirmWild,

        [Parameter()]
        [int]$TimeoutMs = 120000,

        [Parameter()]
        [string]$Token
    )

    if ($Mode -eq 'wild' -and -not $ConfirmWild) {
        throw 'Wild generation requires -ConfirmWild because it makes a broad setup change.'
    }

    $args = @{}
    foreach ($name in 'Style', 'Intensity', 'RolePack', 'Absurdity', 'Seed', 'Prompt', 'Query') {
        if ($PSBoundParameters.ContainsKey($name)) {
            $args[$name.Substring(0, 1).ToLowerInvariant() + $name.Substring(1)] = $PSBoundParameters[$name]
        }
    }
    if ($Mode -eq 'wild') {
        $args.confirm = $true
    }

    Invoke-AIArena -Command "match.generate.$Mode" -Args $args -TimeoutMs $TimeoutMs -Token $Token
}

function Invoke-AIArenaMatchReplay {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter()]
        [switch]$NewSession,

        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    $command = if ($NewSession) { 'match.replay.new' } else { 'match.replay' }
    Invoke-AIArena -Command $command -Id $Id -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaSettings {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'settings.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaSettings {
    [CmdletBinding()]
    param(
        [Parameter()]
        [bool]$CompactTranscript,

        [Parameter()]
        [bool]$FollowTranscript,

        [Parameter()]
        [ValidateSet('diagnostics', 'telemetry', 'hidden')]
        [string]$TopStripMode,

        [Parameter()]
        [bool]$TurnCompare,

        [Parameter()]
        [bool]$MatchTimeline,

        [Parameter()]
        [bool]$BattleReview,

        [Parameter()]
        [bool]$MemoryNotes,

        [Parameter()]
        [bool]$DecisionCard,

        [Parameter()]
        [bool]$AutoModerator,

        [Parameter()]
        [bool]$StyleFit,

        [Parameter()]
        [bool]$InternetDetails,

        [Parameter()]
        [bool]$VoiceEnabled,

        [Parameter()]
        [bool]$WorldEnabled,

        [Parameter()]
        [bool]$AgentWorkspaceEnabled,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $args = @{}
    $preferenceNames = @(
        'CompactTranscript',
        'FollowTranscript',
        'TopStripMode',
        'TurnCompare',
        'MatchTimeline',
        'BattleReview',
        'MemoryNotes',
        'DecisionCard',
        'AutoModerator',
        'StyleFit',
        'InternetDetails',
        'VoiceEnabled',
        'WorldEnabled',
        'AgentWorkspaceEnabled'
    )
    foreach ($name in $preferenceNames) {
        if ($PSBoundParameters.ContainsKey($name)) {
            $argumentName = $name.Substring(0, 1).ToLowerInvariant() + $name.Substring(1)
            $args[$argumentName] = $PSBoundParameters[$name]
        }
    }

    if ($args.Count -eq 0) {
        throw 'Set-AIArenaSettings requires at least one preference parameter.'
    }

    Invoke-AIArena -Command 'settings.update' -Args $args -TimeoutMs $TimeoutMs -Token $Token
}

function Open-AIArenaSettings {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Query,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $args = @{}
    if ($PSBoundParameters.ContainsKey('Query')) {
        $args.query = $Query
    }
    Invoke-AIArena -Command 'settings.open' -Args $args -TimeoutMs $TimeoutMs -Token $Token
}

function Close-AIArenaSettings {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'settings.close' -TimeoutMs $TimeoutMs -Token $Token
}

function Search-AIArenaSettings {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [AllowEmptyString()]
        [string]$Query = '',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'settings.search' -Args @{ query = $Query } -TimeoutMs $TimeoutMs -Token $Token
}

function Select-AIArenaSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter()]
        [int]$TimeoutMs = 10000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'session.select' -Id $Id -TimeoutMs $TimeoutMs -Token $Token
}

function New-AIArenaSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Name,

        [Parameter()]
        [int]$TimeoutMs = 10000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'session.create' -Args @{ name = $Name } -TimeoutMs $TimeoutMs -Token $Token
}

function New-AIArenaSessionFork {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Name = '',

        [Parameter()]
        [int]$TimeoutMs = 10000,

        [Parameter()]
        [string]$Token
    )

    $forkArgs = @{}
    if ($PSBoundParameters.ContainsKey('Name')) {
        $forkArgs['name'] = $Name
    }

    Invoke-AIArena -Command 'session.fork' -Args $forkArgs -TimeoutMs $TimeoutMs -Token $Token
}

function New-AIArenaCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Name = '',

        [Parameter()]
        [int]$TimeoutMs = 10000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'session.checkpoint.create' -Args @{ name = $Name } -TimeoutMs $TimeoutMs -Token $Token
}

function Restore-AIArenaCheckpoint {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter()]
        [int]$TimeoutMs = 10000,

        [Parameter()]
        [string]$Token
    )

    if ($PSCmdlet.ShouldProcess("checkpoint $Id", 'Restore AI Arena session state')) {
        Invoke-AIArena -Command 'session.checkpoint.restore' -Args @{ id = $Id; confirm = $true } -TimeoutMs $TimeoutMs -Token $Token
    }
}

function Resume-AIArenaRunbook {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'agent.runbook.resume' -TimeoutMs $TimeoutMs -Token $Token
}

function Add-AIArenaRunbookCheckpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Summary,

        [Parameter()]
        [string]$Kind = 'operator',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'agent.runbook.checkpoint' -Args @{ summary = $Summary; kind = $Kind } -TimeoutMs $TimeoutMs -Token $Token
}

function Invoke-AIArenaArena {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('start', 'stop', 'turn', 'narrate', 'reset', 'operator.send')]
        [string]$Action,

        [Parameter()]
        [string]$Prompt,

        [Parameter()]
        [ValidateSet('public', 'private', 'narrator')]
        [string]$Route = 'public',

        [Parameter()]
        [switch]$Always,

        [Parameter()]
        [switch]$ConfirmReset,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $command = "arena.$Action"
    if ($Action -eq 'operator.send') {
        return Invoke-AIArena -Command $command -Prompt $Prompt -Route $Route -TimeoutMs $TimeoutMs -Token $Token
    }

    if ($Action -eq 'reset') {
        return Invoke-AIArena -Command $command -ConfirmReset:$ConfirmReset -TimeoutMs $TimeoutMs -Token $Token
    }

    return Invoke-AIArena -Command $command -TimeoutMs $TimeoutMs -Token $Token
}

function Invoke-AIArenaCollaborate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('state', 'review', 'send', 'stop', 'fork', 'repeat')]
        [string]$Action,

        [Parameter()]
        [string]$Prompt,

        [Parameter()]
        [string]$Id,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $command = "collaborate.$Action"
    if ($Action -eq 'send') {
        return Invoke-AIArena -Command $command -Prompt $Prompt -TimeoutMs $TimeoutMs -Token $Token
    }

    if ($Action -eq 'review' -or $Action -eq 'fork' -or $Action -eq 'repeat') {
        return Invoke-AIArena -Command $command -Id $Id -TimeoutMs $TimeoutMs -Token $Token
    }

    return Invoke-AIArena -Command $command -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaCollaborateReview {
    [CmdletBinding()]
    param(
        [Parameter()]
        [string]$Id,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'collaborate.review' -Id $Id -TimeoutMs $TimeoutMs -Token $Token
}

function Export-AIArena {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('transcript', 'session', 'receipts')]
        [string]$Kind,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command "export.$Kind" -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Path,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'agent.workspace.set' -Path $Path -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaAgentCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$CommandText,

        [Parameter()]
        [ValidateSet('PowerShell', 'Terminal')]
        [string]$Shell = 'PowerShell',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'agent.command.stage' -Args @{
        command = $CommandText
        shell = $Shell
    } -TimeoutMs $TimeoutMs -Token $Token
}

function Select-AIArenaView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$View,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'navigation.select' -View $View -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaCapabilities {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'capabilities' -TimeoutMs $TimeoutMs -Token $Token
}

function Save-AIArenaScreenshot {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [ValidateScript({
            if ([string]::IsNullOrWhiteSpace($_)) {
                throw 'Screenshot path cannot be blank.'
            }

            if (-not [System.IO.Path]::GetExtension($_).Equals('.png', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Screenshot path must end in .png.'
            }

            return $true
        })]
        [string]$Path,

        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    $screenshotArgs = @{}
    if ($PSBoundParameters.ContainsKey('Path')) {
        $screenshotArgs['path'] = $Path
    }

    Invoke-AIArena -Command 'app.screenshot' -Args $screenshotArgs -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaRightRail {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('show', 'hide', 'toggle')]
        [string]$State,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'navigation.rail.set' -State $State -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaViewPreset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateSet('focused', 'diagnostics', 'compact', 'review')]
        [string]$Preset,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'view.preset.set' -Preset $Preset -TimeoutMs $TimeoutMs -Token $Token
}

function Send-AIArenaKey {
    <#
        .SYNOPSIS
        Sends a chord through the shell shortcut layer.

        .DESCRIPTION
        Routed through the app's own handlers rather than simulated at the
        operating-system level, so it does not need window focus and cannot leak
        into another application. Reports whether the chord was bound.

        .EXAMPLE
        Send-AIArenaKey F2
        Send-AIArenaKey K -Modifiers ctrl
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Key,

        [Parameter(Position = 1)]
        [string]$Modifiers,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $payload = @{ key = $Key }
    if ($PSBoundParameters.ContainsKey('Modifiers')) { $payload['modifiers'] = $Modifiers }
    Invoke-AIArena -Command 'shell.input.key' -Args $payload -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaText {
    <#
        .SYNOPSIS
        Types text into a named field, or the focused one when no target is given.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Position = 1)]
        [string]$Target,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $payload = @{ text = $Text }
    if ($PSBoundParameters.ContainsKey('Target')) { $payload['target'] = $Target }
    Invoke-AIArena -Command 'shell.input.type' -Args $payload -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaPalette {
    <#
        .SYNOPSIS
        Lists the command palette entries available on the current surface.
    #>
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'shell.palette.list' -TimeoutMs $TimeoutMs -Token $Token
}

function Invoke-AIArenaPaletteCommand {
    <#
        .SYNOPSIS
        Runs a command palette entry by id.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'shell.palette.run' -Id $Id -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaInternet {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'internet.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaInternet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [bool]$Enabled,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'internet.set' -Enabled $Enabled -TimeoutMs $TimeoutMs -Token $Token
}

function Test-AIArenaInternet {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 30000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'internet.test' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaTheme {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Theme,

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'navigation.theme.set' -Theme $Theme -TimeoutMs $TimeoutMs -Token $Token
}

function Get-AIArenaProvider {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'provider.state' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaProviderConfig {
    [CmdletBinding()]
    param(
        [Parameter()]
        [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) })]
        [string]$BaseUrl,

        [Parameter()]
        [ValidateSet('openai_compatible', 'lmstudio_native', 'ollama_native')]
        [string]$ApiMode,

        [Parameter()]
        [ValidateNotNull()]
        [System.Security.SecureString]$ApiToken,

        [Parameter()]
        [switch]$ClearApiToken,

        [Parameter()]
        [AllowEmptyString()]
        [string]$Model,

        [Parameter()]
        [AllowEmptyString()]
        [string]$AlphaModel,

        [Parameter()]
        [AllowEmptyString()]
        [string]$BetaModel,

        [Parameter()]
        [AllowEmptyString()]
        [string]$GammaModel,

        [Parameter()]
        [AllowEmptyString()]
        [string]$DeltaModel,

        [Parameter()]
        [AllowEmptyString()]
        [string]$NarratorModel,

        [Parameter()]
        [ValidateRange(1, 3600)]
        [int]$TimeoutSeconds,

        [Parameter()]
        [ValidateRange(0.0, 2.0)]
        [double]$Temperature,

        [Parameter()]
        [ValidateRange(1, 32768)]
        [int]$MaxOutputTokens,

        [Parameter()]
        [ValidateRange(0, 1048576)]
        [int]$ContextLength,

        [Parameter()]
        [ValidateSet('default', 'off', 'low', 'medium', 'high', 'on')]
        [string]$Reasoning,

        [Parameter()]
        [bool]$NativeStatefulChat,

        [Parameter()]
        [ValidateRange(0, 86400)]
        [int]$NativeIdleTtlSeconds,

        [Parameter()]
        [switch]$RefreshModels,

        [Parameter()]
        [int]$TimeoutMs = 60000,

        [Parameter()]
        [string]$Token
    )

    if ($PSBoundParameters.ContainsKey('ApiToken') -and $ClearApiToken.IsPresent) {
        throw 'ApiToken and ClearApiToken cannot be used together.'
    }

    if ($PSBoundParameters.ContainsKey('ApiToken') -and $ApiToken.Length -eq 0) {
        throw 'ApiToken cannot be empty. Use -ClearApiToken to remove the saved provider token.'
    }

    $providerArgs = @{}
    $hasConfigurationChange = $false

    if ($PSBoundParameters.ContainsKey('BaseUrl')) {
        $providerArgs['baseUrl'] = $BaseUrl
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('ApiMode')) {
        $providerArgs['apiMode'] = $ApiMode
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('ClearApiToken')) {
        $providerArgs['clearApiToken'] = [bool]$ClearApiToken
        if ($ClearApiToken.IsPresent) {
            $hasConfigurationChange = $true
        }
    }
    if ($PSBoundParameters.ContainsKey('Model')) {
        $providerArgs['model'] = $Model
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('AlphaModel')) {
        $providerArgs['alphaModel'] = $AlphaModel
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('BetaModel')) {
        $providerArgs['betaModel'] = $BetaModel
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('GammaModel')) {
        $providerArgs['gammaModel'] = $GammaModel
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('DeltaModel')) {
        $providerArgs['deltaModel'] = $DeltaModel
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('NarratorModel')) {
        $providerArgs['narratorModel'] = $NarratorModel
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('TimeoutSeconds')) {
        $providerArgs['timeoutSeconds'] = $TimeoutSeconds
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('Temperature')) {
        $providerArgs['temperature'] = $Temperature
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('MaxOutputTokens')) {
        $providerArgs['maxOutputTokens'] = $MaxOutputTokens
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('ContextLength')) {
        $providerArgs['contextLength'] = $ContextLength
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('Reasoning')) {
        $providerArgs['reasoning'] = $Reasoning
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('NativeStatefulChat')) {
        $providerArgs['nativeStatefulChat'] = $NativeStatefulChat
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('NativeIdleTtlSeconds')) {
        $providerArgs['nativeIdleTtlSeconds'] = $NativeIdleTtlSeconds
        $hasConfigurationChange = $true
    }
    if ($PSBoundParameters.ContainsKey('RefreshModels')) {
        $providerArgs['refreshModels'] = [bool]$RefreshModels
    }

    if (-not $hasConfigurationChange -and -not $PSBoundParameters.ContainsKey('ApiToken')) {
        throw 'Set-AIArenaProviderConfig requires at least one provider configuration parameter. Use Update-AIArenaProviderModels to refresh models without changing configuration.'
    }

    $apiTokenBstr = [IntPtr]::Zero
    try {
        if ($PSBoundParameters.ContainsKey('ApiToken')) {
            $apiTokenBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApiToken)
            $providerArgs['apiToken'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($apiTokenBstr)
        }

        return Invoke-AIArena -Command 'provider.config.set' -Args $providerArgs -TimeoutMs $TimeoutMs -Token $Token
    }
    finally {
        if ($providerArgs.ContainsKey('apiToken')) {
            $providerArgs['apiToken'] = $null
            $null = $providerArgs.Remove('apiToken')
        }
        if ($apiTokenBstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($apiTokenBstr)
        }
    }
}

function Test-AIArenaProvider {
    [CmdletBinding()]
    param(
        [Parameter()]
        [switch]$AllRoles,

        [Parameter()]
        [int]$TimeoutMs = 310000,

        [Parameter()]
        [string]$Token
    )

    $providerArgs = @{}
    if ($PSBoundParameters.ContainsKey('AllRoles')) {
        $providerArgs['allRoles'] = [bool]$AllRoles
    }
    Invoke-AIArena -Command 'provider.test' -Args $providerArgs -TimeoutMs $TimeoutMs -Token $Token
}

function Update-AIArenaProviderModels {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 60000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'provider.models.refresh' -TimeoutMs $TimeoutMs -Token $Token
}

function Set-AIArenaProviderModel {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Model,

        [Parameter()]
        [switch]$RefreshModels,

        [Parameter()]
        [int]$TimeoutMs = 60000,

        [Parameter()]
        [string]$Token
    )

    Invoke-AIArena -Command 'provider.model.set' -Model $Model -RefreshModels:$RefreshModels -TimeoutMs $TimeoutMs -Token $Token
}

function Watch-AIArenaEvents {
    [CmdletBinding()]
    param(
        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    $request = @{
        id = [guid]::NewGuid().ToString('N')
        command = 'events.watch'
        args = @{}
        token = Get-AIArenaControlToken -Token $Token
    } | ConvertTo-Json -Depth 8 -Compress

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $script:AIArenaPipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    $pipe.Connect($TimeoutMs)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 4096, $true)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8, $false, 4096, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true
    $writer.WriteLine($request)
    while (($line = $reader.ReadLine()) -ne $null) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $line | ConvertFrom-Json
        }
    }
}

function Watch-AIArena {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [ValidateSet('events')]
        [string]$Target = 'events',

        [Parameter()]
        [int]$TimeoutMs = 5000,

        [Parameter()]
        [string]$Token
    )

    Watch-AIArenaEvents -TimeoutMs $TimeoutMs -Token $Token
}

Set-Alias -Name Invoke-AIArenaStatus -Value Invoke-AIArena
Set-Alias -Name Invoke-AIArenaAgentSend -Value Invoke-AIArenaAgent
Set-Alias -Name Watch-AIArenaControlEvents -Value Watch-AIArenaEvents
