#!/usr/bin/env bash
# The game server for the PlayMode live tests (LiveGameTests), on the local MongoDB container "pixelhero".
# Every value that could reach production is overridden here: the database (PixelHero-Data/.env points at the live
# one), the hub, the HTTP API and Discord.
#
#   Tools/live_test_server.sh            # port 59011, the Game scene's ServerUrl
#
# First time: create the container (see CLAUDE.md, "Live tests"), start this server, then run
# node Tools/seed_test_accounts.mjs once for the admin test account (.test-account.json) and unitytest1.
set -euo pipefail

cd "$(dirname "$0")/.."

if [ "$(docker inspect -f '{{.State.Running}}' pixelhero 2>/dev/null)" != "true" ]; then
    docker start pixelhero >/dev/null
fi

exec dotnet run --project Server/src/PixelHero.Server -- \
    --port 59011 \
    --debugging true \
    --skipDatabase false \
    --mongodbHost 127.0.0.1 \
    --mongodbPort 27017 \
    --mongodbUser pixelhero \
    --mongodbPassword pixelhero \
    --mongodbDatabase pixelhero \
    --mongodbAuthSource pixelhero \
    --mongodbTls false \
    --mongodbSrv false \
    --hubEnabled false \
    --apiEnabled false \
    --discordEnabled false
