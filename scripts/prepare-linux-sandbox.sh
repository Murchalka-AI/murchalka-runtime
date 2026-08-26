#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Linux ]]; then
  echo "Linux sandbox preparation is only supported on Linux." >&2
  exit 2
fi

sudo_command=()
if [[ "$(id -u)" -ne 0 ]]; then
  sudo_command=(sudo)
fi

"${sudo_command[@]}" apt-get update
"${sudo_command[@]}" apt-get install -y --no-install-recommends \
  apparmor-profiles \
  apparmor-utils \
  bubblewrap

userns_restriction=/proc/sys/kernel/apparmor_restrict_unprivileged_userns
if [[ -r "${userns_restriction}" && "$(<"${userns_restriction}")" == 1 ]]; then
  profile_source=/usr/share/apparmor/extra-profiles/bwrap-userns-restrict
  profile_target=/etc/apparmor.d/bwrap-userns-restrict
  if [[ ! -f "${profile_source}" ]]; then
    echo "The restricted Bubblewrap AppArmor profile is unavailable." >&2
    exit 1
  fi
  "${sudo_command[@]}" install -m 0644 "${profile_source}" "${profile_target}"
  "${sudo_command[@]}" apparmor_parser --replace "${profile_target}"
fi

sandbox_root="$(mktemp -d)"
trap 'rm -rf -- "${sandbox_root}"' EXIT
mkdir -p "${sandbox_root}/content"
printf '%s\n' ready > "${sandbox_root}/content/probe"

arguments=(
  --die-with-parent
  --new-session
  --unshare-all
  --tmpfs /tmp
  --ro-bind "${sandbox_root}/content" "${sandbox_root}/content"
  --ro-bind /usr /usr
  --proc /proc
  --dev /dev
)
for library_path in /lib /lib64; do
  if [[ -e "${library_path}" ]]; then
    arguments+=(--ro-bind "${library_path}" "${library_path}")
  fi
done
arguments+=(-- /usr/bin/test -r "${sandbox_root}/content/probe")

/usr/bin/bwrap "${arguments[@]}"
echo "Bubblewrap sandbox self-test passed."
