#!/bin/sh
set -e
: "${AUTHORIZED_KEY:?set AUTHORIZED_KEY to the public key deploys use}"
mkdir -p /root/.ssh && chmod 700 /root/.ssh
printf '%s\n' "$AUTHORIZED_KEY" > /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys
ssh-keygen -A >/dev/null
exec /usr/sbin/sshd -D -e
