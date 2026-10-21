# Requires PowerShell 7.4+ (.NET 8+) and the .NET 8+ SDK.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts'))
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
if ($PSVersionTable.PSVersion -lt [version]'7.4') { throw 'Use PowerShell 7.4 or newer.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repo 'PCLPatcher.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$targets = foreach ($rid in @('win-x64', 'linux-x64')) {
    foreach ($kind in @('framework-dependent', 'self-contained')) {
        $extension = if ($rid.StartsWith('win')) { '.zip' } else { '.tar.gz' }
        [pscustomobject]@{ Rid=$rid; Kind=$kind; Name="PCLPatcher-$version-$rid-$kind$extension" }
    }
}
foreach ($target in $targets) {
    if (Test-Path -LiteralPath (Join-Path $output $target.Name)) { throw "Output already exists: $($target.Name)" }
}
foreach ($name in @('SHA256SUMS.txt', 'PACKAGE-SIZES.json')) {
    if (Test-Path -LiteralPath (Join-Path $output $name)) { throw "Output already exists: $name" }
}
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('PCLPatcher-release-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stageRoot) | Out-Null
$records = @()
try {
    foreach ($target in $targets) {
        $stage = Join-Path $stageRoot ($target.Rid + '-' + $target.Kind)
        $selfContained = $target.Kind -eq 'self-contained'
        $arguments = @('publish', $project, '-c', 'Release', '-r', $target.Rid,
            '--self-contained', $selfContained.ToString().ToLowerInvariant(), '-o', $stage,
            '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
        if ($selfContained) {
            $arguments += @('-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
        }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($target.Name)" }
        Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stage
        Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $stage
        Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $stage -Recurse
        if ($selfContained) {
            $assets = Get-Content -LiteralPath (Join-Path $repo 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
            $download = @($assets.project.frameworks.'net8.0'.downloadDependencies | Where-Object { $_.name -ieq ('Microsoft.NETCore.App.Runtime.' + $target.Rid) })
            if ($download.Count -ne 1) { throw 'Cannot identify resolved runtime package for licensing.' }
            $runtimeVersion = [regex]::Match($download[0].version, '\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?').Value
            $runtimePackage = Join-Path $assets.project.restore.packagesPath ('microsoft.netcore.app.runtime.' + $target.Rid + '/' + $runtimeVersion)
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $stage 'licenses/dotnet-LICENSE.txt')
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $stage 'licenses/dotnet-THIRD-PARTY-NOTICES.txt')
        }
        $archive = Join-Path $output $target.Name
        if ($target.Rid.StartsWith('win')) {
            [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
        } else {
            $file = [IO.File]::Create($archive)
            $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
            $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
            try {
                foreach ($item in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName) {
                    $relative = [IO.Path]::GetRelativePath($stage, $item.FullName).Replace('\', '/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
                    $entry.Mode = [IO.UnixFileMode]$(if ($relative -eq 'PCLPatcher') { 493 } else { 420 })
                    $data = [IO.File]::OpenRead($item.FullName)
                    try { $entry.DataStream = $data; $tar.WriteEntry($entry) } finally { $data.Dispose() }
                }
            } finally { $tar.Dispose(); $gzip.Dispose(); $file.Dispose() }
        }
        $records += [pscustomobject]@{ File=$target.Name; Bytes=(Get-Item -LiteralPath $archive).Length; SHA256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); Runtime=$runtimeVersion }
        $runtimeVersion = $null
    }
    $sums = ($records | ForEach-Object { "$($_.SHA256)  $($_.File)" }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), $sums + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $output 'PACKAGE-SIZES.json'), ($records | ConvertTo-Json -Depth 4) + "`n", [Text.UTF8Encoding]::new($false))
    $records | Format-Table File, Bytes
} finally {
    # The only recursive deletion target is the GUID folder created above.
    $resolved = [IO.Path]::GetFullPath($stageRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\','/') -ne $tempParent.TrimEnd('\','/') -or
        [IO.Path]::GetFileName($resolved) -notmatch '^PCLPatcher-release-[0-9a-f]{32}$') { throw 'Unexpected staging path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
