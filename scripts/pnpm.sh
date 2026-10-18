#!/usr/bin/env bash
# pnpm（フロントエンド web/ の依存・ビルド・テスト）をコンテナ内で実行するラッパー。
#
# ADR-0001 のサプライチェーン方針により、npm 依存のインストール・ビルドはホストではなく
# 資格情報を持たないコンテナ内で行う。使い方は pnpm コマンドと同じ（作業ディレクトリは web/）:
#
#   scripts/pnpm.sh install --frozen-lockfile
#   scripts/pnpm.sh typecheck
#   scripts/pnpm.sh test
#   scripts/pnpm.sh build
#   JINJI_PNPM_PORT=28000 scripts/pnpm.sh dev      # 開発サーバー（ホストの 127.0.0.1:28000 だけに公開）
#
# - リポジトリだけを /src にマウントする。ホストのホームディレクトリや資格情報（~/.npmrc 等）はマウントしない
# - HOME（corepack が取得した pnpm 本体を含む）と pnpm のストアは名前付きボリューム
#   （jinji_pnpm_home / jinji_pnpm_store）に置く
# - ホストと同じ UID/GID で実行し、node_modules・dist の所有者がずれないようにする
# - --cap-drop ALL と no-new-privileges で実行する
# - pnpm の版は web/package.json の packageManager（ハッシュ付き）で corepack が固定する
# - Node.js のイメージは版とダイジェストで固定する（更新するときは公開から 2 週間以上経った LTS のパッチ版にする）
#
# 環境変数で上書きできる項目:
#   JINJI_NODE_IMAGE   Node.js のイメージ（既定は下の固定値）
#   JINJI_PNPM_PORT    コンテナのこのポートを 127.0.0.1 の同じ番号に公開する（開発サーバー用。既定は公開しない）
#   JINJI_PNPM_NAME    コンテナ名（既定: jinji-pnpm-<PID>。停止・別コンテナからの接続に使う）
#   JINJI_PNPM_MEMORY  コンテナのメモリ上限（例: 3g。既定は無制限）
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Node.js 24 LTS（24.20.0、2026-08-27 公開）の bookworm-slim。マルチアーキテクチャのダイジェストで固定する
default_image="node:24.20.0-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e"
image="${JINJI_NODE_IMAGE:-$default_image}"
home_volume="jinji_pnpm_home"
store_volume="jinji_pnpm_store"
uid="$(id -u)"
gid="$(id -g)"
name="${JINJI_PNPM_NAME:-jinji-pnpm-$$}"

# 名前付きボリュームを初回だけ作成し、所有者を実行ユーザーに合わせる。
# （docker が作るボリュームは root 所有のため、非 root で書き込めるようにする）
ensure_volume() {
  local volume="$1"
  if ! docker volume inspect "$volume" >/dev/null 2>&1; then
    docker volume create --label jinji.purpose=pnpm "$volume" >/dev/null
    docker run --rm --network none --user 0:0 \
      -v "$volume:/mnt/volume" --entrypoint chown "$image" "$uid:$gid" /mnt/volume
  fi
}

ensure_volume "$home_volume"
ensure_volume "$store_volume"

env_args=(
  -e HOME=/home/jinji
  -e COREPACK_HOME=/home/jinji/.cache/corepack
  -e COREPACK_ENABLE_DOWNLOAD_PROMPT=0
  -e COREPACK_ENABLE_AUTO_PIN=0
  -e COREPACK_DEFAULT_TO_LATEST=0
  -e npm_config_store_dir=/pnpm-store
  -e NPM_CONFIG_UPDATE_NOTIFIER=false
  -e CI=true
)

port_args=()
if [[ -n "${JINJI_PNPM_PORT:-}" ]]; then
  if [[ ! "$JINJI_PNPM_PORT" =~ ^[0-9]{2,5}$ ]]; then
    echo "JINJI_PNPM_PORT はポート番号で指定してください: $JINJI_PNPM_PORT" >&2
    exit 2
  fi
  port_args=(-p "127.0.0.1:$JINJI_PNPM_PORT:$JINJI_PNPM_PORT" -e "JINJI_DEV_PORT=$JINJI_PNPM_PORT")
fi

limit_args=()
if [[ -n "${JINJI_PNPM_MEMORY:-}" ]]; then
  limit_args=(--memory "$JINJI_PNPM_MEMORY")
fi

tty_args=()
if [[ -t 0 && -t 1 ]]; then
  tty_args=(-it)
fi

exec docker run --rm "${tty_args[@]}" \
  --init \
  --name "$name" \
  --user "$uid:$gid" \
  --network bridge \
  "${port_args[@]}" \
  "${limit_args[@]}" \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  -v "$repo_root:/src" \
  -v "$home_volume:/home/jinji" \
  -v "$store_volume:/pnpm-store" \
  -w /src/web \
  "${env_args[@]}" \
  "$image" corepack pnpm "$@"
