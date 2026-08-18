#!/usr/bin/env bash
# Печатает 32 шестнадцатеричных символа (128 бит) из /dev/urandom — общий токен,
# которым нужно заполнить поле "token" в конфиге и Mac, и Windows-агента,
# чтобы обе стороны узнавали друг друга.
set -euo pipefail

xxd -l 16 -p /dev/urandom | tr -d '\n'
echo
