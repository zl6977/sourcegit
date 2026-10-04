param(
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64')][string[]]$Runtimes = @('linux-x64', 'linux-arm64'),
    [string]$Output = 'build/remote',
    [switch]$IncludeClient,
    [string]$BuildId
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if ([string]::IsNullOrEmpty($BuildId)) {
    $sourceBytes = [System.IO.MemoryStream]::new()
    try {
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'modules'), (Join-Path $repositoryRoot 'src') -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.axaml' -and $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName | ForEach-Object {
            $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($_.FullName.Substring($repositoryRoot.Length))
            $sourceBytes.Write($nameBytes, 0, $nameBytes.Length)
            $fileBytes = [System.IO.File]::ReadAllBytes($_.FullName)
            $sourceBytes.Write($fileBytes, 0, $fileBytes.Length)
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $digest = -join ($sha.ComputeHash($sourceBytes.ToArray()) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
        $BuildId = "remote-$($digest.Substring(0, 20))"
    } finally { $sourceBytes.Dispose() }
}
foreach ($runtime in $Runtimes) {
    $destination = Join-Path $outputRoot $runtime
    & dotnet publish (Join-Path $repositoryRoot 'modules/SourceGit.Server/SourceGit.Server.csproj') -c Release -r $runtime --self-contained true -p:PublishAot=false -p:NuGetAudit=false "-p:SourceGitBuildId=$BuildId" -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Server publish failed for $runtime" }
    $archive = Join-Path $outputRoot "sourcegit-server-$runtime.tar.gz"
    & tar -czf $archive -C $destination .
    if ($LASTEXITCODE -ne 0) { throw "Server packaging failed for $runtime" }
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$archive.sha256", "$digest  sourcegit-server-$runtime.tar.gz`n")
    @{ buildId = $BuildId; runtime = $runtime; sha256 = $digest; protocolVersion = 1; package = "sourcegit-server-$runtime.tar.gz" } | ConvertTo-Json | Set-Content "$archive.manifest.json"
}
if ($IncludeClient) {
    & dotnet publish (Join-Path $repositoryRoot 'src/SourceGit.csproj') -c Release -r win-x64 --self-contained true -p:NuGetAudit=false "-p:SourceGitBuildId=$BuildId" -o (Join-Path $outputRoot 'client')
    if ($LASTEXITCODE -ne 0) { throw 'Windows Native AOT client publish failed' }
    $clientServerDirectory = Join-Path $outputRoot 'client/server'
    New-Item -ItemType Directory -Path $clientServerDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $outputRoot -Filter 'sourcegit-server-linux-*.tar.gz*' | Copy-Item -Destination $clientServerDirectory
}
