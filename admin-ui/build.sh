#!/usr/bin/env sh
# Builds the admin website into src/Server/Admin/wwwroot (embedded into the server dll).
# Needs Node 18+. Run after editing anything in admin-ui/src:  npm install && npm run build
set -e
cd "$(dirname "$0")"
OUT=../src/Server/Admin/wwwroot
mkdir -p "$OUT"
npx @tailwindcss/cli -i src/input.css -o "$OUT/app.css" --minify
cp src/index.html src/app.js "$OUT/"
cp node_modules/@fontsource-variable/plus-jakarta-sans/files/plus-jakarta-sans-latin-wght-normal.woff2 "$OUT/font.woff2"
cp ../docs/images/favicon.png "$OUT/logo.png"
echo "admin ui -> $OUT"
