#!/usr/bin/env bash
# 開発サーバー（scripts/pnpm.sh dev、コンテナ名 jinji-pnpm-dev）に、公式の Playwright イメージのコンテナから接続して
# スクリーンショットを web/screenshots/ に撮る。ホストにブラウザ・Playwright は入れない。
#
#   JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh dev &   # 別の端末で
#   web/scripts/screenshots.sh [出力ディレクトリ（web/ からの相対。既定: screenshots）]
#
# - 開発サーバーのコンテナのネットワーク名前空間に参加し、127.0.0.1:28000 に接続する（ホストのネットワークは使わない）
# - リポジトリだけをマウントし、ホストと同じ UID/GID・--cap-drop ALL で実行する
# - イメージの版は web/package.json の playwright-core と揃える
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
image="${JINJI_PLAYWRIGHT_IMAGE:-mcr.microsoft.com/playwright:v1.63.0-noble}"
server="${JINJI_PNPM_NAME:-jinji-pnpm-dev}"
port="${JINJI_PNPM_PORT:-28000}"

exec docker run --rm --init \
  --name "jinji-playwright-$$" \
  --network "container:$server" \
  --user "$(id -u):$(id -g)" \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --shm-size 1g \
  -e HOME=/tmp \
  -e "JINJI_SHOTS=${JINJI_SHOTS:-}" \
  -v "$repo_root:/src" \
  -w /src/web \
  "$image" node scripts/screenshots.mjs "http://127.0.0.1:$port" "${1:-screenshots}"
