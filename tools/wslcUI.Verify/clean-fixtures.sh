#!/bin/sh
# P4-R2 spike: strip util-linux `script` noise (header/trailer lines) from the
# captured .vt fixtures. Those lines wrap past 80 columns and pollute the
# cell grid (LF keeps column after the wrap), and the trailer lands wherever
# the cursor happens to be.
cd /mnt/host/d/vibercodeing/wslcUI/tests/wslcUI.Tests/TestData || exit 1
for f in ls.vt grep.vt vi.vt top.vt; do
    sed -i '/^Script \(started\|done\)/d' "$f"
    # 去掉因此留下的首尾空行（头部一个换行 + 尾部空行）
    sed -i '/./,$!d' "$f"
    printf '%s: %s bytes\n' "$f" "$(wc -c < "$f")"
done
