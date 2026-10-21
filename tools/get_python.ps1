# Downloads the embedded Python that Polymorph carries (python.org "embeddable package", 3.12 x64) into pyembed\.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dst = Join-Path $root "pyembed"
if (Test-Path (Join-Path $dst "python312.dll")) { exit 0 }
$url = "https://www.python.org/ftp/python/3.12.7/python-3.12.7-embed-amd64.zip"
$zip = Join-Path $env:TEMP "python-3.12.7-embed-amd64.zip"
Write-Host "Downloading the embedded Python (once)..."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Expand-Archive -Path $zip -DestinationPath $dst -Force
Remove-Item $zip
