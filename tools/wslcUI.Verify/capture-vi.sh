#!/bin/sh
# P4-R2 spike: capture a stable initial vi screen (stdin never EOFs -> vi
# stays on the opening view until timeout kills it).
cd /tmp/vtcap || exit 1
timeout 8 sh -c 'tail -f /dev/null | script -qec "stty cols 80 rows 30; vi /tmp/vtcap/hello.txt" vi.vt'
echo "rc=$?"
wc -c vi.vt
cp vi.vt /mnt/host/d/vibercodeing/wslcUI/tests/wslcUI.Tests/TestData/
