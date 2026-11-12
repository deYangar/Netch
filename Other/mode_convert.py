# -*- coding: utf-8 -*-
"""M2-4 内置模式库 JSON 化：进程模式 txt 全量转 JSON（一次性工具）。

- 按 ModeHelper.ReadTxtMode 语义解析（meta 行 `# Remark, type`；`!` 前缀 Bypass；#include 展开）
- 平台级模式附带下载 CDN 直连规则（域名 2026-10-26 DNS 存活验证 + geosite 归类核对，宁少勿错）
- 输出与 M2 第一批 Steam.json 同构（camelCase，ProcessMode/TunMode 字符串 type）
"""
import json
import os
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "Storage", "mode")

# 平台级 CDN 直连规则（Direct=1）：仅收录 DNS 存活验证过的下载域；商店/登录域走默认代理
PLATFORM_RULES = {
    "EA Desktop": {"origin-a.akamaihd.net"},
    "EA Desktop Games": {"origin-a.akamaihd.net"},
    "EA Desktop Games (with EA Desktop)": {"origin-a.akamaihd.net"},
    "Origin": {"origin-a.akamaihd.net"},
    "Origin Games": {"origin-a.akamaihd.net"},
    "Discord": {"cdn.discordapp.com", "discordapp.net"},
    "Discord (with Steam Apps)": {"cdn.discordapp.com", "discordapp.net"},
    "Battle": {"dist.blizzard.com", "blzddist1-a.akamaihd.net", "level3.blizzard.com", "cdn.blizzard.com"},
}


def read_txt_mode(path):
    """按 ReadTxtMode 语义解析，返回 (type_num, remark, handle, bypass)"""
    with open(path, encoding="utf-8-sig") as f:
        lines = f.read().splitlines()

    assert lines and lines[0].startswith("#"), f"{path}: no meta line"
    heads = [h.strip() for h in lines[0][1:].split(",")]
    remark = heads[0]
    type_num = heads[1] if len(heads) > 1 and heads[1] else "0"

    handle, bypass = [], []
    for line in lines[1:]:
        line = line.strip()
        if not line or line.startswith("//"):
            continue
        if line.startswith("#include"):
            rel = line[len("#include "):].replace("<", "").replace(">", "").replace(".h", ".txt").strip()
            inc_type, _, inc_handle, inc_bypass = read_txt_mode(os.path.join(ROOT, rel))
            handle.extend(inc_handle)
            bypass.extend(inc_bypass)
        elif line.startswith("!"):
            bypass.append(line)
        else:
            handle.append(line)

    return type_num, remark, handle, bypass


def convert(path):
    type_num, remark, handle, bypass = read_txt_mode(path)
    mode = {
        "type": "ProcessMode" if type_num == "0" else "TunMode",
        "remark": {"en": remark},
        "handle": handle,
        "bypass": bypass,
    }
    if type_num == "0" and remark in PLATFORM_RULES:
        mode["rules"] = [{"domainSuffix": sorted(PLATFORM_RULES[remark]), "action": 1}]
    return mode, type_num


def main():
    targets = []
    for sub in ["", "Game", "Other"]:
        d = os.path.join(ROOT, sub)
        for f in sorted(os.listdir(d)):
            if f.endswith(".txt"):
                targets.append((sub, f))

    plan = []
    skipped = []
    for sub, f in targets:
        path = os.path.join(ROOT, sub, f)
        mode, type_num = convert(path)
        if type_num == "6":
            # ShareMode（pcap2socks 预设）：字段模型不同（Argument），不在本批范围
            skipped.append(os.path.join(sub, f))
            continue
        out = path[:-4] + ".json"
        with open(out, "w", encoding="utf-8", newline="\n") as fp:
            json.dump(mode, fp, ensure_ascii=False, indent=2)
            fp.write("\n")
        rules = len(mode.get("rules", []))
        plan.append((sub, f, type_num, len(mode["handle"]), rules))

    for sub, f, t, h, r in plan:
        kind = "TUN " if t != "0" else "proc"
        print(f"{kind} type={t} handle={h:3d} rules={r}  {os.path.join(sub, f)}")
    print(f"\nconverted: {len(plan)}, skipped(ShareMode): {skipped}")


if __name__ == "__main__":
    main()
