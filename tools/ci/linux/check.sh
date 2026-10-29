#!/usr/bin/env bash
# Runs inside the container: the steps of ci.yml's Linux job on a copy of the working tree.
# /src is the repository (read-only); /work is a volume, so builds are incremental between runs.
set -euo pipefail

rsync -a --delete --exclude .git --exclude 'bin/' --exclude 'obj/' --exclude build/ --exclude TestResults/ \
    --exclude artifacts/ --exclude publish/ --exclude models/ --exclude specs/ /src/ /work/
cd /work
export REC_REQUIRE_NATIVE=1

echo "== Check generated design resources"
python3 tools/tokens/generate.py --check
python3 tools/icons/generate.py --check

echo "== Engine"
cmake -S engine -B build/engine -DCMAKE_BUILD_TYPE=Release -G Ninja > build-configure.log 2>&1 || { cat build-configure.log; exit 1; }
cmake --build build/engine --config Release --parallel
ctest --test-dir build/engine -C Release --output-on-failure
cmake --install build/engine --config Release --component engine --prefix artifacts/native > /dev/null

echo "== Speech model and guide fixture"
mkdir -p /models
if [ ! -f /models/ggml-base.en.bin ]; then
    curl -L --fail --retry 3 -o /models/ggml-base.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin
fi
python3 -c "import hashlib,sys; h=hashlib.sha1(open('/models/ggml-base.en.bin','rb').read()).hexdigest(); sys.exit(h!='137c40403d78fd54d454da0f9bd998f78703390c')"
export RECOMPOSE_SPEECH_MODEL=/models/ggml-base.en.bin
python3 tools/ci/make-guide-fixture.py build/guide-fixture
export REC_E2E_GUIDE=/work/build/guide-fixture REC_E2E_REPORT=/work/build/e2e-heard.txt REC_REQUIRE_E2E=1

echo "== App"
dotnet build RECompose.slnx -c Release -v q -nologo
dotnet test --solution RECompose.slnx -c Release --no-build
