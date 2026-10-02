#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${1:-linux-x64}"
ARTIFACTS="$ROOT/artifacts/linux/$RUNTIME"

rm -rf "$ARTIFACTS"
mkdir -p "$ARTIFACTS"

dotnet restore "$ROOT/FaceHubManagement.sln"
dotnet build "$ROOT/FaceHubManagement.sln" -c "$CONFIGURATION" --no-restore

dotnet publish "$ROOT/src/FaceHubManagement.Desktop/FaceHubManagement.Desktop.csproj" \
  -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true -p:PublishSingleFile=false \
  -o "$ARTIFACTS/desktop"

dotnet publish "$ROOT/src/FaceHubManagement.Agent/FaceHubManagement.Agent.csproj" \
  -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true -p:PublishSingleFile=false \
  -o "$ARTIFACTS/agent"

# dotnet publish "$ROOT/src/FaceHubManagement.SmokeTest/FaceHubManagement.SmokeTest.csproj" \
  # -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true -p:PublishSingleFile=false \
  # -o "$ARTIFACTS/smoketest"
  


echo "Linux artifacts created at $ARTIFACTS"
