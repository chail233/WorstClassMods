# -*- coding: utf-8 -*-
"""修复 mdl2obj.py 源码中误写入的真实控制字节 → 转义序列文本"""
path = "Tools/mdl2obj.py"
raw = open(path, "rb").read()
# 真实字节 → 源码转义文本
replacements = [
    (b"\x00", b"\\x00"),
    (b"\x89PNG\r\n\x1a\n", b"\\x89PNG\\r\\n\\x1a\\n"),
]
for pat, rep in replacements:
    cnt = raw.count(pat)
    if cnt:
        raw = raw.replace(pat, rep)
        print("替换 %d 处: %r" % (cnt, pat))
open(path, "wb").write(raw)
print("剩余 null 字节:", raw.count(b"\x00"))
