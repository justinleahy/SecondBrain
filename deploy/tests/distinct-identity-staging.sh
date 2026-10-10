#!/usr/bin/env bash
# Run as root in CI or in an isolated SDK container; never changes host accounts.
set -euo pipefail
test "$(id -u)" = 0
qualification_root=$(mktemp -d /tmp/sb-distinct-identity.XXXXXX)
qualification_writer=
cleanup() {
    if [ -n "$qualification_writer" ]; then kill "$qualification_writer" 2>/dev/null || true; wait "$qualification_writer" 2>/dev/null || true; fi
    rm -rf "$qualification_root"
}
trap cleanup EXIT
chmod 0755 "$qualification_root"
mkdir "$qualification_root"/{incoming,stage,daemon-home,results,control}
mkdir -p "$qualification_root/incoming/inbox/.claimed"
chown 1656:1654 "$qualification_root/incoming"
chmod 0750 "$qualification_root/incoming"
chown 1654:1654 "$qualification_root/"{stage,daemon-home,results}
chmod 0700 "$qualification_root/stage"
chown 1654:1654 "$qualification_root/incoming/inbox" "$qualification_root/incoming/inbox/.claimed" "$qualification_root/control"
chmod 1770 "$qualification_root/incoming/inbox"
chmod 0700 "$qualification_root/incoming/inbox/.claimed"
touch "$qualification_root/control/done"
chown 1654:1654 "$qualification_root/control/done"
chmod 0660 "$qualification_root/control/done"
printf 'outside boundary marker' > "$qualification_root/outside.md"
chmod 0666 "$qualification_root/outside.md"
setpriv --reuid 1656 --regid 1654 --clear-groups --no-new-privs sh -c '
    ln -s "$1/outside.md" "$1/incoming/symlink.md"
    ln "$1/outside.md" "$1/incoming/hardlink.md"
    printf "safe inbox bytes" > "$1/incoming/inbox/safe.txt"
    if mv "$1/incoming/inbox/.claimed" "$1/incoming/inbox/claimed-substitution" 2>/dev/null; then
        printf "claim substitution succeeded" > "$1/control/done"; exit 1
    fi
    while :; do
        if test -e "$1/control/replace-inbox" && test ! -e "$1/incoming/original-inbox"; then
            mv "$1/incoming/inbox" "$1/incoming/original-inbox"
            mkdir "$1/incoming/inbox"
            printf "outside boundary marker" > "$1/incoming/inbox/safe.txt"
            printf "replacement complete" > "$1/control/done"
        fi
        printf "safe sync bytes" > "$1/incoming/replacement"
        mv -f "$1/incoming/replacement" "$1/incoming/racing.md"
        sleep .002
        ln -s "$1/outside.md" "$1/incoming/replacement"
        mv -f "$1/incoming/replacement" "$1/incoming/racing.md"
        sleep .002
    done' fixture "$qualification_root" &
qualification_writer=$!
sleep .1
# The hard-link target is deliberately linkable by the sync UID while remaining
# outside the registered root; a multi-link inode must never be admitted.
setpriv --reuid 1654 --regid 1654 --clear-groups --no-new-privs env \
    DOTNET_CLI_HOME="$qualification_root/daemon-home" \
    SECONDBRAIN_STAGING_QUALIFICATION_ROOT="$qualification_root" \
    dotnet vstest "${1:-tests/SecondBrain.Extractor.Tests/bin/Release/net10.0/SecondBrain.Extractor.Tests.dll}" \
    --TestCaseFilter:FullyQualifiedName~DistinctIdentityStagingTests --logger:console\;verbosity=normal \
    --ResultsDirectory:"$qualification_root/results"
setpriv --reuid 1656 --regid 1654 --clear-groups --no-new-privs sh -c 'test ! -r "$1/stage" && test ! -x "$1/stage"' fixture "$qualification_root"
printf 'Distinct daemon UID1654 / sync UID1656 staging, hard links, symlink races and private-stage denial passed.\n'
