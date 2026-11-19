#!/usr/bin/env bash
# Build, test and pack the Windows Driver SDK (cross-platform parts).
# Usage: ./build.sh [Debug|Release]
set -euo pipefail

CONFIG="${1:-Debug}"
cd "$(dirname "$0")"

echo "== restore =="
dotnet restore

echo "== build ($CONFIG) =="
dotnet build --no-restore -c "$CONFIG"

echo "== test =="
dotnet test --no-build -c "$CONFIG"

echo "== pack =="
dotnet pack --no-build -c "$CONFIG" -o artifacts/

echo "== infgen smoke test =="
dotnet tool install --prerelease --tool-path ./tools-out --add-source ./artifacts WindowsDriver.InfGen >/dev/null || true
./tools-out/infgen generate --manifest samples/VirtualAudio/manifest.json --out /tmp/virtualaudio.inf
./tools-out/infgen validate --inf /tmp/virtualaudio.inf

echo "Done. Packages are in ./artifacts/"
