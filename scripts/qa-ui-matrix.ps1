$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-AIArenaQaUiMatrixPassCells {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Cells,

        [Parameter(Mandatory)]
        [ValidateRange(1, 5)]
        [int]$PassNumber
    )

    $passPrefix = "p$($PassNumber.ToString('D2'))."
    return @($Cells | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties['key'] -and
        ([string]$_.key).StartsWith($passPrefix, [StringComparison]::Ordinal)
    })
}
