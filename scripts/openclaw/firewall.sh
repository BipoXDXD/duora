#!/bin/sh
# Dedicated chains only. Never flush DOCKER-USER or Duora backup rules.
set -eu
IPT=/usr/sbin/iptables
SUBNET=172.30.40.0/24
# Install fail-closed guards while replacing only our chains.
$IPT -w -I DOCKER-USER 1 -s "$SUBNET" -j DROP
$IPT -w -I INPUT 1 -s "$SUBNET" -j DROP
$IPT -w -N DUORA-AGENT-OUT 2>/dev/null || $IPT -w -S DUORA-AGENT-OUT >/dev/null
$IPT -w -F DUORA-AGENT-OUT
# Reject private, loopback, link-local (including metadata), multicast and reserved networks.
for net in 0.0.0.0/8 10.0.0.0/8 100.64.0.0/10 127.0.0.0/8 169.254.0.0/16 172.16.0.0/12 192.168.0.0/16 224.0.0.0/4 240.0.0.0/4; do
  $IPT -w -A DUORA-AGENT-OUT -d "$net" -j REJECT
done
$IPT -w -A DUORA-AGENT-OUT -p tcp --dport 443 -j ACCEPT
$IPT -w -A DUORA-AGENT-OUT -j REJECT
$IPT -w -C DOCKER-USER -s "$SUBNET" -j DUORA-AGENT-OUT 2>/dev/null || $IPT -w -A DOCKER-USER -s "$SUBNET" -j DUORA-AGENT-OUT
# Existing RETURN rules could precede our jump: insert at position 2, behind temporary guard.
while $IPT -w -D DOCKER-USER -s "$SUBNET" -j DUORA-AGENT-OUT 2>/dev/null; do :; done
$IPT -w -I DOCKER-USER 2 -s "$SUBNET" -j DUORA-AGENT-OUT
# INPUT guard intentionally retained: agent cannot access any host listener, even public IP.
# Remove duplicate host guards without creating an unguarded interval.
while [ "$($IPT -S INPUT | sed -n '/^-A INPUT -s 172.30.40.0\/24 -j DROP$/p' | wc -l)" -gt 1 ]; do
  $IPT -w -D INPUT -s "$SUBNET" -j DROP
done
while $IPT -w -D DOCKER-USER -s "$SUBNET" -j DROP 2>/dev/null; do :; done
