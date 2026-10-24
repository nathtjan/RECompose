#!/usr/bin/env bash
# Builds the native engine, stages it for .NET, then builds and tests the app.
# Usage: ./build.sh [Debug|Release]   (the .NET configuration; the engine is always Release)
set -euo pipefail

CONFIG="${1:-Debug}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENGINE_BUILD="$ROOT/build/engine"

cmake -S "$ROOT/engine" -B "$ENGINE_BUILD" -DCMAKE_BUILD_TYPE=Release
cmake --build "$ENGINE_BUILD" --config Release --parallel
ctest --test-dir "$ENGINE_BUILD" -C Release --output-on-failure
cmake --install "$ENGINE_BUILD" --config Release --component engine --prefix "$ROOT/artifacts/native"

dotnet build "$ROOT/RECompose.slnx" -c "$CONFIG"
dotnet test --solution "$ROOT/RECompose.slnx" -c "$CONFIG" --no-build

echo
echo "Done. Run the app with: dotnet run --project src/RECompose.App -c $CONFIG"
