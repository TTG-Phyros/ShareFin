#!/usr/bin/env bash
# Adds (or replaces) one version of the plugin at the top of a Jellyfin repository manifest.
# Usage: update-manifest.sh <manifest.json> <meta.json> <version> <zip url> <md5> <timestamp> <changelog file>
set -euo pipefail

manifest=$1 meta=$2 version=$3 url=$4 md5=$5 timestamp=$6 changelog=$7

[ -f "$manifest" ] || echo '[]' > "$manifest"

jq --slurpfile meta "$meta" --arg v "$version" --arg url "$url" --arg md5 "$md5" --arg ts "$timestamp" --rawfile cl "$changelog" '
  $meta[0] as $m
  | (map(select(.guid == $m.guid))[0].versions // [] | map(select(.version != $v))) as $older
  | map(select(.guid != $m.guid)) + [{
      guid: $m.guid,
      name: $m.name,
      description: $m.description,
      overview: $m.overview,
      owner: $m.owner,
      category: $m.category,
      imageUrl: ($m.imagePath // ""),
      versions: ([{
        version: $v,
        changelog: ($cl | rtrimstr("\n")),
        targetAbi: $m.targetAbi,
        sourceUrl: $url,
        checksum: $md5,
        timestamp: $ts
      }] + $older)
    }]
' "$manifest" > "$manifest.tmp"
mv "$manifest.tmp" "$manifest"
