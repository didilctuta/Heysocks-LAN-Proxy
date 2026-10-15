$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$pythonCommand = Get-Command python -ErrorAction Stop

& $pythonCommand.Source (Join-Path $projectDirectory 'heysocks_client.py') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Client self-test failed.' }

& $pythonCommand.Source -m unittest discover -s (Join-Path $projectDirectory 'tests') -v
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }

& $pythonCommand.Source (Join-Path $projectDirectory 'build_deb.py')
if ($LASTEXITCODE -ne 0) { throw 'Debian package build failed.' }
