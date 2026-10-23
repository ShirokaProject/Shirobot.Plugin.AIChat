#!/bin/sh
set -eu
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
host_source=${SHIROBOT_DEBUG_HOST_SOURCE:-"$repo_root/../AIChat-debug-host"}
adapter_source=${SHIROBOT_DEBUG_ADAPTER_SOURCE:-"$repo_root/../AIChat-debug-adapter"}
dotnet publish "$host_source/Core/ShiroBot.csproj" -c Debug -o "$repo_root/.debug-runtime" -p:PublishSingleFile=false
dotnet build "$adapter_source/ShiroBot.Adapter.Debug.csproj" -c Release -p:ShiroBotSourceRoot="$host_source"
mkdir -p "$repo_root/.debug-runtime/adapters/debug"
cp "$adapter_source/bin/Release/net10.0/ShiroBot.Adapter.Debug.dll" "$repo_root/.debug-runtime/adapters/debug/"
dotnet build "$repo_root/ShiroBot.AiChatPlugin.csproj" -c Debug -p:UseLegacyDebugHost=true
