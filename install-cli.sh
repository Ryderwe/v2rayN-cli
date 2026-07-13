#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${V2RAYN_CLI_REPOSITORY:-Ryderwe/v2rayN-cli}"
INSTALL_DIR="${V2RAYN_CLI_INSTALL_DIR:-$HOME/.local/share/v2rayN-cli}"
BIN_DIR="${V2RAYN_CLI_BIN_DIR:-$HOME/.local/bin}"
SKIP_PATH_UPDATE="${V2RAYN_CLI_SKIP_PATH_UPDATE:-0}"

fail() {
  echo "安装失败：$*" >&2
  exit 1
}

command -v curl >/dev/null 2>&1 || fail "缺少 curl。"
command -v tar >/dev/null 2>&1 || fail "缺少 tar。"

case "$(uname -s)" in
  Darwin) os="osx" ;;
  Linux) os="linux" ;;
  *) fail "当前只支持 macOS 和 Linux。" ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch="x64" ;;
  arm64|aarch64) arch="arm64" ;;
  *) fail "不支持的 CPU 架构：$(uname -m)" ;;
esac

rid="${os}-${arch}"
api_url="https://api.github.com/repos/${REPOSITORY}/releases/latest"

echo "正在查询 ${REPOSITORY} 的最新版本……"
release_json="$(curl -fsSL --retry 3 \
  -H "Accept: application/vnd.github+json" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  "$api_url")"

release_tag="$(printf '%s\n' "$release_json" | awk '
  /"tag_name":/ {
    line=$0
    sub(/^.*"tag_name":[[:space:]]*"/, "", line)
    sub(/".*$/, "", line)
    print line
    exit
  }
')"

asset_url="$(printf '%s\n' "$release_json" | awk -v suffix="-${rid}.tar.gz" '
  /"browser_download_url":/ && index($0, suffix) > 0 {
    line=$0
    sub(/^.*"browser_download_url":[[:space:]]*"/, "", line)
    sub(/".*$/, "", line)
    print line
    exit
  }
')"

checksum_url="$(printf '%s\n' "$release_json" | awk '
  /"browser_download_url":/ && index($0, "/SHA256SUMS") > 0 {
    line=$0
    sub(/^.*"browser_download_url":[[:space:]]*"/, "", line)
    sub(/".*$/, "", line)
    print line
    exit
  }
')"

[[ -n "$release_tag" ]] || fail "无法读取最新 Release 版本。"
[[ -n "$asset_url" ]] || fail "最新 Release 中没有 ${rid} 安装包。"
[[ -n "$checksum_url" ]] || fail "最新 Release 中没有 SHA256SUMS。"

archive_name="${asset_url##*/}"
temp_dir="$(mktemp -d 2>/dev/null || mktemp -d -t v2rayn-cli)"
trap 'rm -rf "$temp_dir"' EXIT

echo "正在下载 ${release_tag} / ${rid}……"
curl -fL --retry 3 --progress-bar "$asset_url" -o "$temp_dir/$archive_name"
curl -fsSL --retry 3 "$checksum_url" -o "$temp_dir/SHA256SUMS"

expected_hash="$(awk -v name="$archive_name" '$2 == name { print $1; exit }' "$temp_dir/SHA256SUMS")"
[[ -n "$expected_hash" ]] || fail "SHA256SUMS 中没有 ${archive_name}。"

if command -v sha256sum >/dev/null 2>&1; then
  actual_hash="$(sha256sum "$temp_dir/$archive_name" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then
  actual_hash="$(shasum -a 256 "$temp_dir/$archive_name" | awk '{ print $1 }')"
else
  fail "找不到 sha256sum 或 shasum，无法验证安装包。"
fi

[[ "$actual_hash" == "$expected_hash" ]] || fail "SHA256 校验失败，请勿运行下载的文件。"
echo "SHA256 校验通过。"

stage_dir="$temp_dir/stage"
mkdir -p "$stage_dir"
tar -xzf "$temp_dir/$archive_name" -C "$stage_dir" --strip-components=1
[[ -f "$stage_dir/v2rayN-cli" ]] || fail "安装包中缺少 v2rayN-cli。"
chmod +x "$stage_dir/v2rayN-cli"

mkdir -p "$(dirname "$INSTALL_DIR")" "$BIN_DIR"
backup_dir=""
if [[ -e "$INSTALL_DIR" || -L "$INSTALL_DIR" ]]; then
  backup_dir="${INSTALL_DIR}.backup-$RANDOM-$$"
  mv "$INSTALL_DIR" "$backup_dir"
fi

if ! mv "$stage_dir" "$INSTALL_DIR"; then
  if [[ -n "$backup_dir" && -e "$backup_dir" ]]; then
    mv "$backup_dir" "$INSTALL_DIR"
  fi
  fail "无法写入 ${INSTALL_DIR}。"
fi

if [[ -n "$backup_dir" && -e "$backup_dir" ]]; then
  rm -rf "$backup_dir"
fi

ln -sfn "$INSTALL_DIR/v2rayN-cli" "$BIN_DIR/v2rayN-cli"

if [[ "$os" == "osx" ]] && command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$INSTALL_DIR" 2>/dev/null || true
fi

if [[ "$SKIP_PATH_UPDATE" != "1" ]]; then
  # Keep HOME and PATH literal so they are evaluated whenever the shell starts.
  # shellcheck disable=SC2016
  path_line='export PATH="$HOME/.local/bin:$PATH"'
  case "${SHELL:-}" in
    */zsh) shell_rc="$HOME/.zshrc" ;;
    */bash) shell_rc="$HOME/.bashrc" ;;
    *) shell_rc="$HOME/.profile" ;;
  esac

  touch "$shell_rc"
  # shellcheck disable=SC2016
  if ! grep -Fq '$HOME/.local/bin' "$shell_rc"; then
    printf '\n%s\n' "$path_line" >> "$shell_rc"
    echo "已将 ~/.local/bin 写入 ${shell_rc}。"
  fi
fi

echo
echo "v2rayN-cli ${release_tag} 安装完成：${INSTALL_DIR}"
echo "启动命令：v2rayN-cli ui"
