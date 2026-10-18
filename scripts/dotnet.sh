#!/usr/bin/env bash
# .NET SDK をコンテナ内で実行するラッパー。
#
# ADR-0001 のサプライチェーン方針により、依存の復元・ビルド・テストはホストではなく
# 資格情報を持たないコンテナ内で行う。使い方は dotnet コマンドと同じ:
#
#   scripts/dotnet.sh build
#   scripts/dotnet.sh test
#   scripts/dotnet.sh run --project src/JinJi.Host -- migrate
#
# - リポジトリだけを /src にマウントする。ホストのホームディレクトリや資格情報はマウントしない
# - HOME と NuGet のキャッシュは名前付きボリューム（jinji_dotnet_home / jinji_nuget）に置く
# - ホストと同じ UID/GID で実行し、bin/obj の所有者がずれないようにする
# - Compose のネットワーク（jinji_default）があれば参加し、PostgreSQL に postgres:5432 で接続する
#
# 環境変数で上書きできる項目:
#   JINJI_DOTNET_IMAGE    SDK イメージ（既定: mcr.microsoft.com/dotnet/sdk:10.0）
#   JINJI_DOTNET_NETWORK  参加させる Docker ネットワーク（既定: jinji_default があればそれ、無ければ bridge）
#   JINJI_DOTNET_MEMORY   コンテナのメモリ上限（例: 2g。既定は無制限。cgroup のメモリ制御が無効なカーネルでは効かない）
#   JINJI_DOTNET_PASS_ENV コンテナに渡す環境変数の名前（空白かカンマ区切り。既定は空 = 何も渡さない）。
#                         実 LLM を使う評価のときだけ、呼び出し側のシェルにある API キーを名前で渡す（値はコマンドラインに現れない）:
#                           set -a; . ./.env; set +a
#                           JINJI_DOTNET_PASS_ENV=OPENAI_API_KEY scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-ingest ... --llm true
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${JINJI_DOTNET_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
home_volume="jinji_dotnet_home"
nuget_volume="jinji_nuget"
uid="$(id -u)"
gid="$(id -g)"

# 名前付きボリュームを初回だけ作成し、所有者を実行ユーザーに合わせる。
# （docker が作るボリュームは root 所有のため、非 root で書き込めるようにする）
ensure_volume() {
  local name="$1"
  if ! docker volume inspect "$name" >/dev/null 2>&1; then
    docker volume create --label jinji.purpose=dotnet-sdk "$name" >/dev/null
    docker run --rm --network none --user 0:0 \
      -v "$name:/mnt/volume" --entrypoint chown "$image" "$uid:$gid" /mnt/volume
  fi
}

ensure_volume "$home_volume"
ensure_volume "$nuget_volume"

# ネットワークの選択。Compose の PostgreSQL に繋ぐテストのため、あれば jinji_default に参加する。
network="${JINJI_DOTNET_NETWORK:-}"
if [[ -z "$network" ]]; then
  if docker network inspect jinji_default >/dev/null 2>&1; then
    network="jinji_default"
  else
    network="bridge"
  fi
fi

env_args=(
  -e HOME=/home/jinji
  -e NUGET_PACKAGES=/nuget/packages
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1
  -e TESTINGPLATFORM_TELEMETRY_OPTOUT=1
  -e DOTNET_NOLOGO=1
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  -e DOTNET_CLI_USE_MSBUILD_SERVER=0
  -e MSBUILDDISABLENODEREUSE=1
)

# テスト用の DB 接続情報。Compose ネットワーク内ではサービス名で直接つなぐ。
if [[ "$network" == "jinji_default" ]]; then
  env_args+=(-e "JINJI_PG_HOST=${JINJI_PG_HOST:-postgres}" -e "JINJI_PG_PORT=${JINJI_PG_PORT:-5432}")
else
  [[ -n "${JINJI_PG_HOST:-}" ]] && env_args+=(-e "JINJI_PG_HOST=$JINJI_PG_HOST")
  [[ -n "${JINJI_PG_PORT:-}" ]] && env_args+=(-e "JINJI_PG_PORT=$JINJI_PG_PORT")
fi

# DB のパスワードは deploy/compose/.env（gitignore 済み）から渡す。
# リポジトリ直下の .env（外部 API キー等）は渡さない。
compose_env="$repo_root/deploy/compose/.env"
if [[ -f "$compose_env" ]]; then
  env_args+=(--env-file "$compose_env")
fi

# 明示した環境変数だけを名前で渡す（docker run -e NAME は呼び出し側の環境から値を読む。値は引数に出ない）。
# リポジトリ直下の .env をそのまま渡すことはしない
pass_env="${JINJI_DOTNET_PASS_ENV:-}"
for name in ${pass_env//,/ }; do
  if [[ ! "$name" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
    echo "JINJI_DOTNET_PASS_ENV の名前が不正です: $name" >&2
    exit 2
  fi
  if [[ -z "${!name:-}" ]]; then
    echo "JINJI_DOTNET_PASS_ENV の $name が呼び出し側の環境にありません" >&2
    exit 2
  fi
  env_args+=(-e "$name")
done

limit_args=()
if [[ -n "${JINJI_DOTNET_MEMORY:-}" ]]; then
  limit_args=(--memory "$JINJI_DOTNET_MEMORY")
fi

tty_args=()
if [[ -t 0 && -t 1 ]]; then
  tty_args=(-it)
fi

exec docker run --rm "${tty_args[@]}" \
  --init \
  --name "jinji-dotnet-$$" \
  --user "$uid:$gid" \
  --network "$network" \
  "${limit_args[@]}" \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  -v "$repo_root:/src" \
  -v "$home_volume:/home/jinji" \
  -v "$nuget_volume:/nuget" \
  -w /src \
  "${env_args[@]}" \
  "$image" dotnet "$@"
