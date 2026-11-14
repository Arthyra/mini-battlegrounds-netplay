#!/bin/sh
# Prune old per-version update directories on the ECS update mirror.
#
# Every release's ZIP also lives in GitHub Releases, which is the archive
# of record; the ECS mirror only needs the current version (latest.json)
# plus a small rollback window. Run as root on midnight:
#   sh prune-update-mirror.sh [KEEP]
# KEEP = how many most-recent version directories to keep (default 2).
set -e
BASE=/var/www/mbg-update
KEEP=${1:-2}

current=$(python3 -c "import json;print(json.load(open('$BASE/latest.json'))['version'])")
echo "current manifest version: $current"

kept=0
for dir in $(ls -1d "$BASE"/v* 2>/dev/null | sort -rV); do
    name=$(basename "$dir")
    if [ "$name" = "v$current" ] || [ "$KEEP" -gt "$kept" ]; then
        # always keep the live version, then the most recent KEEP dirs
        if [ "$name" = "v$current" ]; then
            echo "keep (live): $name"
        else
            kept=$((kept + 1))
            echo "keep (window): $name"
        fi
        continue
    fi
    echo "prune: $dir ($(du -sh "$dir" | cut -f1))"
    rm -rf "$dir"
done
echo "--- result:"
du -sh "$BASE"
ls -1 "$BASE"
