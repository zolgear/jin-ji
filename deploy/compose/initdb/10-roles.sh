#!/bin/bash
# PostgreSQL の初回起動時（データディレクトリが空のとき）にだけ実行される。
# - jinji_owner: マイグレーションを実行し、テーブルを所有する
# - jinji_app:   アプリが接続する。テーブル所有者にしない（所有者は RLS をすり抜けるため）
# テーブルへの権限付与はマイグレーション（db/migrations）で行う。
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v owner_password="$JINJI_PG_OWNER_PASSWORD" \
  -v app_password="$JINJI_PG_APP_PASSWORD" <<'SQL'
CREATE ROLE jinji_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
  PASSWORD :'owner_password';
CREATE ROLE jinji_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT
  PASSWORD :'app_password';

CREATE DATABASE jinji OWNER jinji_owner;
REVOKE ALL ON DATABASE jinji FROM PUBLIC;
GRANT CONNECT ON DATABASE jinji TO jinji_app;
SQL
