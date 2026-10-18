#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
messy-corp: 「整っていない」日本企業の人事データ（完全な架空データ）を生成する。

  python3 generate.py

標準ライブラリのみ。乱数は固定シード。同じ出力先に全ファイルを再生成する。
人物・企業・メールドメインはすべて架空（example.co.jp / example.com のみ）。
Shift_JIS 系ファイルは Windows-31J（cp932）で出力する（髙・﨑 等を含むため）。
"""
import csv
import collections
import datetime as dt
import io
import json
import os
import random
import re
import unicodedata
import uuid

OUT = os.path.dirname(os.path.abspath(__file__))
rng = random.Random(20260412)
ASOF = dt.date(2026, 10, 1)  # 人事パッケージ・Entra の基準日

# ---------------------------------------------------------------- 文字ユーティリティ
_H2F = {}
for _cp in range(0xFF61, 0xFFA0):
    _h = chr(_cp)
    _H2F[unicodedata.normalize("NFKC", _h)] = _h


def to_half_kana(s):
    out = []
    for ch in s:
        if ch in _H2F:
            out.append(_H2F[ch])
            continue
        d = unicodedata.normalize("NFD", ch)
        if len(d) == 2 and d[0] in _H2F and d[1] in ("゙", "゚"):
            out.append(_H2F[d[0]] + ("ﾞ" if d[1] == "゙" else "ﾟ"))
            continue
        out.append(" " if ch == "　" else ch)
    return "".join(out)


FULL_DIGIT = {ord("0") + i: ord("０") + i for i in range(10)}


def to_full_digits(s):
    return s.translate(FULL_DIGIT)


def cp932_safe(s):
    try:
        s.encode("cp932")
        return s, False
    except UnicodeEncodeError:
        res = []
        for c in s:
            try:
                c.encode("cp932")
                res.append(c)
            except UnicodeEncodeError:
                res.append("?")
        return "".join(res), True


def wareki(d):
    if d >= dt.date(2019, 5, 1):
        return "令和", "R", d.year - 2018
    if d >= dt.date(1989, 1, 8):
        return "平成", "H", d.year - 1988
    return "昭和", "S", d.year - 1925


def fmt_date(d, style):
    """0=ISO(正規形) 1=2024/4/1 2=R6.4.1 3=令和6年4月1日 4=2026/04/01"""
    if style == 0:
        return d.isoformat()
    if style == 1:
        return f"{d.year}/{d.month}/{d.day}"
    if style == 4:
        return f"{d.year}/{d.month:02d}/{d.day:02d}"
    g, a, y = wareki(d)
    if style == 2:
        return f"{a}{y}.{d.month}.{d.day}"
    return f"{g}{'元' if y == 1 else y}年{d.month}月{d.day}日"


def wchoice(items, weights):
    return rng.choices(items, weights=weights, k=1)[0]


# ---------------------------------------------------------------- 異常の記録
CATS = {
    # 組織図
    "ORG_MERGED_BLANK": ("org_chart", "結合セル由来の空欄（上位組織名が上の行にしか無い）"),
    "ORG_NAME_VARIANT": ("全ファイル", "組織名の表記ゆれ（営業第一部/営業１部/営業1部 など）"),
    "ORG_WIDTH_MIX": ("全ファイル", "全角/半角の混在（ﾁｰﾑ、ＳＥ、全角数字の人数・組織コード）"),
    "ORG_TRAILING_SPACE": ("全ファイル", "組織名・氏名等の末尾空白（半角/全角）"),
    "ORG_ABOLISHED_REMAINS": ("org_chart", "廃止済み組織が残っている（備考で明示 / 無表示の両方）"),
    "ORG_NO_CODE": ("org_chart", "組織コードが無い組織"),
    "ORG_LEVEL_SKIP": ("org_chart", "段飛ばし（課を飛ばしてチーム、本部を飛ばして部 など）"),
    "ORG_STALE_HEAD": ("org_chart", "責任者欄が退職者のまま"),
    # 人事パッケージ
    "NAME_KANJI_VARIANT": ("全ファイル", "氏名の異体字（髙橋/高橋、﨑/崎、齋藤/斉藤/齊藤 など）"),
    "KANA_HALFWIDTH": ("hr_package", "フリガナの半角カナ"),
    "MAIDEN_NAME": ("hr/jirei/entra", "旧姓（通称）使用者。戸籍名と業務上の氏名の食い違い"),
    "EMAIL_CASE": ("hr_package", "メールアドレスの大文字小文字ゆれ"),
    "EMPNO_ZEROPAD": ("全ファイル", "社員番号のゼロ埋め有無（00123 と 123）"),
    "DATE_FORMAT_MIX": ("全ファイル", "日付形式の混在（ISO 以外: 2024/4/1、R6.4.1、令和6年4月1日、2026/04/01）"),
    "HR_STALE_ORG": ("hr_package", "人事パッケージの所属が 4/1 発令前のまま（辞令と食い違い）"),
    "COMPANY_NAME_VARIANT": ("hr/entra", "会社名の略記・表記ゆれ"),
    "SAME_NAME_PAIR": ("全ファイル", "同姓同名の別人（2 組）"),
    # 辞令
    "JIREI_EMPNO_TYPO": ("jirei", "社員番号の誤記（氏名で照合して補正する必要あり）"),
    "JIREI_EMPNO_MISSING": ("jirei", "社員番号が無く氏名のみ"),
    "JIREI_AMBIGUOUS_ORG": ("jirei", "「営業第一部 課長」のように所属が一意に定まらない自由記述"),
    "JIREI_FUTURE_DATED": ("jirei", "未来日付の辞令（内示相当）。現在の所属に反映してはならない"),
    # Entra
    "ENTRA_RETIRED_ENABLED": ("entra", "退職者なのに accountEnabled=true"),
    "ENTRA_STALE_DEPT": ("entra", "department が廃止済みの旧組織名のまま"),
    "ENTRA_EMPLOYEEID_EMPTY": ("entra", "employeeId が空"),
    "ENTRA_NAME_REVERSED": ("entra", "displayName が姓名逆順（givenName/surname は正しい or 入れ替わり）"),
    "ENTRA_NAME_ROMAJI": ("entra", "displayName がローマ字"),
    "ENTRA_NON_PERSON": ("entra", "人ではないアカウント（共有メールボックス・サービスアカウント・会議室）"),
    "ENTRA_MISSING_ACCOUNT": ("entra", "人事パッケージに在籍するが Entra にアカウントが無い（行なし）"),
    "SITE_NAME_VARIANT": ("hr_package", "勤務地（拠点名）の表記ゆれ（本社（東京）/東京 本社/半角カナ/末尾空白など）"),
    "SITE_UNKNOWN": ("hr_package", "拠点マスタに無い勤務地（修正依頼にする）"),
    "REMOTE_FLAG_VARIANT": ("hr_package", "リモートワークの有無の表記ゆれ（可/はい/有/対象/いいえ/無/-）"),
    "SITE_CODE_WIDTH": ("sites", "全角の拠点コード"),
    "SITE_TYPE_BLANK": ("sites", "拠点の種別が空欄（拠点名の末尾から推定する）"),
    "SITE_CLOSED_REMAINS": ("sites", "閉鎖済みで jin-ji に無い拠点が一覧に残っている"),
    "CHARSET_UNMAPPABLE": ("Shift_JIS系", "Shift_JIS に無い文字が '?' に化けた"),
}
ANOM = []


def note(row, cat, col, desc, sugg=""):
    row["an"].append((cat, col, desc, sugg))


def new_row(cells, ref):
    return {"c": cells, "ref": ref, "an": []}


# ---------------------------------------------------------------- 氏名データ
SURN = [
    ("佐藤", "サトウ", "Sato"), ("鈴木", "スズキ", "Suzuki"), ("高橋|髙橋", "タカハシ", "Takahashi"),
    ("田中", "タナカ", "Tanaka"), ("伊藤", "イトウ", "Ito"), ("渡辺|渡邊|渡邉", "ワタナベ", "Watanabe"),
    ("山本", "ヤマモト", "Yamamoto"), ("中村", "ナカムラ", "Nakamura"), ("小林", "コバヤシ", "Kobayashi"),
    ("加藤", "カトウ", "Kato"), ("吉田", "ヨシダ", "Yoshida"), ("山田", "ヤマダ", "Yamada"),
    ("佐々木", "ササキ", "Sasaki"), ("山口", "ヤマグチ", "Yamaguchi"), ("松本", "マツモト", "Matsumoto"),
    ("井上", "イノウエ", "Inoue"), ("木村", "キムラ", "Kimura"), ("林", "ハヤシ", "Hayashi"),
    ("斉藤|齋藤|齊藤", "サイトウ", "Saito"), ("清水", "シミズ", "Shimizu"), ("山崎|山﨑", "ヤマザキ", "Yamazaki"),
    ("森", "モリ", "Mori"), ("池田", "イケダ", "Ikeda"), ("橋本", "ハシモト", "Hashimoto"),
    ("阿部", "アベ", "Abe"), ("石川", "イシカワ", "Ishikawa"), ("前田", "マエダ", "Maeda"),
    ("藤田", "フジタ", "Fujita"), ("小川", "オガワ", "Ogawa"), ("後藤", "ゴトウ", "Goto"),
    ("岡田", "オカダ", "Okada"), ("長谷川", "ハセガワ", "Hasegawa"), ("村上", "ムラカミ", "Murakami"),
    ("近藤", "コンドウ", "Kondo"), ("石井", "イシイ", "Ishii"), ("坂本", "サカモト", "Sakamoto"),
    ("遠藤", "エンドウ", "Endo"), ("青木", "アオキ", "Aoki"), ("藤井", "フジイ", "Fujii"),
    ("西村", "ニシムラ", "Nishimura"), ("福田", "フクダ", "Fukuda"), ("太田", "オオタ", "Ota"),
    ("三浦", "ミウラ", "Miura"), ("岡本", "オカモト", "Okamoto"), ("中島", "ナカジマ", "Nakajima"),
    ("原田", "ハラダ", "Harada"), ("竹内", "タケウチ", "Takeuchi"), ("金子", "カネコ", "Kaneko"),
    ("和田", "ワダ", "Wada"), ("中野", "ナカノ", "Nakano"), ("上田", "ウエダ", "Ueda"),
    ("柴田", "シバタ", "Shibata"), ("酒井", "サカイ", "Sakai"), ("工藤", "クドウ", "Kudo"),
    ("横山", "ヨコヤマ", "Yokoyama"), ("宮崎|宮﨑", "ミヤザキ", "Miyazaki"), ("内田", "ウチダ", "Uchida"),
    ("高木|髙木", "タカギ", "Takagi"), ("神崎|神﨑", "カンザキ", "Kanzaki"), ("大野", "オオノ", "Ono"),
    ("今井", "イマイ", "Imai"), ("平野", "ヒラノ", "Hirano"), ("久保", "クボ", "Kubo"),
    ("松井", "マツイ", "Matsui"), ("千葉", "チバ", "Chiba"), ("岩崎|岩﨑", "イワサキ", "Iwasaki"),
    ("菊地", "キクチ", "Kikuchi"), ("杉山", "スギヤマ", "Sugiyama"), ("谷口", "タニグチ", "Taniguchi"),
]
GIVEN = [
    ("太郎", "タロウ", "Taro", "M"), ("健太", "ケンタ", "Kenta", "M"), ("翔太", "ショウタ", "Shota", "M"),
    ("大輔", "ダイスケ", "Daisuke", "M"), ("拓也", "タクヤ", "Takuya", "M"), ("直樹", "ナオキ", "Naoki", "M"),
    ("和也", "カズヤ", "Kazuya", "M"), ("誠", "マコト", "Makoto", "M"), ("浩二", "コウジ", "Koji", "M"),
    ("剛", "ツヨシ", "Tsuyoshi", "M"), ("一郎", "イチロウ", "Ichiro", "M"), ("優太", "ユウタ", "Yuta", "M"),
    ("隆", "タカシ", "Takashi", "M"), ("学", "マナブ", "Manabu", "M"), ("勇気", "ユウキ", "Yuki", "M"),
    ("亮", "リョウ", "Ryo", "M"), ("慎一", "シンイチ", "Shinichi", "M"), ("哲也", "テツヤ", "Tetsuya", "M"),
    ("雅人", "マサト", "Masato", "M"), ("悠斗", "ユウト", "Yuto", "M"), ("蓮", "レン", "Ren", "M"),
    ("康平", "コウヘイ", "Kohei", "M"), ("達也", "タツヤ", "Tatsuya", "M"), ("修", "オサム", "Osamu", "M"),
    ("俊介", "シュンスケ", "Shunsuke", "M"), ("健司", "ケンジ", "Kenji", "M"), ("秀樹", "ヒデキ", "Hideki", "M"),
    ("圭介", "ケイスケ", "Keisuke", "M"), ("智也", "トモヤ", "Tomoya", "M"), ("光", "ヒカル", "Hikaru", "M"),
    ("大樹", "ダイキ", "Daiki", "M"), ("敦", "アツシ", "Atsushi", "M"), ("稔", "ミノル", "Minoru", "M"),
    ("洋平", "ヨウヘイ", "Yohei", "M"), ("純", "ジュン", "Jun", "M"), ("渉", "ワタル", "Wataru", "M"),
    ("裕介", "ユウスケ", "Yusuke", "M"), ("花子", "ハナコ", "Hanako", "F"), ("美咲", "ミサキ", "Misaki", "F"),
    ("陽子", "ヨウコ", "Yoko", "F"), ("恵子", "ケイコ", "Keiko", "F"), ("由美", "ユミ", "Yumi", "F"),
    ("愛", "アイ", "Ai", "F"), ("彩", "アヤ", "Aya", "F"), ("真由美", "マユミ", "Mayumi", "F"),
    ("裕子", "ユウコ", "Yuko", "F"), ("麻衣", "マイ", "Mai", "F"), ("優奈", "ユウナ", "Yuna", "F"),
    ("沙織", "サオリ", "Saori", "F"), ("千尋", "チヒロ", "Chihiro", "F"), ("美穂", "ミホ", "Miho", "F"),
    ("智子", "トモコ", "Tomoko", "F"), ("結衣", "ユイ", "Yui", "F"), ("菜々子", "ナナコ", "Nanako", "F"),
    ("香織", "カオリ", "Kaori", "F"), ("里奈", "リナ", "Rina", "F"), ("久美子", "クミコ", "Kumiko", "F"),
    ("絵里", "エリ", "Eri", "F"), ("奈緒", "ナオ", "Nao", "F"), ("直子", "ナオコ", "Naoko", "F"),
    ("瞳", "ヒトミ", "Hitomi", "F"), ("亜希子", "アキコ", "Akiko", "F"), ("純子", "ジュンコ", "Junko", "F"),
    ("葵", "アオイ", "Aoi", "F"), ("理恵", "リエ", "Rie", "F"), ("友美", "トモミ", "Tomomi", "F"),
    ("夏美", "ナツミ", "Natsumi", "F"), ("玲奈", "レナ", "Rena", "F"), ("莉子", "リコ", "Riko", "F"),
    ("明日香", "アスカ", "Asuka", "F"), ("典子", "ノリコ", "Noriko", "F"), ("美香", "ミカ", "Mika", "F"),
    ("遥", "ハルカ", "Haruka", "F"),
]

# ---------------------------------------------------------------- 組織
COMPANIES = [
    {"id": "P", "name": "株式会社サンプル商事", "short": "サンプル商事", "domain": "example.co.jp"},
    {"id": "L", "name": "株式会社サンプル物流", "short": "サンプル物流", "domain": "example.com"},
]
CO = {c["id"]: c for c in COMPANIES}

# 2 スペース字下げ。フラグ: "!" = 組織コード無し、"xYYYY-MM-DD" = その日付で廃止
SPEC = """
@P
経営企画室
内部監査部
営業本部
  営業第一部
    第一営業課
      東日本チーム
      西日本チーム
    第二営業課
      法人営業チーム
      公共営業チーム
  営業第二部
    第三営業課
    第四営業課
    新規開拓チーム !
  営業第三部 ! x2025-03-31
    第五営業課 x2025-03-31
  営業企画室
    販売分析チーム
管理本部
  総務部
    総務課
    人事課
      採用チーム
      労務チーム
    総務人事課 x2025-03-31
  経理部
    経理課
    財務課
  情報システム部
    社内SE課
    インフラ課
    ヘルプデスクチーム
  システム管理室 x2024-03-31
  法務室 !
製造本部
  製造第一部
    製造一課
      組立チーム
      検査チーム
    製造二課
  製造第二部
    製造三課
    製造四課
  品質保証部
    品質管理課
    品質保証課
  技術開発部
    開発一課
      基盤開発チーム
      応用開発チーム
    開発二課
商品本部
  商品企画部
    企画一課
    企画二課
  マーケティング部
    広報課
    販売促進課
@L
物流企画部
  企画グループ
  業務改善グループ
東日本物流部
  東京配送グループ
  千葉倉庫グループ
  第二倉庫グループ ! x2025-03-31
西日本物流部
  大阪配送グループ
  神戸倉庫グループ
管理部
  総務経理グループ
  人事グループ
"""
SUCCESSOR = {
    "営業第三部": ["営業第二部"], "第五営業課": ["第三営業課", "第四営業課"],
    "総務人事課": ["総務課", "人事課"], "システム管理室": ["情報システム部"],
    "第二倉庫グループ": ["千葉倉庫グループ"],
}
KINDS = ("本部", "チーム", "グループ", "室", "課", "部")
HEAD_TITLE = {"本部": "本部長", "部": "部長", "室": "室長", "課": "課長", "チーム": "リーダー", "グループ": "グループ長"}


def kind_of(n):
    for k in KINDS:
        if n.endswith(k):
            return k
    raise ValueError(n)


def level_of(co, kind, parent):
    if co == "L":
        return 1 if kind == "部" else 2
    if kind == "本部":
        return 1
    if kind == "室":
        return 1 if parent is None else 2
    return {"部": 2, "課": 3, "チーム": 4}[kind]


orgs = []
ORG = {}
BYNAME = {}
_stack, _co, _seq = [], None, 0
for line in SPEC.strip("\n").splitlines():
    if line.startswith("@"):
        _co, _stack = line[1], []
        continue
    depth = (len(line) - len(line.lstrip(" "))) // 2
    toks = line.split()
    name, flags = toks[0], toks[1:]
    parent = _stack[depth - 1] if depth > 0 else None
    del _stack[depth:]
    kind = kind_of(name)
    lv = level_of(_co, kind, parent)
    _seq += 10
    o = {
        "org_id": f"gt-o-{len(orgs) + 1:03d}", "company": _co, "name": name, "kind": kind, "level": lv,
        "parent_id": parent["org_id"] if parent else None,
        "code": None if "!" in flags else f"{'A' if _co == 'P' else 'B'}{_seq:03d}",
        "active": True, "abolished_on": None, "successor_org_ids": [],
        "skipped_level": False, "children": [], "head": None, "seen_names": set(),
    }
    for f in flags:
        if f.startswith("x"):
            o["active"], o["abolished_on"] = False, f[1:]
    pl = parent["level"] if parent else 0
    o["skipped_level"] = (lv - pl) > 1
    if parent:
        parent["children"].append(o["org_id"])
    orgs.append(o)
    ORG[o["org_id"]] = o
    BYNAME[name] = o
    _stack.append(o)
for _n, _s in SUCCESSOR.items():
    BYNAME[_n]["successor_org_ids"] = [BYNAME[x]["org_id"] for x in _s]
_nocode = [o["name"] for o in orgs if o["code"] is None]
assert len(set(o["name"] for o in orgs)) == len(orgs)


def active_children(o):
    return [ORG[c] for c in o["children"] if ORG[c]["active"]]


def ancestors(o):
    res = []
    while o["parent_id"]:
        o = ORG[o["parent_id"]]
        res.append(o)
    return res[::-1]


def top_of(o):
    return (ancestors(o) or [o])[0]


def descendants(o):
    res = []
    for c in o["children"]:
        res.append(ORG[c])
        res += descendants(ORG[c])
    return res


active_orgs = [o for o in orgs if o["active"]]
leaves = [o for o in active_orgs if not active_children(o)]
leaf_w = {}
for o in leaves:
    w = rng.uniform(0.6, 1.8)
    if top_of(o)["name"] in ("製造本部", "営業本部", "東日本物流部", "西日本物流部"):
        w *= 1.6
    leaf_w[o["org_id"]] = w

# ---- 組織名のゆれ
_NUM = {"一": "1", "二": "2", "三": "3", "四": "4", "五": "5"}


def org_variants(name):
    m = re.search(r"(第)?([一二三四五])(部|課)", name)
    if not m:
        return []
    d = _NUM[m.group(2)]
    fw = to_full_digits(d)
    reps = [d, fw, "第" + d, "第" + fw]
    res = {name[:m.start()] + r + m.group(3) + name[m.end():] for r in reps}
    res.discard(name)
    return sorted(res)


def width_var(name):
    t = name.replace("チーム", to_half_kana("チーム")).replace("グループ", to_half_kana("グループ"))
    t = t.replace("SE", "ＳＥ")
    return t


def noisy_org(o, pv=0.2, row=None, col=None, track=True):
    name = o["name"]
    t, r = name, rng.random()
    cats = []
    vs = org_variants(name)
    if vs and r < pv:
        t = rng.choice(vs)
        cats.append(("ORG_NAME_VARIANT", f"組織名の表記ゆれ: 「{t}」は「{name}」"))
    elif r < pv * 1.7 and width_var(name) != name:
        t = width_var(name)
        cats.append(("ORG_WIDTH_MIX", f"全角/半角の混在: 「{t}」は「{name}」"))
    if rng.random() < pv / 3:
        t += rng.choice([" ", "　"])
        cats.append(("ORG_TRAILING_SPACE", f"末尾空白付き: 「{name}」"))
    if track:
        o["seen_names"].add(t)
    if row is not None:
        for c, d in cats:
            note(row, c, col, d, f"{o['org_id']}（{name}）へ名寄せ")
    return t


# ---------------------------------------------------------------- 人物
P_N, L_N = 305, 45
persons = []
used_keys = set()
for i in range(P_N + L_N):
    while True:
        fs, kana, rom = rng.choice(SURN)
        g = rng.choice(GIVEN)
        if (fs.split("|")[0], g[0]) not in used_keys:
            break
    used_keys.add((fs.split("|")[0], g[0]))
    grp = fs.split("|")
    sei = rng.choice(grp)
    persons.append({
        "company": "P" if i < P_N else "L",
        "sei": sei, "sei_group": grp, "sei_kana": kana, "sei_rom": rom,
        "bsei": sei, "bsei_group": grp, "bsei_kana": kana, "bsei_rom": rom,
        "mei": g[0], "mei_kana": g[1], "mei_rom": g[2], "sex": g[3],
        "status": "active", "maiden": False, "samename": None,
        "title": "", "concurrent": [], "head_of": None, "secondment": None, "leave": None,
        "returnee": False, "retired_on": None, "flags": set(), "org": None, "work_org": None,
    })
rng.shuffle(persons)  # 会社の並びを混ぜない（company 別に再分割するため）
PP = [p for p in persons if p["company"] == "P"]
LL = [p for p in persons if p["company"] == "L"]


def take(lst, n):
    s = rng.sample(lst, n)
    for x in s:
        lst.remove(x)
    return s


remP, remL = list(PP), list(LL)
retired = take(remP, 17) + take(remL, 3)
leavers = take(remP, 4) + take(remL, 1)
sec_PL = take(remP, 3)
sec_LP = take(remL, 3)
returnee = take(remP, 1)
hire_1001 = take(remP, 3)
newgrad = take(remP, 12) + take(remL, 2)
# 同姓同名ペア: A=親会社同士、B=親会社と子会社
pairA = take(remP, 2)
pairB = take(remP, 1) + take(remL, 1)
for a, b in (pairA, pairB):
    for k in ("sei", "sei_group", "sei_kana", "sei_rom", "bsei", "bsei_group", "bsei_kana", "bsei_rom",
              "mei", "mei_kana", "mei_rom", "sex"):
        b[k] = a[k]
    a["samename"] = b["samename"] = f"pair{'A' if (a, b) == (pairA[0], pairA[1]) else 'B'}"
# 旧姓使用者 8 名（女性・在籍者から）
fem = [p for p in remP + remL if p["sex"] == "F"]
maiden = rng.sample(fem, 8)
for p in maiden:
    while True:
        fs, kana, rom = rng.choice(SURN)
        if fs.split("|")[0] != p["sei_group"][0]:
            break
    grp = fs.split("|")
    # 戸籍姓 = 現在の sei（結婚後）、業務上の氏名 = 旧姓
    p["maiden"] = True
    p["bsei_group"], p["bsei"], p["bsei_kana"], p["bsei_rom"] = grp, rng.choice(grp), kana, rom
for p in retired:
    p["status"] = "retired"
for p in leavers:
    p["status"] = "leave"
for p in sec_PL + sec_LP:
    p["flags"].add("secondee")

# ---- 日付・社員番号
def rand_date(a, b):
    return dt.date.fromordinal(rng.randint(a.toordinal(), b.toordinal()))


for p in persons:
    if p["status"] == "retired":
        r = rand_date(dt.date(2019, 6, 1), dt.date(2026, 9, 30))
        if rng.random() < 0.7:
            nxt = dt.date(r.year + (r.month == 12), r.month % 12 + 1, 1)
            r = nxt - dt.timedelta(days=1)
        p["retired_on"] = r
        p["hired_on"] = dt.date(r.year - rng.randint(3, 28), rng.randint(1, 12), 1) if rng.random() < .4 else \
            dt.date(r.year - rng.randint(3, 28), 4, 1)
    elif p in newgrad:
        p["hired_on"] = dt.date(2026, 4, 1)
    elif p in hire_1001:
        p["hired_on"] = dt.date(2026, 10, 1)
    elif rng.random() < 0.45:
        p["hired_on"] = dt.date(rng.randint(1996, 2025), 4, 1)
    else:
        p["hired_on"] = rand_date(dt.date(1996, 1, 1), dt.date(2026, 3, 31))
    if p["hired_on"] >= ASOF and p not in hire_1001:
        p["hired_on"] = dt.date(2026, 4, 1)
    age = rng.randint(22, 38)
    p["birth"] = dt.date(p["hired_on"].year - age, rng.randint(1, 12), rng.randint(1, 28))
for grp, fmt, stepch in ((PP, "{:05d}", [1, 1, 1, 2, 3]), (LL, "L{:04d}", [1, 1, 2])):
    n = 0
    for p in sorted(grp, key=lambda x: (x["hired_on"], rng.random())):
        n += rng.choice(stepch)
        p["empno_n"] = n
        p["empno"] = fmt.format(n)
persons.sort(key=lambda p: (p["company"], p["empno"]))
for i, p in enumerate(persons, 1):
    p["person_id"] = f"gt-p-{i:04d}"
PID = {p["person_id"]: p for p in persons}
BYEMP = {p["empno"]: p for p in persons}

# ---- メール
_taken = set()
for p in persons:
    local = f"{p['mei_rom']}.{p['bsei_rom']}".lower()
    dom = CO[p["company"]]["domain"]
    cand, n = local, 2
    while (cand, dom) in _taken:
        cand, n = f"{local}{n}", n + 1
    _taken.add((cand, dom))
    p["email"] = f"{cand}@{dom}"

# ---- 組織への配置
def assign_heads():
    plain = {c: [p for p in persons if p["company"] == c and p["status"] == "active" and "secondee" not in p["flags"]
                 and p["hired_on"] < dt.date(2026, 4, 1) and p not in returnee] for c in "PL"}
    for lst in plain.values():
        rng.shuffle(lst)
    conc_targets = ["経営企画室", "営業企画室", "法務室", "内部監査部", "品質保証部", "技術開発部",
                    "新規開拓チーム", "ヘルプデスクチーム", "販売分析チーム", "物流企画部"]
    tgt_ids = {BYNAME[n]["org_id"] for n in conc_targets}
    heads = {}
    for o in active_orgs:
        if o["org_id"] in tgt_ids:
            continue
        p = plain[o["company"]].pop()
        heads[o["org_id"]] = p
    # 兼務: 他組織の責任者が兼ねる
    pool = {"P": [(oid, p) for oid, p in heads.items() if p["company"] == "P"
                  and ORG[oid]["kind"] in ("本部", "部", "課")], "L": []}
    rng.shuffle(pool["P"])
    used = set()
    for n in conc_targets:
        t = BYNAME[n]
        if t["company"] == "L":
            holder = heads[BYNAME["管理本部"]["org_id"]]
            hoid = BYNAME["管理本部"]["org_id"]
        else:
            while True:
                hoid, holder = pool["P"].pop()
                if holder["person_id" if "person_id" in holder else "empno"] not in used and \
                        hoid not in [a["org_id"] for a in ancestors(t)] and top_of(ORG[hoid]) is not top_of(t):
                    break
        used.add(holder["empno"])
        heads[t["org_id"]] = holder
        holder["concurrent"].append({"org_id": t["org_id"], "title": HEAD_TITLE[t["kind"]], "since": None})
    return heads


HEADS = assign_heads()
for oid, p in HEADS.items():
    o = ORG[oid]
    o["head"] = p["person_id"]
    if p["head_of"] is None:  # 兼務の場合は本務側を head_of に
        p["head_of"] = oid
        p["org"] = oid
        p["title"] = HEAD_TITLE[o["kind"]]
# 兼務者の本務は最初に割り当てた組織。上のループは dict 順で本務→兼務の順に当たるとは限らないため補正
for p in persons:
    if p["concurrent"]:
        conc_ids = {c["org_id"] for c in p["concurrent"]}
        mains = [oid for oid, h in HEADS.items() if h is p and oid not in conc_ids]
        p["head_of"] = p["org"] = mains[0]
        p["title"] = HEAD_TITLE[ORG[mains[0]]["kind"]]
leaves_by_co = {c: [o for o in leaves if o["company"] == c] for c in "PL"}
for p in persons:
    if p["org"] is None:
        cands = leaves_by_co[p["company"]]
        p["org"] = wchoice(cands, [leaf_w[o["org_id"]] for o in cands])["org_id"]
        if p["status"] == "active" and "secondee" not in p["flags"] and p["hired_on"] < dt.date(2026, 4, 1) \
                and rng.random() < 0.12:
            p["title"] = "主任"
    p["work_org"] = p["org"]
# 出向
sec_dates = [dt.date(2025, 10, 1), dt.date(2026, 4, 1), dt.date(2026, 7, 1)]
for p in sec_PL:
    dest = wchoice(leaves_by_co["L"], [leaf_w[o["org_id"]] for o in leaves_by_co["L"]])
    p["secondment"] = {"company": "L", "org_id": dest["org_id"], "since": rng.choice(sec_dates)}
    p["work_org"] = dest["org_id"]
for p in sec_LP:
    dest = wchoice(leaves_by_co["P"], [leaf_w[o["org_id"]] for o in leaves_by_co["P"]])
    p["secondment"] = {"company": "P", "org_id": dest["org_id"], "since": rng.choice(sec_dates)}
    p["work_org"] = dest["org_id"]
# 休職
for p in leavers:
    p["leave"] = {"since": rand_date(dt.date(2026, 3, 1), dt.date(2026, 9, 15)),
                  "reason": rng.choice(["私傷病", "育児", "介護", "私傷病", "自己都合（留学）"])}
returnee[0]["returnee"] = True
returnee[0]["leave_history"] = [{"since": "2026-02-01", "returned_on": "2026-07-01"}]
# 兼務開始日
for p in persons:
    for c in p["concurrent"]:
        c["since"] = rng.choice([dt.date(2026, 4, 1)] * 6 + [dt.date(2025, 10, 1)] * 4)
assert sum(1 for p in persons if p["concurrent"]) == 10
for p in persons:
    p["name_variants"] = sorted({a + p["mei"] for a in p["bsei_group"]} | {a + p["mei"] for a in p["sei_group"]})

# 場所・電話
OFFICE = {"営業本部": "東京本社", "管理本部": "東京本社", "商品本部": "東京本社", "経営企画室": "東京本社",
          "内部監査部": "東京本社", "製造本部": "横浜工場", "東日本物流部": "千葉物流センター",
          "西日本物流部": "大阪物流センター", "物流企画部": "千葉物流センター", "管理部": "千葉物流センター"}


def office_of(oid):
    t = top_of(ORG[oid])
    loc = OFFICE.get(t["name"], "東京本社")
    if t["name"] == "営業本部" and rng.random() < .3:
        loc = rng.choice(["大阪支社", "名古屋支店", "福岡支店"])
    return loc


for p in persons:
    p["office"] = office_of(p["work_org"])
    p["ext"] = f"{rng.randint(1000, 9999)}"
    p["tel"] = f"+81 {'3' if p['company'] == 'P' else '43'}-5550-{rng.randint(1000, 9999)}"
    p["mobile"] = f"+81 90-0000-{rng.randint(1000, 9999)}" if rng.random() < .2 else None
    p["guid"] = str(uuid.UUID(int=rng.getrandbits(128), version=4))

# 拠点マスタ（会社の事業所。所在地は架空の番地）。乱数は使わない（他のファイルの乱数の並びを変えないため）
SITES = [
    {"site_id": "gt-s-01", "company": "P", "code": "S010", "name": "東京本社", "type": "head_office", "prefecture_code": "13",
     "location": "千代田区丸の内9-9-1", "opened_on": "1990-04-01"},
    {"site_id": "gt-s-02", "company": "P", "code": "S020", "name": "横浜工場", "type": "factory", "prefecture_code": "14",
     "location": "横浜市鶴見区末広町9-9-2", "opened_on": "1995-10-01"},
    {"site_id": "gt-s-03", "company": "P", "code": "S030", "name": "大阪支社", "type": "branch", "prefecture_code": "27",
     "location": "大阪市北区梅田9-9-3", "opened_on": "2001-04-01"},
    {"site_id": "gt-s-04", "company": "P", "code": "S040", "name": "名古屋支店", "type": "branch", "prefecture_code": "23",
     "location": "名古屋市中村区名駅9-9-4", "opened_on": "2008-04-01"},
    {"site_id": "gt-s-05", "company": "P", "code": "S050", "name": "福岡支店", "type": "branch", "prefecture_code": "40",
     "location": "福岡市博多区博多駅前9-9-5", "opened_on": "2012-04-01"},
    {"site_id": "gt-s-06", "company": "L", "code": "LS10", "name": "千葉物流センター", "type": "logistics", "prefecture_code": "12",
     "location": "千葉市美浜区新港9-9-6", "opened_on": "2003-07-01"},
    {"site_id": "gt-s-07", "company": "L", "code": "LS20", "name": "大阪物流センター", "type": "logistics", "prefecture_code": "27",
     "location": "大阪市住之江区南港北9-9-7", "opened_on": "2010-10-01"},
]
SITE_BY_NAME = {x["name"]: x for x in SITES}
for p in persons:
    # リモートワークの対象（有無だけ）。製造・物流の現場以外の在籍者の一部
    p["remote_work"] = p["status"] != "retired" and p["office"] in ("東京本社", "大阪支社", "名古屋支店", "福岡支店") \
        and int(p["person_id"][-4:]) % 4 == 1


def org_site_name(o):
    """組織の拠点（最上位の組織だけが持ち、配下は引き継ぐ）。"""
    return OFFICE.get(top_of(o)["name"], "東京本社")


# 人事パッケージの勤務地の表記ゆれ（乱数を使わず決める）と、拠点マスタに無い勤務地
SITE_VARIANTS = {"東京本社": ["本社（東京）", "東京 本社", "東京本社 "], "千葉物流センター": ["千葉物流ｾﾝﾀｰ"],
                 "横浜工場": ["横浜工場　"], "大阪支社": ["大阪支社(梅田)"]}
SITE_UNKNOWN = ["札幌出張所", "仙台営業所"]

# ================================================================ 辞令イベントの設計（基準日以前=発令済み、以後=内示）
EV = []
cur_states = [p for p in persons if p["status"] == "active" and "secondee" not in p["flags"]
              and p["hired_on"] < dt.date(2026, 4, 1) and p not in returnee]
head_people = [p for p in cur_states if p["head_of"]]
nonhead = [p for p in cur_states if not p["head_of"]]
mass = rng.sample(head_people, 30) + rng.sample([p for p in nonhead if p["samename"] != "pairA"], 24) \
    + [pairA[0]]
mass = list({id(x): x for x in mass}.values())
# 人事パッケージが 4/1 発令を反映できていない者（旧所属のまま）
stale_hr = rng.sample([p for p in mass if not p["head_of"] and p is not pairA[0]], 4)
STALE_HR = {}
for p in stale_hr:
    others = [o for o in leaves_by_co[p["company"]] if o["org_id"] != p["org"]]
    STALE_HR[p["person_id"]] = rng.choice(others)["org_id"]


def head_phrase(p, o):
    return HEAD_TITLE[o["kind"]]


for p in mass:
    o = ORG[p["org"]]
    EV.append({"date": dt.date(2026, 4, 1), "p": p, "type": "assign", "org": o["org_id"], "title": p["title"], "eff": True})
for p in newgrad:
    EV.append({"date": dt.date(2026, 4, 1), "p": p, "type": "hire_assign", "org": p["org"], "title": "", "eff": True})
for p in persons:
    for c in p["concurrent"]:
        EV.append({"date": c["since"], "p": p, "type": "concurrent_start", "org": c["org_id"], "title": c["title"], "eff": True})
ended = rng.sample([p for p in head_people if not p["concurrent"]], 3)
for p in ended:
    EV.append({"date": dt.date(2026, 4, 1), "p": p, "type": "concurrent_end", "org": None, "title": "", "eff": True})
for p in sec_PL + sec_LP:
    EV.append({"date": p["secondment"]["since"], "p": p, "type": "secondment", "org": p["secondment"]["org_id"],
               "title": "", "eff": True})
for p in leavers:
    EV.append({"date": p["leave"]["since"], "p": p, "type": "leave", "org": None, "title": "", "eff": True})
rp = returnee[0]
EV.append({"date": dt.date(2026, 2, 1), "p": rp, "type": "leave", "org": None, "title": "", "eff": True})
EV.append({"date": dt.date(2026, 7, 1), "p": rp, "type": "return", "org": None, "title": "", "eff": True})
# 未来日付（内示相当）
_used = set(id(x) for x in mass) | set(id(x) for x in ended)
fut_pool = [p for p in nonhead if id(p) not in _used and p["title"] == ""]
rng.shuffle(fut_pool)
fut_pool_all = [p for p in cur_states if not p["head_of"]]
for p in fut_pool[:3]:
    EV.append({"date": dt.date(2026, 11, 1), "p": p, "type": "promote", "org": p["org"], "title": "主任", "eff": False})
p = fut_pool[3]
dest = rng.choice([o for o in leaves_by_co[p["company"]] if o["org_id"] != p["org"]])
EV.append({"date": dt.date(2026, 11, 1), "p": p, "type": "transfer", "org": dest["org_id"], "title": "", "eff": False})
p = fut_pool[4]
EV.append({"date": dt.date(2026, 11, 1), "p": p, "type": "leave", "org": None, "title": "", "eff": False})
for p in fut_pool[5:8]:
    dest = rng.choice([o for o in leaves_by_co[p["company"]] if o["org_id"] != p["org"]])
    EV.append({"date": dt.date(2027, 4, 1), "p": p, "type": "transfer", "org": dest["org_id"], "title": "", "eff": False})
ka = [o for o in leaves_by_co["P"] if o["kind"] == "課"]
for p, o in zip(fut_pool[8:11], rng.sample(ka, 3)):
    EV.append({"date": dt.date(2027, 4, 1), "p": p, "type": "promote_head", "org": o["org_id"], "title": "課長", "eff": False})
p = fut_pool[11]
EV.append({"date": dt.date(2027, 4, 1), "p": p, "type": "secondment_future", "org": rng.choice(leaves_by_co["L"])["org_id"],
           "title": "", "eff": False})
cc = [p for p in persons if p["concurrent"] and p["company"] == "P"][0]
EV.append({"date": dt.date(2027, 4, 1), "p": cc, "type": "concurrent_end_future", "org": None, "title": "", "eff": False})

# ================================================================ 1. 組織図 CSV
ORG_COLS = ["会社", "本部", "部・室", "課・グループ", "チーム", "組織コード", "責任者", "人数", "備考", "拠点"]
COLIDX = {"P": {1: 1, 2: 2, 3: 3, 4: 4}, "L": {1: 2, 2: 3}}
ABOL_NOTE = {
    "営業第三部": "2025/03/31 廃止", "第五営業課": "", "総務人事課": "旧組織（人事課・総務課へ分割）",
    "システム管理室": "廃止", "第二倉庫グループ": "H29統合",
}
stale_head_nodes = rng.sample([o for o in leaves if o["kind"] in ("課", "チーム") and o["company"] == "P"], 2)
ret_by_co = {c: [p for p in retired if p["company"] == c] for c in "PL"}
mem_count = collections.Counter(p["org"] for p in persons if p["status"] != "retired")
org_rows = []
first_in_co = set()
for o in orgs:
    cells = [""] * 10
    row = new_row(cells, {"org_id": o["org_id"]})
    if o["parent_id"] is None and o["active"]:
        cells[9] = org_site_name(o)
    if o["company"] not in first_in_co:
        first_in_co.add(o["company"])
        cells[0] = CO[o["company"]]["name"]
    idx = COLIDX[o["company"]][o["level"]]
    cells[idx] = noisy_org(o, 0.3, row, ORG_COLS[idx])
    if o["parent_id"] or o["level"] > 1:
        note(row, "ORG_MERGED_BLANK", None, "上位組織名の列が空欄（結合セル由来）。親は上の行から推定",
             f"親={ORG[o['parent_id']]['name'] if o['parent_id'] else '会社直下'}")
    if o["parent_id"] and rng.random() < 0.12:  # 改ページ位置などで上位名が再掲される
        for a in ancestors(o):
            ai = COLIDX[o["company"]][a["level"]]
            cells[ai] = noisy_org(a, 0.5, row, ORG_COLS[ai])
    if o["skipped_level"]:
        note(row, "ORG_LEVEL_SKIP", ORG_COLS[idx], "上位の段を飛ばして配置されている",
             f"親={ORG[o['parent_id']]['name'] if o['parent_id'] else '会社直下'}（level {o['level']} は段飛ばし）")
    if o["code"]:
        code = o["code"]
        if rng.random() < 0.05:
            code = to_full_digits(code.replace("A", "Ａ").replace("B", "Ｂ"))
            note(row, "ORG_WIDTH_MIX", "組織コード", f"全角の組織コード「{code}」は「{o['code']}」", "半角へ正規化")
        cells[5] = code
    else:
        note(row, "ORG_NO_CODE", "組織コード", "組織コード欄が空", "組織名と上位組織から仮コードを採番（名称は全体で一意）")
    # 責任者
    head = HEADS.get(o["org_id"])
    if o in stale_head_nodes:
        sp = rng.choice(ret_by_co["P"])
        nm = sp["bsei"] + "　" + sp["mei"]
        cells[6] = nm
        note(row, "ORG_STALE_HEAD", "責任者", f"責任者が退職者（{sp['person_id']}・{sp['retired_on']}退職）のまま",
             f"現責任者 {head['person_id']} へ更新を提案" if head else "")
        row["ref"]["stale_head_person"] = sp["person_id"]
    elif head and rng.random() < 0.9:
        sei = head["bsei"]
        if len(head["bsei_group"]) > 1 and rng.random() < 0.25:
            sei = rng.choice([g for g in head["bsei_group"] if g != sei])
            note(row, "NAME_KANJI_VARIANT", "責任者", f"異体字: {sei}（正: {head['bsei']}）", f"{head['person_id']} へ名寄せ")
        nm = sei + rng.choice(["　", "　", " ", ""]) + head["mei"]
        cells[6] = nm
        row["ref"]["head_person"] = head["person_id"]
    if o["active"]:
        n = mem_count.get(o["org_id"], 0)
        if o["org_id"] in [x["org_id"] for x in leaves]:
            cells[7] = to_full_digits(str(n)) if rng.random() < 0.15 else str(n)
            if cells[7] != str(n):
                note(row, "ORG_WIDTH_MIX", "人数", "全角数字の人数", "半角へ正規化")
    else:
        note(row, "ORG_ABOLISHED_REMAINS", None, f"廃止済み組織（{o['abolished_on']}）が残っている"
             + ("（備考に明示あり）" if ABOL_NOTE.get(o["name"]) else "（備考に表示なし）"),
             "後継組織: " + "、".join(ORG[s]["name"] for s in o["successor_org_ids"]))
        cells[8] = ABOL_NOTE.get(o["name"], "")
    if o["name"] == "新規開拓チーム":
        cells[8] = "2026/04新設"
    if o["org_id"] in {BYNAME[n]["org_id"] for n in ("営業企画室", "品質保証部", "技術開発部", "法務室",
                                                    "経営企画室", "内部監査部", "物流企画部")}:
        cells[8] = (cells[8] + " 兼務者が責任者").strip()
    org_rows.append(row)

# ================================================================ 共通: 氏名レンダリング
def render_name(p, row, col, who="biz", pvar=0.25, spaces=(0.4, 0.4, 0.2), track=True, file=None):
    sei = p["bsei"] if who == "biz" else p["sei"]
    grp = p["bsei_group"] if who == "biz" else p["sei_group"]
    if len(grp) > 1 and rng.random() < pvar:
        alt = rng.choice([g for g in grp if g != sei])
        note(row, "NAME_KANJI_VARIANT", col, f"異体字: 「{alt}」（正: 「{sei}」）", f"{p['person_id']} へ名寄せ（異体字を同一視）")
        sei = alt
    sp = wchoice([" ", "　", ""], spaces)
    return sei + sp + p["mei"]


def render_empno(p, row, col, pad=0.5):
    e = p["empno"]
    if rng.random() >= pad:
        if p["company"] == "P":
            e2 = str(p["empno_n"])
        else:
            e2 = f"L{p['empno_n']}"
        note(row, "EMPNO_ZEROPAD", col, f"ゼロ埋め無し「{e2}」（正規形「{e}」）", f"正規形 {e} へ補完")
        return e2
    return e


def date_cell(d, row, col, weights=(0.4, 0.3, 0.15, 0.15), styles=(0, 1, 2, 3)):
    st = wchoice(styles, weights)
    s = fmt_date(d, st)
    if st != 0:
        note(row, "DATE_FORMAT_MIX", col, f"ISO 以外の日付形式「{s}」", f"{d.isoformat()} へ正規化")
    return s


# ================================================================ 2. 人事パッケージ CSV
HR_KEEP = ["社員番号", "氏名", "氏名カナ", "通称(旧姓使用)", "会社", "所属コード", "所属部署", "役職", "兼務部署", "兼務役職",
           "入社日", "退職日", "在籍区分", "出向先会社", "出向先部署", "出向開始日", "休職開始日", "メールアドレス", "内線", "勤務地"]
HR_FORBIDDEN = ["生年月日", "性別", "郵便番号", "住所", "私用電話番号", "扶養人数", "配偶者の有無", "等級", "号俸",
                "休職理由", "最新人事評価"]
HR_COLS = HR_KEEP + HR_FORBIDDEN + ["リモートワーク"]
REMOTE_TRUE = ["○", "○", "可", "○", "はい", "有", "○", "対象"]
REMOTE_FALSE = ["×", "", "×", "いいえ", "", "×", "無", "", "-", ""]
CITY = ["東京都世田谷区", "東京都練馬区", "神奈川県横浜市港北区", "千葉県船橋市", "埼玉県さいたま市大宮区", "大阪府大阪市北区",
        "大阪府吹田市", "愛知県名古屋市中区", "福岡県福岡市博多区", "兵庫県神戸市灘区"]
GRADE = {"本部長": "M3", "部長": "M2", "室長": "M2", "課長": "M1", "グループ長": "M1"}
hr_rows = []
for p in persons:
    row = new_row([""] * len(HR_COLS), {"person_id": p["person_id"]})
    c = row["c"]
    c[0] = render_empno(p, row, "社員番号", 0.7)
    c[1] = render_name(p, row, "氏名", who="legal", pvar=0.2, spaces=(0.15, 0.7, 0.15))
    kana = p["sei_kana"] + wchoice([" ", "　", ""], (.4, .5, .1)) + p["mei_kana"]
    if rng.random() < 0.2:
        kana = to_half_kana(kana)
        note(row, "KANA_HALFWIDTH", "氏名カナ", "半角カナのフリガナ", "全角カナへ正規化")
    c[2] = kana
    if p["maiden"]:
        c[3] = render_name(p, row, "通称(旧姓使用)", who="biz", pvar=0.1, spaces=(0.1, 0.8, 0.1))
        note(row, "MAIDEN_NAME", "氏名/通称", "氏名は戸籍姓、通称が業務上の氏名（旧姓使用）",
             f"業務上の氏名={p['bsei']} {p['mei']}、戸籍名は別名として保持（{p['person_id']}）")
    if p["samename"]:
        note(row, "SAME_NAME_PAIR", "氏名", "同姓同名の別人が存在する", "氏名のみで名寄せせず社員番号で識別")
    cn = CO[p["company"]]["name"]
    if rng.random() < 0.04:
        cn = "(株)" + CO[p["company"]]["short"]
        note(row, "COMPANY_NAME_VARIANT", "会社", f"会社名の略記「{cn}」", f"{CO[p['company']]['name']} へ名寄せ")
    c[4] = cn
    home = ORG[STALE_HR.get(p["person_id"], p["org"])]
    if p["person_id"] in STALE_HR:
        note(row, "HR_STALE_ORG", "所属部署", f"所属が 2026-04-01 発令前のまま（正: {ORG[p['org']]['name']}）",
             f"辞令（2026-04-01）に従い {p['org']}（{ORG[p['org']]['name']}）へ更新を提案")
    if rng.random() < 0.75 and home["code"]:
        c[5] = home["code"]
    c[6] = noisy_org(home, 0.2, row, "所属部署")
    c[7] = p["title"]
    if p["concurrent"]:
        co_ = p["concurrent"][0]
        c[8] = (CO[ORG[co_["org_id"]]["company"]]["short"] + " " if ORG[co_["org_id"]]["company"] != p["company"] else "") \
            + noisy_org(ORG[co_["org_id"]], 0.2, row, "兼務部署")
        c[9] = co_["title"]
    c[10] = date_cell(p["hired_on"], row, "入社日")
    if p["retired_on"]:
        c[11] = date_cell(p["retired_on"], row, "退職日")
    c[12] = {"active": "在籍", "leave": "休職中", "retired": "退職"}[p["status"]]
    if p["secondment"]:
        c[12] = "在籍(出向中)"
        c[13] = CO[p["secondment"]["company"]]["name"]
        c[14] = noisy_org(ORG[p["secondment"]["org_id"]], 0.2, row, "出向先部署")
        c[15] = date_cell(p["secondment"]["since"], row, "出向開始日")
    if p["leave"]:
        c[16] = date_cell(p["leave"]["since"], row, "休職開始日")
    em = p["email"]
    if rng.random() < 0.12:
        loc, dom = em.split("@")
        em = rng.choice([loc.title() + "@" + dom, em.upper(), loc.title() + "@" + dom.title()])
        note(row, "EMAIL_CASE", "メールアドレス", f"大文字小文字ゆれ「{em}」", "小文字へ正規化（照合は大文字小文字無視）")
    c[17] = em
    if p["status"] != "retired":
        c[18] = p["ext"]
    # 勤務地（拠点名）: 表記ゆれと、拠点マスタに無い勤務地（乱数を使わず決める）
    office = p["office"]
    pidx = int(p["person_id"][-4:])
    if p["status"] != "retired" and pidx in (41, 97):
        office = SITE_UNKNOWN[0 if pidx == 41 else 1]
        note(row, "SITE_UNKNOWN", "勤務地", f"拠点マスタに無い勤務地「{office}」（正: {p['office']}）", "修正依頼（拠点マスタに無い）。拠点は所属組織の拠点のまま")
    elif office in SITE_VARIANTS and pidx % 9 == 0:
        office = SITE_VARIANTS[p["office"]][pidx // 9 % len(SITE_VARIANTS[p["office"]])]
        note(row, "SITE_NAME_VARIANT", "勤務地", f"拠点名の表記ゆれ「{office}」（正: {p['office']}）", f"{p['office']} へ照合")
    c[19] = office
    # リモートワーク（有無だけ。表示値の揺れ）
    if p["status"] != "retired":
        flags = REMOTE_TRUE if p["remote_work"] else REMOTE_FALSE
        c[31] = flags[pidx % len(flags)]
        if c[31] not in ("○", "×", ""):
            note(row, "REMOTE_FLAG_VARIANT", "リモートワーク", f"有無の表記「{c[31]}」", "true" if p["remote_work"] else "false")
    # --- 保持禁止列（ダミー値）
    c[20] = date_cell(p["birth"], row, "生年月日", styles=(0, 1, 2, 3)) if False else fmt_date(p["birth"], wchoice([0, 1, 2, 3], [.3, .3, .2, .2]))
    c[21] = "男" if p["sex"] == "M" else "女"
    c[22] = f"{rng.randint(100, 999)}-{rng.randint(1000, 9999)}"
    c[23] = f"{rng.choice(CITY)}{rng.randint(1, 9)}-{rng.randint(1, 30)}-{rng.randint(1, 20)}"
    c[24] = f"090-0000-{rng.randint(1000, 9999)}"
    c[25] = str(rng.choice([0, 0, 1, 1, 2, 3]))
    c[26] = rng.choice(["有", "無"])
    c[27] = GRADE.get(p["title"], f"G{rng.randint(2, 5)}")
    c[28] = str(rng.randint(1, 12))
    c[29] = p["leave"]["reason"] if p["leave"] else ""
    c[30] = rng.choice(["S", "A", "B", "B", "C"]) if p["status"] != "retired" else ""
    hr_rows.append(row)
# 行順: 社員番号の文字列順 + 少し乱れ
hr_rows.sort(key=lambda r: (PID[r["ref"]["person_id"]]["company"], r["c"][0]))
for _ in range(25):
    i, j = rng.randrange(len(hr_rows)), rng.randrange(len(hr_rows))
    hr_rows[i], hr_rows[j] = hr_rows[j], hr_rows[i]

# ================================================================ 3. 辞令 CSV
JIREI_COLS = ["発令日", "社員番号", "氏名", "発令内容", "備考"]


def org_text(oid, row, pv=0.25):
    return noisy_org(ORG[oid], pv, row, "発令内容")


def ev_text(e, row):
    p, t = e["p"], e["type"]
    ty = e["type"]
    if ty in ("assign", "hire_assign", "transfer", "promote", "promote_head"):
        o = ORG[e["org"]]
        title = e["title"]
        otxt = org_text(e["org"], row)
        if ty == "hire_assign":
            return rng.choice([f"{otxt}に配属を命ずる", f"新入社員 {otxt}配属を命ずる"])
        if ty == "assign" and title in ("課長",) and o["parent_id"] and ORG[o["parent_id"]]["kind"] == "部" \
                and len(active_children(ORG[o["parent_id"]])) >= 2 and AMB["n"] < 6:
            AMB["n"] += 1
            par = ORG[o["parent_id"]]
            note(row, "JIREI_AMBIGUOUS_ORG", "発令内容", f"所属が「{par['name']} 課長」としか書かれず課が特定できない",
                 f"{par['name']} 配下の課長が未任命の課を現職と照合して {o['org_id']}（{o['name']}）を提案。複数候補は要確認: "
                 + "、".join(c["name"] for c in active_children(par)))
            row["ref"]["ambiguous_candidates"] = [c["org_id"] for c in active_children(par)]
            return f"{noisy_org(par, 0.25)} 課長を命ずる"
        if title:
            return rng.choice([f"{otxt} {title}を命ずる", f"{otxt}　{title}を命ずる", f"{otxt}{title}に任ずる"])
        return rng.choice([f"{otxt}勤務を命ずる", f"{otxt}への異動を命ずる", f"{otxt}に配置換えを命ずる"])
    if ty == "concurrent_start":
        o = ORG[e["org"]]
        pre = CO[o["company"]]["name"] + " " if o["company"] != p["company"] else ""
        return rng.choice([f"{pre}{org_text(e['org'], row)} {e['title']}を兼務させる",
                           f"{pre}{org_text(e['org'], row)} {e['title']}を兼務することを命ずる"])
    if ty == "concurrent_end":
        return rng.choice(["兼務を解く", "兼務を解く", "兼務を免ずる"])
    if ty == "concurrent_end_future":
        return "兼務を解く"
    if ty in ("secondment", "secondment_future"):
        dc = CO[ORG[e["org"]]["company"]]["name"]
        return rng.choice([f"{dc}へ出向を命ずる", f"{dc}（{org_text(e['org'], row)}）へ出向を命ずる", f"出向を命ずる（出向先: {dc}）"])
    if ty == "leave":
        return "休職を命ずる"
    if ty == "return":
        return "復職を命ずる"
    raise ValueError(ty)


AMB = {"n": 0}
EV.sort(key=lambda e: (e["date"], e["p"]["company"], e["p"]["empno"]))
# 誤記・社員番号なし対象の選定
mass_ev = [e for e in EV if e["type"] == "assign" and e["eff"]]
pairA_ev = [e for e in mass_ev if e["p"] is pairA[0]][0]
noemp_ev = [pairA_ev] + rng.sample([e for e in mass_ev if e is not pairA_ev], 4)
typo_ev = rng.sample([e for e in mass_ev if e not in noemp_ev], 4)
jirei_rows = []
for e in EV:
    p = e["p"]
    row = new_row([""] * 5, {"person_id": p["person_id"], "order_type": e["type"], "effective": e["eff"],
                             "org_id": e["org"], "title": e["title"], "date": e["date"].isoformat()})
    c = row["c"]
    c[0] = date_cell(e["date"], row, "発令日", weights=(.1, .25, .15, .5 * 0.3 + .0), styles=(0, 4, 3, 1)) \
        if False else None
    st = wchoice([4, 1, 3, 0], [.5, .25, .15, .1])
    c[0] = fmt_date(e["date"], st)
    if st != 0:
        note(row, "DATE_FORMAT_MIX", "発令日", f"ISO 以外の日付形式「{c[0]}」", f"{e['date'].isoformat()} へ正規化")
    # 社員番号
    if e in noemp_ev:
        c[1] = ""
        ambiguous = p["samename"] is not None
        note(row, "JIREI_EMPNO_MISSING", "社員番号", "社員番号が空（氏名のみ）",
             ("同姓同名が 2 名おり一意に決まらない → 要確認（候補: " + "、".join(
                 q["person_id"] for q in persons if q["samename"] == p["samename"]) + "）")
             if ambiguous else f"氏名と発令内容から {p['person_id']}（{p['empno']}）を提案")
        row["ref"]["resolution"] = "ambiguous_same_name" if ambiguous else "by_name"
    elif e in typo_ev:
        good = p["empno"]
        if e is typo_ev[0]:
            other = rng.choice([q for q in persons if q["company"] == p["company"] and q is not p and q["status"] != "retired"])
            bad = other["empno"] if p["company"] == "P" else other["empno"]
            note(row, "JIREI_EMPNO_TYPO", "社員番号", f"社員番号が別人（{other['person_id']}）の番号になっている誤記「{bad}」",
                 f"氏名が不一致のため {p['person_id']}（{good}）を提案")
            row["ref"]["collides_with_person"] = other["person_id"]
        else:
            digits = list(good)
            k = len(digits) - 1
            digits[k] = str((int(digits[k]) + rng.randint(1, 8)) % 10)
            bad = "".join(digits)
            while bad in BYEMP:
                digits[k] = str((int(digits[k]) + 1) % 10)
                bad = "".join(digits)
            note(row, "JIREI_EMPNO_TYPO", "社員番号", f"存在しない社員番号「{bad}」（誤記）",
                 f"氏名一致から {p['person_id']}（{good}）を提案")
        row["ref"]["resolution"] = "by_name"
        c[1] = bad
    else:
        c[1] = render_empno(p, row, "社員番号", 0.5)
    # 氏名
    if p["maiden"] and rng.random() < 0.35:
        c[2] = render_name(p, row, "氏名", who="legal", pvar=0.1)
        note(row, "MAIDEN_NAME", "氏名", "戸籍姓で記載（他ファイルは通称）", f"{p['person_id']} の戸籍名として名寄せ")
    else:
        c[2] = render_name(p, row, "氏名", who="biz", pvar=0.25)
    if p["samename"]:
        note(row, "SAME_NAME_PAIR", "氏名", "同姓同名の別人が存在する", "社員番号で識別")
    c[3] = ev_text(e, row)
    if not e["eff"]:
        note(row, "JIREI_FUTURE_DATED", "発令日", f"未来日付（{e['date'].isoformat()}）の辞令。基準日 {ASOF} 時点の所属に反映しない",
             "将来予定として保持し、現在の所属は変更しない")
        if e["date"].year == 2027 and rng.random() < 0.5 or e["date"] == dt.date(2026, 11, 1) and rng.random() < .4:
            c[4] = "内示"
    elif rng.random() < 0.04:
        c[4] = "要確認"
    jirei_rows.append(row)

# ================================================================ 4. Entra
def entra_build():
    present = [p for p in persons if p not in hire_1001]
    ret_pres = rng.sample(retired, 12)
    ret_enabled = set(id(x) for x in rng.sample(ret_pres, 5))
    present = [p for p in persons if p["status"] != "retired" and p not in hire_1001] + ret_pres
    act = [p for p in present if p["status"] != "retired"]
    # 逆順/ローマ字/employeeId 空
    rev = rng.sample([p for p in act if not p["samename"]], 8)
    rom = rng.sample([p for p in act if p not in rev and not p["samename"]], 10)
    emp_empty = set(id(x) for x in rng.sample(act + ret_pres, 25))
    emp_nopad = set(id(x) for x in rng.sample(act, 40))
    # 旧組織名のまま
    stale = {}

    def pickst(subtree, oldname, n):
        cand = [p for p in act if p["work_org"] in {x["org_id"] for x in subtree} and p["person_id"] not in stale
                and p not in rev and p not in rom]
        for p in rng.sample(cand, min(n, len(cand))):
            stale[p["person_id"]] = BYNAME[oldname]["org_id"]
    pickst([BYNAME["総務課"], BYNAME["人事課"]] + descendants(BYNAME["人事課"]), "総務人事課", 5)
    pickst(descendants(BYNAME["営業第二部"]), "営業第三部", 3)
    pickst(descendants(BYNAME["営業第二部"]), "第五営業課", 2)
    pickst(descendants(BYNAME["情報システム部"]), "システム管理室", 3)
    pickst([BYNAME["千葉倉庫グループ"]], "第二倉庫グループ", 2)
    val = []
    for p in present:
        row = new_row(None, {"person_id": p["person_id"], "kind": "person"})
        full = (p["bsei"], p["mei"])
        sei = p["bsei"]
        if len(p["bsei_group"]) > 1 and rng.random() < 0.3:
            sei = rng.choice([g for g in p["bsei_group"] if g != sei])
            note(row, "NAME_KANJI_VARIANT", "displayName", f"異体字「{sei}」（正: 「{p['bsei']}」）", f"{p['person_id']} へ名寄せ")
        if p["maiden"] and rng.random() < 0.15:
            note(row, "MAIDEN_NAME", "displayName", "通称（旧姓）で表示。HR の氏名は戸籍姓", "通称として照合")
        given, surname = p["mei"], sei
        if p in rev:
            disp = f"{p['mei']} {sei}"
            note(row, "ENTRA_NAME_REVERSED", "displayName", "displayName が名・姓の逆順", "givenName/surname と employeeId で照合")
            if rev.index(p) < 3:
                given, surname = sei, p["mei"]
                note(row, "ENTRA_NAME_REVERSED", "givenName/surname", "givenName と surname も入れ替わっている", "employeeId で照合")
        elif p in rom:
            gr, sr = p["mei_rom"], p["bsei_rom"]
            disp = rng.choice([f"{gr} {sr}", f"{gr} {sr}", f"{sr.upper()} {gr}"])
            given, surname = gr, sr
            note(row, "ENTRA_NAME_ROMAJI", "displayName", f"ローマ字表記「{disp}」", "メール・employeeId で照合（姓名ローマ字は辞書変換）")
        else:
            disp = sei + wchoice([" ", "　", ""], (.8, .1, .1)) + p["mei"]
        if p["samename"]:
            note(row, "SAME_NAME_PAIR", "displayName", "同姓同名の別人が存在する", "employeeId / mail で識別")
        # 部署
        if p["person_id"] in stale:
            old = ORG[stale[p["person_id"]]]
            dep = old["name"]
            old["seen_names"].add(dep)
            note(row, "ENTRA_STALE_DEPT", "department",
                 f"廃止済みの旧組織名「{dep}」（廃止日 {old['abolished_on']}）。実際の所属は {ORG[p['work_org']]['name']}",
                 "後継組織候補: " + "、".join(ORG[s]["name"] for s in old["successor_org_ids"])
                 + f"。HR/辞令の現所属 {p['work_org']} を優先")
            row["ref"]["stale_to"] = old["org_id"]
        else:
            dep = noisy_org(ORG[p["work_org"]], 0.12, row, "department")
        # employeeId
        if id(p) in emp_empty:
            eid = None
            note(row, "ENTRA_EMPLOYEEID_EMPTY", "employeeId", "employeeId が空", "mail / 氏名 / 部署から人事パッケージの行を推定して補完")
        elif id(p) in emp_nopad:
            eid = str(p["empno_n"]) if p["company"] == "P" else f"L{p['empno_n']}"
            note(row, "EMPNO_ZEROPAD", "employeeId", f"ゼロ埋め無し「{eid}」（正規形「{p['empno']}」）", f"正規形 {p['empno']} へ補完")
        else:
            eid = p["empno"]
        enabled = True
        if p["status"] == "retired":
            enabled = id(p) in ret_enabled
            if enabled:
                note(row, "ENTRA_RETIRED_ENABLED", "accountEnabled",
                     f"{p['retired_on']} 退職済みだが accountEnabled=true", "accountEnabled=false への変更を提案")
        mail = p["email"]
        if p["person_id"] in ("",):
            pass
        comp = CO[p["secondment"]["company"] if p["secondment"] else p["company"]]["name"]
        r = rng.random()
        if r < 0.04:
            comp = "サンプル商事(株)" if "商事" in comp else "サンプル物流(株)"
            note(row, "COMPANY_NAME_VARIANT", "companyName", f"会社名の表記ゆれ「{comp}」", "正式名称へ名寄せ")
        elif r < 0.06:
            comp = None
        obj = {
            "id": p["guid"], "displayName": disp, "givenName": given, "surname": surname, "mail": mail,
            "userPrincipalName": mail, "department": dep, "jobTitle": p["title"] or None, "employeeId": eid,
            "accountEnabled": enabled, "companyName": comp, "officeLocation": p["office"],
            "businessPhones": [p["tel"]] if rng.random() < 0.8 else [], "mobilePhone": p["mobile"],
        }
        row["obj"] = obj
        val.append(row)
    # 人ではないアカウント
    NONP = [
        ("総務部 共有メールボックス", "soumu@example.co.jp", "総務部", True, "shared_mailbox"),
        ("経理 共有", "keiri@example.co.jp", "経理部", True, "shared_mailbox"),
        ("採用窓口", "saiyo@example.co.jp", "人事課", False, "shared_mailbox"),
        ("代表お問い合わせ", "info@example.co.jp", None, True, "shared_mailbox"),
        ("SVC_BACKUP", "svc-backup@example.co.jp", "情報システム部", True, "service_account"),
        ("HR連携サービス", "svc-hrsync@example.co.jp", "情報システム部", True, "service_account"),
        ("会議室 5F-A", "room-5fa@example.co.jp", None, True, "room_resource"),
        ("配送受付", "haiso@example.com", "東日本物流部", True, "shared_mailbox"),
    ]
    for name, mail, dep, en, typ in NONP:
        row = new_row(None, {"person_id": None, "kind": "non_person", "non_person_type": typ})
        note(row, "ENTRA_NON_PERSON", "displayName", f"人ではないアカウント（{typ}）", "人物として取り込まず除外 / 別種別として分類")
        row["obj"] = {
            "id": str(uuid.UUID(int=rng.getrandbits(128), version=4)), "displayName": name, "givenName": None,
            "surname": None, "mail": mail, "userPrincipalName": mail, "department": dep, "jobTitle": None,
            "employeeId": None, "accountEnabled": en,
            "companyName": CO["L" if mail.endswith("example.com") else "P"]["name"],
            "officeLocation": None, "businessPhones": [], "mobilePhone": None,
        }
        val.append(row)
    rng.shuffle(val)
    return val


entra_rows = entra_build()
missing_acct = [p for p in persons if p in hire_1001]

# ================================================================ 書き出し
def write_csv(path, header, rows, enc, fkey):
    buf = io.StringIO(newline="")
    w = csv.writer(buf, lineterminator="\r\n")
    w.writerow(header)
    for r in rows:
        cells = []
        for i, v in enumerate(r["c"]):
            if enc == "cp932":
                v2, bad = cp932_safe(v)
                if bad:
                    note(r, "CHARSET_UNMAPPABLE", header[i], f"Shift_JIS に無い文字が '?' に化けた「{v2}」", "元データでの再取得を要求")
                v = v2
            cells.append(v)
        w.writerow(cells)
    data = buf.getvalue()
    with open(path, "wb") as f:
        if enc == "utf-8-sig":
            f.write(b"\xef\xbb\xbf")
            f.write(data.encode("utf-8"))
        else:
            f.write(data.encode("cp932"))


os.makedirs(OUT, exist_ok=True)
write_csv(os.path.join(OUT, "org_chart_2026-04.csv"), ORG_COLS, org_rows, "cp932", "org")
write_csv(os.path.join(OUT, "employees_hr_package.csv"), HR_COLS, hr_rows, "utf-8-sig", "hr")
write_csv(os.path.join(OUT, "jirei_2026.csv"), JIREI_COLS, jirei_rows, "cp932", "jirei")

# 拠点マスタ（総務部が Excel で管理する事業所の一覧）。種別の空欄（拠点名から推定）・全角のコード・和暦の開設日・閉鎖済みの拠点を含む
SITE_COLS = ["会社", "拠点コード", "拠点名", "種別", "所在地", "開設日", "閉鎖日"]
SITE_TYPE_LABEL = {"head_office": "本社", "factory": "工場", "branch": "支店", "logistics": ""}
site_rows = []
for i, x in enumerate(SITES):
    row = new_row([""] * len(SITE_COLS), {"site_id": x["site_id"]})
    c = row["c"]
    c[0] = CO[x["company"]]["name"]
    c[1] = x["code"]
    if x["code"] == "S030":
        c[1] = to_full_digits(x["code"].replace("S", "Ｓ"))
        note(row, "SITE_CODE_WIDTH", "拠点コード", f"全角の拠点コード「{c[1]}」は「{x['code']}」", "半角へ正規化")
    c[2] = x["name"]
    c[3] = "支社" if x["code"] == "S030" else SITE_TYPE_LABEL[x["type"]]
    if not c[3]:
        note(row, "SITE_TYPE_BLANK", "種別", "種別が空欄", f"拠点名の末尾から推定（{x['type']}）")
    pref = next(n for code, n in [("12", "千葉県"), ("13", "東京都"), ("14", "神奈川県"), ("23", "愛知県"), ("27", "大阪府"), ("40", "福岡県")]
                if code == x["prefecture_code"])
    c[4] = pref + x["location"]
    d = dt.date.fromisoformat(x["opened_on"])
    c[5] = fmt_date(d, i % 4)
    if i % 4 != 0:
        note(row, "DATE_FORMAT_MIX", "開設日", f"ISO 以外の日付形式「{c[5]}」", f"{d.isoformat()} へ正規化")
    site_rows.append(row)
CLOSED_SITE = {"company": "L", "code": "LS30", "name": "川崎倉庫", "closed_on": "2025-03-31"}
row = new_row(["", CLOSED_SITE["code"], CLOSED_SITE["name"], "倉庫", "神奈川県川崎市川崎区9-9-8", "", "2025/3/31"], {"site_id": None})
row["c"][0] = CO["L"]["name"]
note(row, "SITE_CLOSED_REMAINS", None, "閉鎖済みの拠点（jin-ji に無い）が一覧に残っている", "修正依頼（閉鎖した拠点が jin-ji に無い）")
site_rows.append(row)
write_csv(os.path.join(OUT, "sites.csv"), SITE_COLS, site_rows, "cp932", "sites")
entra_doc = {
    "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#users(id,displayName,givenName,surname,mail,"
                      "userPrincipalName,department,jobTitle,employeeId,accountEnabled,companyName,officeLocation,"
                      "businessPhones,mobilePhone)",
    "value": [r["obj"] for r in entra_rows],
}
with open(os.path.join(OUT, "entra_users.json"), "w", encoding="utf-8") as f:
    json.dump(entra_doc, f, ensure_ascii=False, indent=2)
    f.write("\n")

# ---- 異常一覧の確定（行番号が決まってから）
for fkey, fname, rows, is_json in (("org_chart", "org_chart_2026-04.csv", org_rows, False),
                                   ("hr_package", "employees_hr_package.csv", hr_rows, False),
                                   ("jirei", "jirei_2026.csv", jirei_rows, False),
                                   ("sites", "sites.csv", site_rows, False),
                                   ("entra", "entra_users.json", entra_rows, True)):
    for i, r in enumerate(rows):
        for cat, col, desc, sugg in r["an"]:
            a = {"anomaly_id": f"A{len(ANOM) + 1:04d}", "category": cat, "file": fname, "column": col,
                 "person_id": r["ref"].get("person_id"), "org_id": r["ref"].get("org_id"),
                 "description": desc, "expected_suggestion": sugg}
            a["index" if is_json else "row"] = i if is_json else i + 1
            ANOM.append(a)
for p in missing_acct:
    ANOM.append({"anomaly_id": f"A{len(ANOM) + 1:04d}", "category": "ENTRA_MISSING_ACCOUNT", "file": "entra_users.json",
                 "column": None, "person_id": p["person_id"], "org_id": None,
                 "description": f"{p['hired_on']} 入社だが Entra に該当アカウント無し",
                 "expected_suggestion": "アカウント未作成の入社者として検出（名寄せ不能ではなく『未作成』と判定）"})
for a in ANOM:
    a["person_id"] = a["person_id"]
cnt = collections.Counter(a["category"] for a in ANOM)

# ================================================================ ground_truth.json
def person_status(p):
    return {"active": "在籍", "leave": "一時停止（休職）", "retired": "退職"}[p["status"]]


def pjson(p):
    return {
        "person_id": p["person_id"], "company": p["company"], "employee_no": p["empno"],
        "business_name": {"family": p["bsei"], "given": p["mei"], "kana_family": p["bsei_kana"], "kana_given": p["mei_kana"]},
        "legal_name": {"family": p["sei"], "given": p["mei"], "kana_family": p["sei_kana"]},
        "uses_maiden_name": p["maiden"],
        "name_variants": p["name_variants"],
        "email": p["email"], "hired_on": p["hired_on"].isoformat(),
        "status": p["status"], "status_label": person_status(p),
        "retired_on": p["retired_on"].isoformat() if p["retired_on"] else None,
        "home_org_id": p["org"], "title": p["title"] or None,
        "is_head_of_org_id": p["head_of"],
        "concurrent": [{"org_id": c["org_id"], "title": c["title"], "since": c["since"].isoformat()} for c in p["concurrent"]],
        "secondment": ({"to_company": p["secondment"]["company"], "org_id": p["secondment"]["org_id"],
                        "since": p["secondment"]["since"].isoformat()} if p["secondment"] else None),
        "leave": {"since": p["leave"]["since"].isoformat()} if p["leave"] else None,
        "leave_history": p.get("leave_history"),
        "same_name_group": p["samename"],
        "site_code": SITE_BY_NAME[p["office"]]["code"],
        "site_override": p["office"] != org_site_name(ORG[p["org"]]),
        "remote_work": p["remote_work"],
    }


def tree(o):
    return {"org_id": o["org_id"], "name": o["name"], "kind": o["kind"], "level": o["level"], "code": o["code"],
            "active": o["active"], "children": [tree(ORG[c]) for c in o["children"]]}


def ojson(o):
    return {k: (sorted(v) if k == "seen_names" else v) for k, v in o.items() if k not in ("children",)} | \
           {"observed_names": sorted(o["seen_names"]), "child_org_ids": o["children"]}


for o in orgs:
    pass
orgs_json = []
for o in orgs:
    d = {k: v for k, v in o.items() if k not in ("children", "seen_names", "head")}
    d["head_person_id"] = o["head"]
    d["child_org_ids"] = o["children"]
    d["observed_names"] = sorted(o["seen_names"])
    d["site_code"] = SITE_BY_NAME[org_site_name(o)]["code"] if o["parent_id"] is None and o["active"] else None
    orgs_json.append(d)

mappings = {
    "org_chart_2026-04.csv": [{"row": i + 1, "org_id": r["ref"]["org_id"],
                               **{k: v for k, v in r["ref"].items() if k != "org_id"}} for i, r in enumerate(org_rows)],
    "employees_hr_package.csv": [{"row": i + 1, "person_id": r["ref"]["person_id"]} for i, r in enumerate(hr_rows)],
    "jirei_2026.csv": [{"row": i + 1, **{k: v for k, v in r["ref"].items()}} for i, r in enumerate(jirei_rows)],
    "sites.csv": [{"row": i + 1, "site_id": r["ref"]["site_id"]} for i, r in enumerate(site_rows)],
    "entra_users.json": [{"index": i, "object_id": r["obj"]["id"], "kind": r["ref"]["kind"],
                          "person_id": r["ref"]["person_id"],
                          **({"non_person_type": r["ref"]["non_person_type"]} if "non_person_type" in r["ref"] else {}),
                          **({"stale_department_org_id": r["ref"]["stale_to"]} if "stale_to" in r["ref"] else {})}
                         for i, r in enumerate(entra_rows)],
}
pending = [{"jirei_row": i + 1, "person_id": r["ref"]["person_id"], "order_type": r["ref"]["order_type"],
            "effective_date": r["ref"]["date"], "target_org_id": r["ref"]["org_id"], "title": r["ref"]["title"] or None}
           for i, r in enumerate(jirei_rows) if not r["ref"]["effective"]]
truth = {
    "meta": {
        "description": "messy-corp 正解データ。全て架空。", "as_of": ASOF.isoformat(), "seed": 20260412,
        "row_numbering": "CSV は 1 始まりのデータ行番号（ヘッダ除く）。entra_users.json は value 配列の 0 始まり index。",
        "encodings": {"org_chart_2026-04.csv": "cp932 (Shift_JIS), CRLF", "jirei_2026.csv": "cp932 (Shift_JIS), CRLF",
                      "employees_hr_package.csv": "UTF-8 BOM, CRLF", "entra_users.json": "UTF-8", "sites.csv": "cp932 (Shift_JIS), CRLF"},
        "employee_no_canonical": "親会社=5桁ゼロ埋め数字(00123)、子会社=L+4桁(L0045)",
        "counts": {
            "persons": len(persons), "active_incl_leave": sum(p["status"] != "retired" for p in persons),
            "active_working": sum(p["status"] == "active" for p in persons), "leave": len(leavers),
            "retired": len(retired), "concurrent": sum(bool(p["concurrent"]) for p in persons),
            "secondment_P_to_L": len(sec_PL), "secondment_L_to_P": len(sec_LP),
            "orgs": len(orgs), "orgs_active": len(active_orgs), "orgs_abolished": len(orgs) - len(active_orgs),
            "sites": len(SITES), "remote_work": sum(p["remote_work"] for p in persons),
            "site_override": sum(p["office"] != org_site_name(ORG[p["org"]]) for p in persons),
            "anomalies": len(ANOM),
            "rows": {k: len(v) for k, v in mappings.items()},
        },
        "anomaly_category_counts": dict(sorted(cnt.items())),
        "forbidden_columns_in_hr_package": HR_FORBIDDEN,
        "notes": [
            "旧姓使用者は business_name（通称）が業務上の氏名、legal_name が戸籍名。",
            "name_variants は異体字を同一視した氏名の集合（髙橋/高橋 等）。",
            "level: 親会社 本部=1 部/室=2 課=3 チーム=4（会社直下の室は 1、会社直下の部は 2 で skipped_level）。子会社 部=1 グループ=2。",
            "出向者の home_org_id は籍のある会社の組織。出向先は secondment。Entra の department/companyName は出向先を指す。",
            "未来日付の辞令（effective=false）は pending_orders にも列挙。現在状態（persons）には反映しない。",
            "同姓同名: gt-p の same_name_group が同じ 2 名は別人。",
            "拠点: 組織は最上位の組織だけが拠点を持ち（orgs[].site_code）、配下は引き継ぐ。人の site_code は実際の勤務先の拠点で、主務の組織の拠点と違えば site_override=true（出向者・営業本部の支社勤務など）。人事パッケージの勤務地は拠点名（表記ゆれ・拠点マスタに無い値を含む）。",
            "リモートワークは有無だけ（remote_work）。人事パッケージの「リモートワーク」列は ○/可/はい/有/対象 と ×/いいえ/無/- と空欄の揺れがある（空欄は値なし）。",
        ],
    },
    "companies": COMPANIES,
    "sites": SITES,
    "closed_sites": [CLOSED_SITE],
    "persons": [pjson(p) for p in persons],
    "orgs": orgs_json,
    "org_tree": [{"company": c["id"], "name": c["name"],
                  "children": [tree(o) for o in orgs if o["company"] == c["id"] and o["parent_id"] is None]} for c in COMPANIES],
    "mappings": mappings,
    "pending_orders": pending,
    "entra_persons_without_account": [{"person_id": p["person_id"], "hired_on": p["hired_on"].isoformat()} for p in missing_acct],
    "anomalies": ANOM,
}
with open(os.path.join(OUT, "ground_truth.json"), "w", encoding="utf-8") as f:
    json.dump(truth, f, ensure_ascii=False, indent=2)
    f.write("\n")

# ================================================================ README.md
cat_rows = "\n".join(f"| `{c}` | {CATS[c][0]} | {CATS[c][1]} | {cnt[c]} |" for c in CATS if cnt[c])
forb_desc = {
    "生年月日": "要配慮でないが個人識別性が高く、人事基盤に不要", "性別": "属性情報。保持しない方針", "郵便番号": "住所の一部", "住所": "自宅住所",
    "私用電話番号": "私用連絡先", "扶養人数": "家族情報・給与計算用", "配偶者の有無": "家族情報", "等級": "評価・処遇情報",
    "号俸": "処遇（給与）情報", "休職理由": "健康・家庭事情を含み得る要配慮情報", "最新人事評価": "評価情報",
}
forb_rows = "\n".join(f"| {c} | {forb_desc[c]} |" for c in HR_FORBIDDEN)
readme = f"""# messy-corp（整っていない人事データの架空サンプル）

jin-ji の取り込み・名寄せ機能の検証用。人物・企業・メールドメインはすべて架空
（社名は「株式会社サンプル商事」「株式会社サンプル物流」、メールは `example.co.jp` / `example.com` のみ）。

再生成: `python3 generate.py`（標準ライブラリのみ・乱数は固定シード 20260412。同じ出力になる）。
基準日は {ASOF.isoformat()}（人事パッケージ・Entra・「発令済み」の境界）。

## 会社の設定

- 従業員 {len(persons)} 名（在籍 {sum(p['status'] != 'retired' for p in persons)}、うち休職中 {len(leavers)}、退職者 {len(retired)}）
- グループ 2 社: 親会社（社員番号 5 桁）{len(PP)} 名 / 子会社（`L` + 4 桁）{len(LL)} 名。出向は親→子 {len(sec_PL)} 名・子→親 {len(sec_LP)} 名
- 兼務者 {sum(bool(p['concurrent']) for p in persons)} 名（うち 1 名は子会社の部長を兼務）
- 組織 {len(orgs)} 件（うち廃止済み {len(orgs) - len(active_orgs)}）。親会社は 本部 > 部 > 課 > チーム（段飛ばし・室あり）、子会社は 部 > グループ
- 拠点 {len(SITES)} 件（本社・工場・支社・支店・物流センター）。組織は最上位の組織の行にだけ拠点があり、配下は引き継ぐ。主務の組織の拠点と違う拠点で働く人（出向者・営業本部の支社勤務など）は {sum(p["office"] != org_site_name(ORG[p["org"]]) for p in persons)} 名
- リモートワークの対象 {sum(p["remote_work"] for p in persons)} 名（有無だけ）

## ファイル

| ファイル | 文字コード | 行数 | 内容 |
| --- | --- | --- | --- |
| `org_chart_2026-04.csv` | Shift_JIS（cp932）, CRLF | {len(org_rows)} | 総務部が Excel で管理する組織図の書き出し。1 行 1 組織、上位組織名は結合セル由来で空欄 |
| `employees_hr_package.csv` | UTF-8 BOM, CRLF | {len(hr_rows)} | 人事パッケージのエクスポート。保持禁止列を含む |
| `jirei_2026.csv` | Shift_JIS（cp932）, CRLF | {len(jirei_rows)} | 辞令一覧（自由記述）。2026-04-01 の一斉異動と未来日付の内示を含む |
| `entra_users.json` | UTF-8 | {len(entra_rows)} | Microsoft Graph `GET /users` 応答形式（`value` 配列） |
| `sites.csv` | Shift_JIS（cp932）, CRLF | {len(site_rows)} | 拠点マスタ（事業所の一覧。コード・名称・種別・所在地〔事業所の住所〕・開設日・閉鎖日） |
| `ground_truth.json` | UTF-8 | - | 正解データ。人・組織・各行の対応・汚れの一覧 |
| `generate.py` | - | - | 生成スクリプト＋自己検証 |

Shift_JIS は Windows-31J（cp932）。髙・﨑 など機種依存文字を含むため `shift_jis` ではなく `cp932` で読むこと。

## 汚れのカテゴリと件数

行・セル単位で数える（`ground_truth.json` の `anomalies` に 1 件ずつ列挙。期待される補完提案付き）。

| カテゴリ | 対象 | 内容 | 件数 |
| --- | --- | --- | --- |
{cat_rows}

合計 {len(ANOM)} 件。日付形式の混在は ISO（YYYY-MM-DD）以外を全て数えている。

## 保持禁止列（人事パッケージ）

`employees_hr_package.csv` の次の列は jin-ji が保持してはならない。取り込み時に捨てられることを検証する（値はすべてダミー）。

| 列 | 理由 |
| --- | --- |
{forb_rows}

保持してよい列: {"、".join(HR_KEEP)}。

## 正解データ（ground_truth.json）

- `persons`: 正しい人の一覧（社員番号の正規形、業務上の氏名、所属、役職、兼務、出向、休職、退職）
- `orgs` / `org_tree`: 組織（段・親・組織コード・廃止日・後継組織・観測された表記）と段付きツリー
- `mappings`: 各ファイルの行 → 人/組織。辞令は発令後の所属・役職・発令済みか否かまで含む
- `pending_orders`: 未来日付の辞令（現在状態に反映してはならない）
- `sites` / `closed_sites`: 拠点マスタの正解（コード・種別・都道府県のコード・所在地）と、一覧に残る閉鎖済みの拠点。人の `site_code`（実際の勤務先の拠点）・`site_override`（主務の組織の拠点と違うか）・`remote_work`、組織の `site_code`（最上位の組織だけ）
- `anomalies`: 混ぜた不整合（カテゴリ、ファイル、行、列、説明、期待される補完提案）
- 行番号は CSV が 1 始まりのデータ行（ヘッダ除く）、Entra は `value` の 0 始まり index

## 注意点

- 親会社の社員番号は 5 桁ゼロ埋めが正規形（`00123`）。各ファイルで `123` も混ざる。子会社は `L0045`（`L45` も混ざる）。
- 同姓同名は 2 組（親会社同士 / 親会社と子会社）。氏名だけで名寄せしてはならない。
- 旧姓使用者は HR の「氏名」が戸籍姓、「通称」が業務上の氏名。Entra・辞令は主に通称。
- 辞令の社員番号誤記には、別人の番号に偶然一致する行が 1 件ある。
- Entra の `department` / `companyName` は出向者では出向先を指す。
- 人事パッケージの一部の所属は 4/1 発令を反映していない（辞令側が正）。
- 人事パッケージの「勤務地」は自由記述の拠点名（表記ゆれ・拠点マスタに無い値を含む）。jin-ji は拠点マスタに照合し、当たらなければ修正依頼にする。
"""
with open(os.path.join(OUT, "README.md"), "w", encoding="utf-8") as f:
    f.write(readme)

# ================================================================ 自己検証
def verify():
    gt = json.load(open(os.path.join(OUT, "ground_truth.json"), encoding="utf-8"))
    # Shift_JIS 系
    for fn, key in (("org_chart_2026-04.csv", "org_chart_2026-04.csv"), ("jirei_2026.csv", "jirei_2026.csv"), ("sites.csv", "sites.csv")):
        raw = open(os.path.join(OUT, fn), "rb").read()
        assert b"\r\n" in raw and not raw.startswith(b"\xef\xbb\xbf")
        rows = list(csv.reader(io.StringIO(raw.decode("cp932"), newline="")))
        assert len(rows) - 1 == len(gt["mappings"][key]), fn
        width = {len(r) for r in rows}
        assert len(width) == 1, (fn, width)
    raw = open(os.path.join(OUT, "employees_hr_package.csv"), "rb").read()
    assert raw.startswith(b"\xef\xbb\xbf") and b"\r\n" in raw
    rows = list(csv.reader(io.StringIO(raw.decode("utf-8-sig"), newline="")))
    assert rows[0] == HR_COLS and len(rows) - 1 == len(gt["mappings"]["employees_hr_package.csv"]) == len(gt["persons"])
    assert len({m["person_id"] for m in gt["mappings"]["employees_hr_package.csv"]}) == len(gt["persons"])
    ent = json.load(open(os.path.join(OUT, "entra_users.json"), encoding="utf-8"))
    assert len(ent["value"]) == len(gt["mappings"]["entra_users.json"])
    for k in ("id", "displayName", "givenName", "surname", "mail", "userPrincipalName", "department", "jobTitle",
              "employeeId", "accountEnabled", "companyName", "officeLocation", "businessPhones", "mobilePhone"):
        assert all(k in v for v in ent["value"]), k
    assert {p["site_code"] for p in gt["persons"]} <= {x["code"] for x in gt["sites"]}
    c = gt["meta"]["counts"]
    assert c["persons"] == 350 and c["retired"] == 20 and c["active_incl_leave"] == 330 and c["leave"] == 5
    assert c["concurrent"] == 10 and c["secondment_P_to_L"] == 3 and c["secondment_L_to_P"] == 3
    assert c["orgs"] == len(gt["orgs"]) == c["rows"]["org_chart_2026-04.csv"]
    ids = {p["person_id"] for p in gt["persons"]}
    oids = {o["org_id"] for o in gt["orgs"]}
    for fn, ms in gt["mappings"].items():
        for m in ms:
            if m.get("person_id"):
                assert m["person_id"] in ids
            if m.get("org_id"):
                assert m["org_id"] in oids
    for a in gt["anomalies"]:
        assert a.get("person_id") is None or a["person_id"] in ids
    # 禁止列が jin-ji 側想定の保持列と重ならないこと
    assert not set(HR_FORBIDDEN) & set(HR_KEEP)
    mailset = [v["mail"].lower() for v in ent["value"]]
    assert len(mailset) == len(set(mailset))
    print("自己検証 OK")


verify()
print("persons", len(persons), "orgs", len(orgs))
for k, v in sorted(cnt.items()):
    print(f"  {k}: {v}")
print("files:", {fn: os.path.getsize(os.path.join(OUT, fn)) for fn in sorted(os.listdir(OUT))})
