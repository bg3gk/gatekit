#!/usr/bin/env bash
# Builds GateKit's brand images from tools/brand/brand.html and tools/brand/repos.tsv.
#
#   tools/brand/make-brand.sh logos                     assets/brand/logo-square*.png, logo-round*.png
#   tools/brand/make-brand.sh og <id>                   assets/brand/og/<id>.png (1280x640 social card)
#   tools/brand/make-brand.sh banner <id> <demo> [out]  README banner over a demo image or GIF
#   tools/brand/make-brand.sh all                       logos and every og card
#
# <id> is the first column of repos.tsv. The page is rendered by a headless Chromium-family
# browser (Edge, Chrome or Chromium; set GATEKIT_BROWSER if it is not found); banners over a GIF
# are composed frame by frame with ffmpeg (on PATH).
#
# Game assets: all text uses the game's own UI face, Quadraat Offc Pro, and the motto is divided
# by one of the game's own marks. Both are Larian's and never committed: take them from your copy
# of the game (Game.pak) into one folder and point GATEKIT_BRAND_GAME at it:
#   Public/Game/GUI/Assets/Fonts/QuadraatOffcPro/QuadraatOffcPro-Bold.ttf, -Italic.ttf
#   the mark as PNG (GATEKIT_BRAND_MARK, default c_ico_inspirationBulletPoint: the journal's inspiration mark,
#   Public/Game/GUI/Assets/Journal_c/c_ico_inspirationBulletPoint.DDS, BC7 - decode it with e.g. Pillow)
# Without them the free Cinzel and Alegreya in tools/brand/fonts and a drawn diamond stand in.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
brand="$root/assets/brand"

native() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi; }  # Git Bash -> C:/...
url() { local p; p=$(native "$1"); case "$p" in /*) printf 'file://%s' "$p" ;; *) printf 'file:///%s' "$p" ;; esac; }
enc() { printf '%s' "$1" | sed -e 's/%/%25/g' -e 's/ /%20/g' -e 's/&/%26/g' -e 's/#/%23/g' -e "s/'/%27/g" -e 's/?/%3F/g'; }

browser=${GATEKIT_BROWSER:-}
if [ -z "$browser" ]; then
  for candidate in \
      "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
      "/c/Program Files/Google/Chrome/Application/chrome.exe" \
      "$(command -v chromium || true)" "$(command -v chromium-browser || true)" \
      "$(command -v google-chrome || true)" "$(command -v microsoft-edge || true)"; do
    if [ -n "$candidate" ] && [ -x "$candidate" ]; then browser=$candidate; break; fi
  done
fi
[ -n "$browser" ] || { echo "no Chromium-family browser found; set GATEKIT_BROWSER" >&2; exit 1; }

# render <query> <width> <height> <out.png>
render() {
  local page
  page="$(url "$here/brand.html")?logo=$(url "$brand/logo.png")&$1"
  [ -n "${GATEKIT_BRAND_GAME:-}" ] && page="$page&game=$(url "$GATEKIT_BRAND_GAME")&mark=${GATEKIT_BRAND_MARK:-c_ico_inspirationBulletPoint}"
  rm -f "$4"
  "$browser" --headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files \
    --default-background-color=00000000 --window-size="$2,$3" \
    --screenshot="$(native "$4")" "$page" >/dev/null 2>&1
  [ -s "$4" ] || { echo "the browser rendered nothing for $4" >&2; exit 1; }
}

# repo <id>: sets REPO TITLE MOTTO TAGLINE STILL from repos.tsv
repo() {
  local line
  line=$(grep -v '^#' "$here/repos.tsv" | awk -F'\t' -v id="$1" '$1 == id' | head -1)
  [ -n "$line" ] || { echo "no repository '$1' in repos.tsv" >&2; exit 1; }
  IFS=$'\t' read -r _ REPO TITLE MOTTO TAGLINE STILL <<<"$line"
  case "$STILL" in ''|/*|[A-Za-z]:*) ;; *) STILL="$root/$STILL" ;; esac
}

texts() { printf 'title=%s&motto=%s&tagline=%s&repo=%s' "$(enc "$TITLE")" "$(enc "$MOTTO")" "$(enc "$TAGLINE")" "$(enc "$REPO")"; }

# The square avatar is the emblem on the brand field; the round one is its own artwork
# (logo-circle-source.png, a medallion), cropped to its gold ring and used as it is.
logos() {
  render "mode=square" 1024 1024 "$brand/logo-square.png"
  ffmpeg -v error -y -i "$brand/logo-circle-source.png" -vf "crop=1092:1092:72:60,scale=1024:1024:flags=lanczos" "$brand/logo-round.png"
  for size in 512 256 128 64; do
    for kind in square round; do
      ffmpeg -v error -y -i "$brand/logo-$kind.png" -vf "scale=$size:$size:flags=lanczos" "$brand/logo-$kind-$size.png"
    done
  done
  echo "$brand/logo-square.png $brand/logo-round.png (+ 512, 256, 128, 64)"
}

og() {
  repo "$1"
  mkdir -p "$brand/og"
  local image=""
  [ -n "$STILL" ] && [ -f "$STILL" ] && image="&image=$(url "$STILL")"
  render "mode=og&$(texts)$image" 1280 640 "$brand/og/$1.png"
  echo "$brand/og/$1.png"
}

banner() {
  repo "$1"
  local demo=${2:?demo image or GIF} out=${3:-$brand/banner-$1.png} work
  work=$(mktemp -d)
  render "mode=banner&$(texts)" 1280 320 "$work/lockup.png"
  if [[ "$out" == *.gif ]]; then
    # one palette for the whole clip keeps the purple glow and the bronze free of banding
    ffmpeg -v error -y -ignore_loop 0 -i "$demo" -i "$work/lockup.png" -filter_complex "
      [0]scale=1280:-2:flags=lanczos,crop=1280:320[d];[d][1]overlay=format=auto:shortest=1,split[a][b];
      [a]palettegen=stats_mode=diff:max_colors=256[p];[b][p]paletteuse=dither=sierra2_4a" "$out"
  else
    ffmpeg -v error -y -i "$demo" -i "$work/lockup.png" -filter_complex "
      [0]scale=1280:-2:flags=lanczos,crop=1280:320[d];[d][1]overlay=format=auto" -frames:v 1 "$out"
  fi
  rm -rf "$work"
  echo "$out"
}

case "${1:-}" in
  logos) logos ;;
  og) og "${2:?repository id}" ;;
  banner) banner "${2:?repository id}" "${3:-}" "${4:-}" ;;
  all) logos; for id in $(grep -v '^#' "$here/repos.tsv" | cut -f1); do og "$id"; done ;;
  *) sed -n '2,16p' "$0"; exit 2 ;;
esac
