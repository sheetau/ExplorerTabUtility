param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$required = @('ExplorerTabUtility.exe', 'ExplorerTabUtility.dll', 'ExplorerTabUtility.pri', 'App.xbf', 'UI/MainWindow.xbf', 'Assets/Icon.ico', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $sourcePath $name) -PathType Leaf)) { throw "Missing runtime file: $name" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination (Join-Path $sourcePath 'LICENSE')
# Publish contains only runtime files. Symbols and repository documentation are not shipped.
Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Filter '*.pdb' | Remove-Item
$files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
    ForEach-Object { $_.FullName.Substring($sourcePath.Length + 1).Replace('\','/') } |
    Where-Object { $_ -ne 'installed-files.json' }) + 'installed-files.json'
[IO.File]::WriteAllText((Join-Path $sourcePath 'installed-files.json'), (ConvertTo-Json -InputObject @($files | Sort-Object) -Compress), [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $sourcePath '*') -DestinationPath $Destination -Force
