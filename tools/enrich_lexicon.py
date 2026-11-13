#!/usr/bin/env python3
"""
Deterministically enriches src/AutoZamena.Core/Data/lexicon_data.gz (AZLX v1):
  - Enables CandidateTarget (flag 16) on medium/long corpus-attested RU & EN words
    so typos in 6+ letter words (e.g. раскладка, установщик, директория, слитые) are indexed.
  - Adds modern everyday Russian nouns & inflected paradigms missing from OpenCorpora 2013
    (e.g. тестировщик, автозамена, нейросеть, маркетплейс, видеозвонок, удаленка).
  - Expands unambiguous orthographic & merged-word entries in ExplicitFixes.
  - Preserves all word indices / lemma IDs on existing words and remaps surface bigram indices
    after re-sorting.
"""

from __future__ import annotations

import gzip
import struct
from pathlib import Path


def shared_prefix_len(a: str, b: str) -> int:
    limit = min(len(a), len(b), 255)
    i = 0
    while i < limit and a[i] == b[i]:
        i += 1
    return i


def write_short_utf8(buf: bytearray, s: str) -> None:
    b = s.encode("utf-8")
    if len(b) > 255:
        raise ValueError(f"String too long ({len(b)} bytes): {s}")
    buf.append(len(b))
    buf.extend(b)


def expand_masc_noun(stem: str, animate: bool = False) -> list[tuple[str, int]]:
    last = stem[-1]
    velar_hush = last in "кгхжшчщ"
    hush_ts = last in "жшчщц"
    nom_pl = "и" if velar_hush else "ы"
    ins_sg = "ем" if hush_ts else "ом"
    gen_pl = "ей" if last in "жшчщ" else "ов"
    forms = [
        (stem, 325),
        (stem + "а", 295),
        (stem + "у", 270),
        (stem + ins_sg, 280),
        (stem + "е", 285),
        (stem + nom_pl, 305),
        (stem + gen_pl, 285),
        (stem + "ам", 265),
        (stem + "ами", 265),
        (stem + "ах", 265),
    ]
    return forms


def expand_fem_a_noun(word_a: str, gen_pl_zero: bool = True) -> list[tuple[str, int]]:
    root = word_a[:-1]
    last = root[-1]
    acc_pl = "и" if last in "кгхжшчщ" else "ы"
    ins_sg = "ей" if last in "жшчщц" else "ой"
    forms = [
        (word_a, 320),
        (root + acc_pl, 305),
        (root + "е", 285),
        (root + "у", 295),
        (root + ins_sg, 280),
        (root + "ам", 260),
        (root + "ами", 260),
        (root + "ах", 260),
    ]
    if gen_pl_zero and len(root) >= 4:
        # e.g. автозамена -> автозамен; if root ends with consonant cluster + к -> ок/ек
        if root.endswith("к") and len(root) >= 3 and root[-2] not in "аеёиоуыэюя":
            ep = "ек" if root[-2] in "жшчщцйь" else "ок"
            r2 = root[:-2] + ("" if root[-2] == "ь" else root[-2]) + ep
            forms.append((r2, 285))
        else:
            forms.append((root, 275))
    return forms


MODERN_RU_PARADIGMS: list[list[tuple[str, int]]] = [
    expand_masc_noun("тестировщик", animate=True),
    expand_fem_a_noun("тестировщица", gen_pl_zero=True),
    expand_fem_a_noun("автозамена", gen_pl_zero=True),
    expand_fem_a_noun("удаленка", gen_pl_zero=True),
    expand_fem_a_noun("нейросеть", gen_pl_zero=False)[:1] + [
        ("нейросеть", 330), ("нейросети", 320), ("нейросетью", 280),
        ("нейросетей", 300), ("нейросетям", 270), ("нейросетями", 270), ("нейросетях", 275),
    ],
    expand_masc_noun("маркетплейс"),
    expand_masc_noun("видеозвонок")[:1] + [
        ("видеозвонок", 315), ("видеозвонка", 285), ("видеозвонку", 270), ("видеозвонком", 275),
        ("видеозвонке", 275), ("видеозвонки", 300), ("видеозвонков", 285),
    ],
    expand_masc_noun("аудиозвонок")[:1] + [
        ("аудиозвонок", 300), ("аудиозвонка", 275), ("аудиозвонки", 285), ("аудиозвонков", 275),
    ],
    expand_masc_noun("электросамокат"),
    expand_masc_noun("каршеринг"),
    expand_masc_noun("коворкинг"),
    expand_masc_noun("установщик"),
    expand_masc_noun("инсталлятор"),
    expand_masc_noun("мессенджер"),
    expand_masc_noun("скриншот"),
    expand_fem_a_noun("раскладка", gen_pl_zero=True),
    expand_fem_a_noun("подписка", gen_pl_zero=True),
    expand_fem_a_noun("прошивка", gen_pl_zero=True),
    expand_fem_a_noun("подсветка", gen_pl_zero=True),
    expand_fem_a_noun("перезагрузка", gen_pl_zero=True),
    [("госуслуги", 325), ("госуслуг", 310), ("госуслугам", 280), ("госуслугах", 295)],
    [("автоисправление", 305), ("автоисправления", 290), ("автоисправлению", 265), ("автоисправлением", 265)],
]

EXTRA_EXPLICIT_FIXES = {
    "малако": "молоко",
    "харашо": "хорошо",
    "сичас": "сейчас",
    "сейчяс": "сейчас",
    "щастье": "счастье",
    "сщастье": "счастье",
    "низнаю": "не знаю",
    "вообщем": "в общем",
    "вобщем": "в общем",
    "кстате": "кстати",
    "каторый": "который",
    "каторая": "которая",
    "каторое": "которое",
    "каторые": "которые",
    "есле": "если",
    "канечно": "конечно",
    "конешно": "конечно",
    "скушно": "скучно",
    "нарошно": "нарочно",
    "севодня": "сегодня",
    "сигодня": "сегодня",
    "ничево": "ничего",
    "чево": "чего",
    "ково": "кого",
    "многа": "много",
    "немнога": "немного",
    "пачему": "почему",
    "девченка": "девчонка",
    "девченки": "девчонки",
    "мароженое": "мороженое",
    "пироженое": "пирожное",
    "экскалатор": "эскалатор",
    "экспрессо": "эспрессо",
    "прецендент": "прецедент",
    "инциндент": "инцидент",
    "извени": "извини",
    "извените": "извините",
    "спасиба": "спасибо",
    "пожалусто": "пожалуйста",
    "както": "как-то",
    "откудато": "откуда-то",
    "отчегото": "отчего-то",
    "врядли": "вряд ли",
    "наврядли": "навряд ли",
    "чутьли": "чуть ли",
    "едвали": "едва ли",
    "тоесть": "то есть",
    "какбудто": "как будто",
    "всеравно": "все равно",
    "досвидания": "до свидания",
    "alot": "a lot",
    "noone": "no one",
    "eachother": "each other",
    "thankyou": "thank you",
    "ofcourse": "of course",
    "atleast": "at least",
    "infront": "in front",
    "aswell": "as well",
    "infact": "in fact",
    "wont": "won't",
    "cant": "can't",
    "ive": "I've",
}


def main() -> None:
    lex_path = Path("src/AutoZamena.Core/Data/lexicon_data.gz")
    with gzip.open(lex_path, "rb") as f:
        data = f.read()

    pos = 0
    magic = data[pos : pos + 4]
    pos += 4
    assert magic == b"AZLX"
    ver = struct.unpack_from("<H", data, pos)[0]
    pos += 2
    assert ver == 1

    # 1. RU words
    ru_count, lemma_count = struct.unpack_from("<II", data, pos)
    pos += 8
    old_ru_idx_to_word: list[str] = []
    ru_map: dict[str, tuple[int, int, int]] = {}  # word -> (zipf_scaled, flags, lemma_id)
    prev = ""
    for _ in range(ru_count):
        pfx = data[pos]; pos += 1
        blen = data[pos]; pos += 1
        suf = data[pos : pos + blen].decode("utf-8"); pos += blen
        w = prev[:pfx] + suf
        z_sc, flags, lid = struct.unpack_from("<HBI", data, pos); pos += 7
        old_ru_idx_to_word.append(w)
        ru_map[w] = (z_sc, flags, lid)
        prev = w

    # 2. EN words
    en_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    old_en_idx_to_word: list[str] = []
    en_map: dict[str, tuple[int, int]] = {}
    prev = ""
    for _ in range(en_count):
        pfx = data[pos]; pos += 1
        blen = data[pos]; pos += 1
        suf = data[pos : pos + blen].decode("utf-8"); pos += blen
        w = prev[:pfx] + suf
        z_sc, flags = struct.unpack_from("<HB", data, pos); pos += 3
        old_en_idx_to_word.append(w)
        en_map[w] = (z_sc, flags)
        prev = w

    # 3. Protected words
    prot_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    prot_set: set[str] = set()
    prev = ""
    for _ in range(prot_count):
        pfx = data[pos]; pos += 1
        blen = data[pos]; pos += 1
        suf = data[pos : pos + blen].decode("utf-8"); pos += blen
        w = prev[:pfx] + suf
        prot_set.add(w)
        prev = w

    # 4. Explicit fixes
    fix_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    fixes: dict[str, str] = {}
    for _ in range(fix_count):
        blen = data[pos]; pos += 1
        k = data[pos : pos + blen].decode("utf-8"); pos += blen
        blen = data[pos]; pos += 1
        v = data[pos : pos + blen].decode("utf-8"); pos += blen
        fixes[k] = v

    # 5. RU lemma bigrams
    ru_lem_bi_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    ru_lem_bi: list[tuple[int, int, int]] = []
    for _ in range(ru_lem_bi_count):
        l1, l2, b = struct.unpack_from("<IIH", data, pos); pos += 10
        ru_lem_bi.append((l1, l2, b))

    # 6. RU surface bigrams (convert to word pairs before modifying ru_map)
    ru_surf_bi_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    ru_surf_word_bi: list[tuple[str, str, int]] = []
    for _ in range(ru_surf_bi_count):
        w1, w2, b = struct.unpack_from("<IIH", data, pos); pos += 10
        ru_surf_word_bi.append((old_ru_idx_to_word[w1], old_ru_idx_to_word[w2], b))

    # 7. EN surface bigrams
    en_surf_bi_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    en_surf_word_bi: list[tuple[str, str, int]] = []
    for _ in range(en_surf_bi_count):
        w1, w2, b = struct.unpack_from("<IIH", data, pos); pos += 10
        en_surf_word_bi.append((old_en_idx_to_word[w1], old_en_idx_to_word[w2], b))

    assert pos == len(data), f"Trailing bytes: {len(data) - pos}"

    # Remove archaic zero-corpus ghost 'еру' (blocks 'the' -> 'еру' in RU->EN layout auto-switch)
    if "еру" in ru_map and ru_map["еру"][0] <= 215:
        del ru_map["еру"]

    # Add modern RU paradigms
    next_lemma_id = lemma_count
    added_ru = 0
    for paradigm in MODERN_RU_PARADIGMS:
        existing_lid = None
        for w, _ in paradigm:
            if w in ru_map:
                existing_lid = ru_map[w][2]
                break
        if existing_lid is None:
            existing_lid = next_lemma_id
            next_lemma_id += 1

        for w, min_z_sc in paradigm:
            if w in ru_map:
                old_z, old_fl, old_lid = ru_map[w]
                ru_map[w] = (max(old_z, min_z_sc), old_fl | 16, old_lid)
            else:
                ru_map[w] = (min_z_sc, 16, existing_lid)
                added_ru += 1

    # Enable CandidateTarget (flag 16) on corpus-attested RU & EN words
    newly_targeted_ru = 0
    for w, (z_sc, fl, lid) in list(ru_map.items()):
        if not (fl & 16):
            if (len(w) >= 6 and z_sc >= 220) or (len(w) == 5 and z_sc >= 255):
                ru_map[w] = (z_sc, fl | 16, lid)
                newly_targeted_ru += 1

    newly_targeted_en = 0
    for w, (z_sc, fl) in list(en_map.items()):
        if not (fl & 16):
            if (len(w) >= 6 and z_sc >= 240) or (len(w) == 5 and z_sc >= 270):
                en_map[w] = (z_sc, fl | 16)
                newly_targeted_en += 1

    # Merge explicit fixes (ensure no explicit fix key stays in ru_map / en_map as valid word)
    fixes.update(EXTRA_EXPLICIT_FIXES)
    for bad in fixes:
        ru_map.pop(bad, None)
        en_map.pop(bad, None)

    # Protect ambiguous 3-letter tech/country codes from EN->RU false layout switching
    prot_set.update({"cif", "lng", "сво", "кпд", "мгу", "ввп", "нло", "дтп", "жкх", "зож", "сми", "вуз"})

    # Re-sort and serialize AZLX v1
    sorted_ru = sorted(ru_map.keys())
    new_ru_word_to_idx = {w: idx for idx, w in enumerate(sorted_ru)}

    sorted_en = sorted(en_map.keys())
    new_en_word_to_idx = {w: idx for idx, w in enumerate(sorted_en)}

    sorted_prot = sorted(prot_set)
    sorted_fixes = sorted(fixes.items())

    new_ru_surf_bi = sorted(
        (new_ru_word_to_idx[w1], new_ru_word_to_idx[w2], b)
        for w1, w2, b in ru_surf_word_bi
        if w1 in new_ru_word_to_idx and w2 in new_ru_word_to_idx
    )
    new_en_surf_bi = sorted(
        (new_en_word_to_idx[w1], new_en_word_to_idx[w2], b)
        for w1, w2, b in en_surf_word_bi
        if w1 in new_en_word_to_idx and w2 in new_en_word_to_idx
    )

    buf = bytearray()
    buf.extend(b"AZLX")
    buf.extend(struct.pack("<H", 1))

    # Section 1: RU words
    buf.extend(struct.pack("<II", len(sorted_ru), next_lemma_id))
    prev = ""
    for w in sorted_ru:
        z_sc, fl, lid = ru_map[w]
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        buf.extend(struct.pack("<HBI", z_sc, fl, lid))
        prev = w

    # Section 2: EN words
    buf.extend(struct.pack("<I", len(sorted_en)))
    prev = ""
    for w in sorted_en:
        z_sc, fl = en_map[w]
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        buf.extend(struct.pack("<HB", z_sc, fl))
        prev = w

    # Section 3: Protected words
    buf.extend(struct.pack("<I", len(sorted_prot)))
    prev = ""
    for w in sorted_prot:
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        prev = w

    # Section 4: Explicit fixes
    buf.extend(struct.pack("<I", len(sorted_fixes)))
    for k, v in sorted_fixes:
        write_short_utf8(buf, k)
        write_short_utf8(buf, v)

    # Section 5: RU lemma bigrams
    buf.extend(struct.pack("<I", len(ru_lem_bi)))
    for l1, l2, b in ru_lem_bi:
        buf.extend(struct.pack("<IIH", l1, l2, b))

    # Section 6: RU surface bigrams
    buf.extend(struct.pack("<I", len(new_ru_surf_bi)))
    for w1, w2, b in new_ru_surf_bi:
        buf.extend(struct.pack("<IIH", w1, w2, b))

    # Section 7: EN surface bigrams
    buf.extend(struct.pack("<I", len(new_en_surf_bi)))
    for w1, w2, b in new_en_surf_bi:
        buf.extend(struct.pack("<IIH", w1, w2, b))

    compressed = gzip.compress(bytes(buf), compresslevel=9, mtime=0)
    lex_path.write_bytes(compressed)

    print(
        f"Updated {lex_path}: {len(compressed):,} bytes gz ({len(buf):,} raw) | "
        f"RU={len(sorted_ru):,} (+{added_ru} modern forms, +{newly_targeted_ru:,} newly targeted, "
        f"total targets={sum(1 for w in sorted_ru if ru_map[w][1] & 16):,}) | "
        f"EN={len(sorted_en):,} (+{newly_targeted_en:,} newly targeted) | "
        f"ExplicitFixes={len(sorted_fixes)}"
    )


if __name__ == "__main__":
    main()
