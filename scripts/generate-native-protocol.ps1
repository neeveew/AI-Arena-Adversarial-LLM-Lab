param(
    [Parameter(Mandatory = $true)][string]$ProtocPath,
    [string]$SourceProtoPath = (Join-Path $PSScriptRoot '..\..\AI Arena - C++\protocols\ai_arena_native.proto')
)

$ErrorActionPreference = 'Stop'
# The C++ coordinating task owns this schema. Change it there, then regenerate.
$generatorVersion = (& $ProtocPath --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $generatorVersion -ne 'libprotoc 36.1') {
    throw 'Use the pinned official protoc 36.1 generator with Google.Protobuf 3.36.1.'
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$protocolDirectory = Join-Path $projectRoot 'protocols'
$generatedDirectory = Join-Path $projectRoot 'src\AIArena.Wpf\Modules\NativeServices\Generated'
New-Item -ItemType Directory -Path $protocolDirectory, $generatedDirectory -Force | Out-Null
$localSchema = Join-Path $protocolDirectory 'ai_arena_native.proto'
if ([IO.Path]::GetFullPath($SourceProtoPath) -ne $localSchema) {
    Copy-Item -LiteralPath $SourceProtoPath -Destination $localSchema
}
& $ProtocPath "--proto_path=$protocolDirectory" "--csharp_out=$generatedDirectory" '--csharp_opt=file_extension=.g.cs' $localSchema
if ($LASTEXITCODE -ne 0) { throw 'Native protocol C# generation failed.' }
Write-Output ('Generated native protocol from C++ protocols/ai_arena_native.proto; SHA256=' + (Get-FileHash -LiteralPath $localSchema -Algorithm SHA256).Hash)
