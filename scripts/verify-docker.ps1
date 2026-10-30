# Runs "Joust.Tools verify-native" in a Linux .NET 10 SDK container.
# Use this when Windows Smart App Control blocks the locally built tools assembly.
# Arguments are passed to verify-native, e.g.:
#   powershell -File scripts/verify-docker.ps1 --frames 40000 --seeds 1,2,3,4 --troll
# The repository is mounted read-only and copied (without bin/obj/build/.git) into the container.
$repo = Split-Path -Parent $PSScriptRoot
$verifyArgs = ($args | ForEach-Object { "'$_'" }) -join " "
$script = "mkdir /work && cd /src && tar cf - --exclude=./.git --exclude=./build --exclude='*/bin' --exclude='*/obj' . | (cd /work && tar xf -) && cd /work && dotnet run --project src/Joust.Tools -v q -- verify-native $verifyArgs"

docker run --rm -v "${repo}:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c $script
exit $LASTEXITCODE
