#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
OUTPUT_ROOT="${2:-$(pwd)/release-cli}"
WITH_CORES="${WITH_CORES:-1}"
PROJECT="v2rayN/v2rayN.Cli/v2rayN.Cli.csproj"

usage() {
  cat <<'EOF'
Build v2rayN-cli for Linux or macOS.

Usage:
  ./package-cli.sh <linux-x64|linux-arm64|osx-x64|osx-arm64> [output-directory]

Environment:
  WITH_CORES=0  Build the CLI without downloading the v2rayN core bundle.
EOF
}

case "$RID" in
  linux-x64)   CORE_ARCH="linux-64" ;;
  linux-arm64) CORE_ARCH="linux-arm64" ;;
  osx-x64)     CORE_ARCH="macos-64" ;;
  osx-arm64)   CORE_ARCH="macos-arm64" ;;
  -h|--help|"") usage; exit 0 ;;
  *) echo "Unsupported RID: $RID" >&2; usage >&2; exit 2 ;;
esac

command -v dotnet >/dev/null 2>&1 || {
  echo "dotnet SDK 10.x is required." >&2
  exit 1
}

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' v2rayN/Directory.Build.props | head -n1)"
PACKAGE_NAME="v2rayN-cli-${VERSION}-${RID}"
PUBLISH_DIR="${OUTPUT_ROOT}/${PACKAGE_NAME}"
ARCHIVE="${OUTPUT_ROOT}/${PACKAGE_NAME}.tar.gz"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

dotnet publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  --output "$PUBLISH_DIR"

if [[ "$WITH_CORES" == "1" ]]; then
  TEMP_DIR="$(mktemp -d)"
  trap 'rm -rf "$TEMP_DIR"' EXIT
  CORE_ZIP="v2rayN-${CORE_ARCH}.zip"
  CORE_URL="https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/${CORE_ZIP}"

  echo "Downloading core bundle: $CORE_URL"
  curl -fL --retry 3 "$CORE_URL" -o "$TEMP_DIR/$CORE_ZIP"
  unzip -q "$TEMP_DIR/$CORE_ZIP" -d "$TEMP_DIR/extracted"

  CORE_ROOT="$TEMP_DIR/extracted/v2rayN-${CORE_ARCH}"
  [[ -d "$CORE_ROOT/bin" ]] || {
    echo "Core bundle did not contain the expected bin directory." >&2
    exit 1
  }
  cp -R "$CORE_ROOT/bin" "$PUBLISH_DIR/bin"
fi

printf '%s\n' \
  'v2rayN-cli stores configuration in the current user data directory by default.' \
  'Run ./v2rayN-cli help for commands.' \
  > "$PUBLISH_DIR/README.txt"
touch "$PUBLISH_DIR/NotStoreConfigHere.txt"
chmod +x "$PUBLISH_DIR/v2rayN-cli"

tar -C "$OUTPUT_ROOT" -czf "$ARCHIVE" "$PACKAGE_NAME"
echo "Created: $ARCHIVE"
