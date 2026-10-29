# The CI's Linux job in a throwaway container (Docker), on the working tree as it is now: run it before pushing.
# Volumes keep the build, NuGet packages and the speech model between runs, so later runs are incremental.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path

docker build -q -t recompose-linux-check "$PSScriptRoot/linux" | Out-Null
docker run --rm `
    -v "${repo}:/src:ro" `
    -v recompose-linux-work:/work `
    -v recompose-linux-nuget:/root/.nuget/packages `
    -v recompose-linux-models:/models `
    recompose-linux-check bash /src/tools/ci/linux/check.sh
exit $LASTEXITCODE
