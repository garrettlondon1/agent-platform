#!/usr/bin/env bash
set -euo pipefail
request=$(cat)
labels=$(printf '%s' "$request" | jq '[.outputs[] | select(.type=="add_labels")] | length')
printf '[{"id":"triage-minutes-saved","value":%s}]\n' "$(( labels * 3 ))"
