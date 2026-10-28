#!/bin/sh
# Launch Lumora directly on Steam Frame: the native arm64 build when it sits here, else the x86_64 build through
# FEX. Only the arm64 build reaches the headset's VR runtime; an x86_64 process under FEX cannot load it.
# Renderer selection must happen before Godot starts; C# device detection is too late.
set -eu

lumora_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$lumora_dir"
for lumora_name in Lumora.arm64 Lumora.x86_64 LumoraVR.x86_64; do
    if [ -x "$lumora_dir/$lumora_name" ]; then
        exec "$lumora_dir/$lumora_name" --rendering-method mobile --xr-mode on "$@" -- --steam-frame --vr
    fi
done

printf '%s\n' 'Lumora executable not found. Keep Lumora-Frame.sh beside the Linux game executable.' >&2
exit 1
