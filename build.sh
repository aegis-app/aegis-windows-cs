#!/usr/bin/env bash
# ========================================
# 🔨 AEGIS Windows版 一括ビルド (テスト → 単一ファイル exe publish)
# ========================================
# 使い方: ./build.sh
# 成果物: out/AEGIS.exe (Windows x64、自己完結・圧縮つき)
# ※ exe の実行には Windows が必要です (このスクリプトはビルドのみ)
set -euo pipefail
cd "$(dirname "$0")"

# 🧰 dotnet の解決: PATH → このワークスペースの SDK の順
if command -v dotnet >/dev/null 2>&1; then
  DOTNET=dotnet
elif [ -x "$HOME/workspace/tools/dotnet/dotnet" ]; then
  DOTNET="$HOME/workspace/tools/dotnet/dotnet"
else
  echo "dotnet が見つかりません (.NET SDK 8 が必要です)" >&2
  exit 1
fi

# 🗂️ /tmp が小さい環境向けに TMPDIR をワークスペースへ逃がす
export TMPDIR="${TMPDIR:-$HOME/workspace/tmp}"
mkdir -p "$TMPDIR"

# 📦 NuGet のローカルソースがあれば使う (プロキシ環境での restore ハング回避)
RESTORE_ARGS=()
if [ -d "$HOME/workspace/tools/nuget-local" ]; then
  RESTORE_ARGS=(-p:RestoreSources="$HOME/workspace/tools/nuget-local")
fi

echo "=== 🧪 テスト実行 ==="
"$DOTNET" run --project src/AegisTest/AegisTest.csproj -c Release "${RESTORE_ARGS[@]}"

echo "=== 🪟 exe を publish ==="
rm -rf out
"$DOTNET" publish src/AegisWin/AegisWin.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  "${RESTORE_ARGS[@]}" -o out

echo "=== ✅ 完了: out/AEGIS.exe ==="
ls -lh out/AEGIS.exe
