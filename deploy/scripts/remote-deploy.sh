#!/usr/bin/env bash
set -euo pipefail

: "${RELEASE_ID:?RELEASE_ID not set}"
: "${RELEASE_ROOT:?RELEASE_ROOT not set}"
: "${CURRENT_LINK:?CURRENT_LINK not set}"
: "${SERVICE_NAME:?SERVICE_NAME not set}"
: "${HEALTH_ENDPOINT:?HEALTH_ENDPOINT not set}"
DRY_RUN="${DRY_RUN:-true}"

REMOTE_PATH="${RELEASE_ROOT%/}/${RELEASE_ID}"

if [ "$DRY_RUN" = "true" ]; then
    echo "[dry-run] would install unit from ${REMOTE_PATH}/systemd/${SERVICE_NAME}"
    echo "[dry-run] would update symlink ${CURRENT_LINK} -> ${REMOTE_PATH}"
    echo "[dry-run] would restart ${SERVICE_NAME}"
    echo "[dry-run] would verify ${HEALTH_ENDPOINT}"
    exit 0
fi

if [ ! -d "$REMOTE_PATH" ]; then
    echo "Expected release directory $REMOTE_PATH missing" >&2
    exit 1
fi

sudo cp "${REMOTE_PATH}/systemd/${SERVICE_NAME}" "/etc/systemd/system/${SERVICE_NAME}"
sudo systemctl daemon-reload
sudo ln -sfn "$REMOTE_PATH" "$CURRENT_LINK"
sudo systemctl restart "$SERVICE_NAME"

for attempt in $(seq 1 5); do
    if response=$(curl -fsS "$HEALTH_ENDPOINT"); then
        echo "Health check response: $response"
        exit 0
    fi
    echo "Health check not ready (attempt $attempt). Retrying..."
    sleep 5
done

echo "Health check failed for $HEALTH_ENDPOINT" >&2
exit 1

