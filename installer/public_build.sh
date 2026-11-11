#!/bin/bash
# Build Coworker for Windows from a Mac, on GitHub's free runners for public repositories.
#
# The HumanlikeAI repository is private and its Actions budget can be used up; a public repository builds for free.
# This copies only this folder (Humanlike-apps/coworker-windows, no history, no docs/) to the public repository
# mmkontis/coworker-windows, waits for its "Coworker Windows build" run and downloads the installer it made.
#
#   installer/public_build.sh                 # push to the branch "main" there, wait, download
#   installer/public_build.sh try-something   # another branch, for a build you are not ready to keep
#   NO_WAIT=1 installer/public_build.sh       # push only; prints the run to watch
#
# Nothing secret leaves this Mac: the copy is refused when a file looks like it holds a key, the workflow uses
# no secrets, and the update signature is made here afterwards (installer/sign_update_mac.sh).
set -euo pipefail
APP_DIR=$(cd "$(dirname "$0")/.." && pwd)
REPO="${COWORKER_WINDOWS_PUBLIC_REPO:-mmkontis/coworker-windows}"
BRANCH="${1:-main}"
CLONE="${COWORKER_WINDOWS_PUBLIC_CLONE:-$HOME/Library/Caches/coworker-windows-public}"
OUT="${COWORKER_WINDOWS_OUT:-$APP_DIR/installer/release}"

cd "$APP_DIR"
# Tracked files plus new ones that are not ignored, so a build can be tried before it is committed.
FILES=$(mktemp)
trap 'rm -f "$FILES"' EXIT
git ls-files -co --exclude-standard | grep -v '^docs/' > "$FILES"

# Refuse to publish anything that looks like a credential.
PATTERN='(sk-[A-Za-z0-9_-]{20,}|sk_live_|sk_test_|pk_live_|AIza[0-9A-Za-z_-]{30,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY|GOCSPX-[A-Za-z0-9_-]{20,})'
# (This script holds the pattern itself, so it is not scanned.)
if grep -v '^installer/public_build.sh$' "$FILES" | tr '\n' '\0' | xargs -0 grep -nIE "$PATTERN" 2>/dev/null | cut -c1-120 | grep . ; then
  echo 'These lines look like credentials. Nothing was published.' >&2; exit 1
fi
if grep -E '(^|/)(\.env[^/]*|[^/]*\.(key|pfx|p12|pem))$' "$FILES"; then
  echo 'These files look like key material. Nothing was published.' >&2; exit 1
fi

if [[ ! -d "$CLONE/.git" ]]; then
  rm -rf "$CLONE"; mkdir -p "$(dirname "$CLONE")"
  git clone --quiet "https://github.com/$REPO.git" "$CLONE"
fi
git -C "$CLONE" fetch --quiet origin
git -C "$CLONE" reset --quiet --hard
git -C "$CLONE" clean -qfdx
if git -C "$CLONE" rev-parse --verify --quiet "origin/$BRANCH" >/dev/null; then
  git -C "$CLONE" checkout --quiet -B "$BRANCH" "origin/$BRANCH"
elif git -C "$CLONE" rev-parse --verify --quiet origin/main >/dev/null; then
  git -C "$CLONE" checkout --quiet -B "$BRANCH" origin/main
else
  git -C "$CLONE" checkout --quiet --orphan "$BRANCH" 2>/dev/null || true
fi
# The clone becomes exactly the list above.
find "$CLONE" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
rsync -a --files-from="$FILES" "$APP_DIR/" "$CLONE/"

VERSION=$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' Coworker.csproj | head -1)
BUILD=$(sed -n 's:.*<AppBuild>\(.*\)</AppBuild>.*:\1:p' Coworker.csproj | head -1)
SOURCE=$(git rev-parse --short HEAD)
[[ -z "$(git status --porcelain -- .)" ]] || SOURCE="$SOURCE plus local changes"
git -C "$CLONE" add -A
if git -C "$CLONE" diff --cached --quiet; then
  echo "Nothing changed since the last push to $REPO ($BRANCH)."
else
  git -C "$CLONE" -c user.name="$(git config user.name)" -c user.email="$(git config user.email)" commit --quiet \
    -m "Coworker for Windows $VERSION (build $BUILD)" -m "Source: HumanlikeAI $SOURCE" \
    -m "Co-Authored-By: Claude <noreply@anthropic.com>"
  git -C "$CLONE" push --quiet origin "$BRANCH"
fi
SHA=$(git -C "$CLONE" rev-parse HEAD)
echo "Pushed $SHA to https://github.com/$REPO/tree/$BRANCH"

# The run appears a few seconds after the push.
RUN=""
for _ in $(seq 1 30); do
  RUN=$(gh run list --repo "$REPO" --commit "$SHA" --json databaseId -q '.[0].databaseId' 2>/dev/null || true)
  [[ -n "$RUN" ]] && break
  sleep 4
done
[[ -n "$RUN" ]] || { echo "No run started for $SHA. Look at https://github.com/$REPO/actions" >&2; exit 1; }
echo "Run: https://github.com/$REPO/actions/runs/$RUN"
[[ "${NO_WAIT:-0}" == 1 ]] && exit 0

if ! gh run watch "$RUN" --repo "$REPO" --exit-status --interval 20 >/dev/null; then
  gh run view "$RUN" --repo "$REPO" --log-failed | tail -80
  echo "The build failed: https://github.com/$REPO/actions/runs/$RUN" >&2; exit 1
fi
mkdir -p "$OUT"
rm -f "$OUT"/Coworker-*-win-x64.exe "$OUT"/Coworker-*-win-x64.exe.sha256
gh run download "$RUN" --repo "$REPO" --name coworker-windows-installer --dir "$OUT"
( cd "$OUT" && shasum -a 256 -c Coworker-*-win-x64.exe.sha256 )
ls -lh "$OUT"/Coworker-*-win-x64.exe
