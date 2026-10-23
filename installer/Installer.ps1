# Installe l'application pour l'utilisateur courant (pas besoin d'être administrateur)
# et crée les raccourcis sur le Bureau et dans le menu Démarrer.
$ErrorActionPreference = 'Stop'
$src  = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\RetoucheInventaire'

Get-Process RetoucheInventaire -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
Get-ChildItem $dest -Recurse | Unblock-File

$exe   = Join-Path $dest 'RetoucheInventaire.exe'
$shell = New-Object -ComObject WScript.Shell
$dirs  = @([Environment]::GetFolderPath('Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))
foreach ($dir in $dirs) {
    $lnk = $shell.CreateShortcut((Join-Path $dir "Retouche d'inventaire.lnk"))
    $lnk.TargetPath       = $exe
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation     = "$exe,0"
    $lnk.Save()
}
Write-Host ""
Write-Host "Installé dans $dest"
Write-Host "Raccourci « Retouche d'inventaire » créé sur le Bureau et dans le menu Démarrer."
Write-Host "Pour l'avoir dans la barre des tâches : menu Démarrer, clic droit (ou appui long) sur l'icône, « Épingler à la barre des tâches »."
