# Runs the test suite in a Linux .NET 10 SDK container.
# Use this when Windows Smart App Control blocks the locally built test assembly.
# The repository is mounted read-only and copied (without bin/obj/build/.git) into the container.
param([string]$Filter = "")

$repo = Split-Path -Parent $PSScriptRoot
$filterArg = if ($Filter) { "--filter '$Filter'" } else { "" }
$script = "mkdir /work && cd /src && tar cf - --exclude=./.git --exclude=./build --exclude='*/bin' --exclude='*/obj' . | (cd /work && tar xf -) && cd /work && dotnet test tests/Joust.Tests --nologo $filterArg"

docker run --rm -v "${repo}:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c $script
exit $LASTEXITCODE
