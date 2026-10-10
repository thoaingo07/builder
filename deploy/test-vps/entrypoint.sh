#!/bin/sh
set -e
: "${AUTHORIZED_KEY:?set AUTHORIZED_KEY to the public key deploys use}"
mkdir -p /root/.ssh && chmod 700 /root/.ssh
printf '%s\n' "$AUTHORIZED_KEY" > /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys
# optional: also allow root to log in with a password (tests of password-based environments)
if [ -n "${ROOT_PASSWORD:-}" ]; then
  printf 'root:%s\n' "$ROOT_PASSWORD" | chpasswd
  sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication yes/; s/^#\?PermitRootLogin.*/PermitRootLogin yes/' /etc/ssh/sshd_config
fi
ssh-keygen -A >/dev/null
exec /usr/sbin/sshd -D -e
