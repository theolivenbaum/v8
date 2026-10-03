#!/usr/bin/env bash
# Downloads Octane 2.0 (BSD, the V8 project authors) from the npm mirror into
# dotnet/artifacts/octane. V8 keeps it in test/benchmarks/data, a DEPS
# checkout this environment cannot reach.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dest="$here/../../artifacts/octane"
mkdir -p "$dest"
tmp="$(mktemp -d)"
curl -sSfL https://registry.npmjs.org/benchmark-octane/-/benchmark-octane-1.0.1.tgz | tar xz -C "$tmp"
cp "$tmp"/package/lib/octane/*.js "$dest"/
rm -rf "$tmp"
echo "octane -> $dest"
