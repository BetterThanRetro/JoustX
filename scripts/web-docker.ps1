# Builds the browser version of JoustX in Docker and serves it at http://127.0.0.1:<Port>,
# and over HTTPS at https://127.0.0.1:<HttpsPort> with a self-signed certificate (the browser
# warns once), as a public host such as GitHub Pages would serve it.
# The first build downloads the .NET WebAssembly tools and takes several minutes.
# -Stop stops the running container.
param([int]$Port = 8080, [int]$HttpsPort = 8443, [switch]$Stop)

$repo = Split-Path -Parent $PSScriptRoot
$name = "joustx-web"

docker rm -f $name 2>$null | Out-Null
if ($Stop) { exit 0 }

docker build -f "$repo/docker/web/Dockerfile" -t $name $repo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
docker run -d --name $name -p "${Port}:80" -p "${HttpsPort}:443" $name | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "JoustX is at http://127.0.0.1:$Port and https://127.0.0.1:$HttpsPort"
Write-Host "Gamepad check page: /pads.html"
