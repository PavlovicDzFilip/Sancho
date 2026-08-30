#!/usr/bin/env bash
# Publishes Sancho for all supported platforms (framework-dependent single-file).
# Output: artifacts/publish/<rid>/ per RID, plus release-ready assets staged in
# artifacts/release/ named per convention: sancho.exe (Windows), sancho-<os>-<arch>.
# Requires only the .NET 10 SDK - no AOT, no native toolchain.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rids=(win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64)

asset_name() {
    local rid="$1"
    if [ "$rid" = "win-x64" ]; then
        echo "sancho.exe"
    else
        echo "sancho-${rid%%-*}-${rid##*-}"
    fi
}

release_dir="$root/artifacts/release"
mkdir -p "$release_dir"

for rid in "${rids[@]}"; do
    echo "Publishing $rid..."
    exe_name="sancho"
    [ "$rid" = "win-x64" ] && exe_name="sancho.exe"

    dotnet publish "$root/Sancho.Console/Sancho.Console.csproj" \
        -c Release \
        -r "$rid" \
        --self-contained false \
        -p:PublishSingleFile=true \
        -p:InvariantGlobalization=true \
        -o "$root/artifacts/publish/$rid"

    rm -f "$root"/artifacts/publish/"$rid"/*.pdb
    cp "$root/artifacts/publish/$rid/$exe_name" "$release_dir/$(asset_name "$rid")"
    cp "$root/artifacts/publish/$rid/prompt.md" "$release_dir/prompt.md"
done

echo ""
echo "Published. Release assets staged in $release_dir:"
ls -1 "$release_dir"
