$ErrorActionPreference = 'Stop'
$base = 'https://github.com/isaiahpettingill/LevelBuilder/releases/latest/download'
$asset = 'LevelBuilder-win-x64.zip'
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('LevelBuilder-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $env:LOCALAPPDATA 'LevelBuilder'
try {
    New-Item -ItemType Directory -Force $stage | Out-Null
    Invoke-WebRequest "$base/$asset" -OutFile (Join-Path $stage $asset)
    Invoke-WebRequest "$base/$asset.sha256" -OutFile (Join-Path $stage "$asset.sha256")
    $expected = ((Get-Content (Join-Path $stage "$asset.sha256") -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash (Join-Path $stage $asset) -Algorithm SHA256).Hash
    if ($actual -ine $expected) { throw 'Release checksum does not match.' }
    New-Item -ItemType Directory -Force $target | Out-Null
    Expand-Archive -Path (Join-Path $stage $asset) -DestinationPath $target -Force
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'LevelBuilder.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $target 'LevelBuilder.App.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.Save()
    Write-Host "Installed LevelBuilder at $target"
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
