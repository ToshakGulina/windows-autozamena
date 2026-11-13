#!/usr/bin/env python3
"""
Builds the compressed binary lexicon (src/AutoZamena.Core/Data/lexicon.bin.gz)
used by AutoZamena.Core.

Sources:
  - wordfreq (calibrated corpus Zipf frequencies for RU and EN)
  - pymorphy3 / OpenCorpora (morphological validation, lemmas, POS/inflection flags for RU)
  - pyspellchecker (orthographic validation for EN)
  - Universal Dependencies (UD_Russian-SynTagRus, UD_Russian-Taiga, UD_English-EWT) for bigrams

Binary format 'AZLX' v1 (gzipped):
  Header:
    4B magic: b'AZLX'
    uint16 version = 1
  Section 1 - Russian Words (sorted by word UTF-8, front-coded):
    uint32 count
    uint32 lemmaCount
    repeat count:
      uint8 sharedPrefixCharCount (in Unicode chars with previous word)
      string suffix (UTF-8, uint8 byteLen + bytes)
      uint16 zipfScaled (round(zipf * 100))
      uint8 flags (1=Infinitive, 2=Finite3Per, 4=TakesInf, 8=Takes3Per, 16=CandidateTarget)
      uint32 lemmaId
  Section 2 - English Words (sorted by word UTF-8, front-coded):
    uint32 count
    repeat count:
      uint8 sharedPrefixCharCount
      string suffix (UTF-8, uint8 byteLen + bytes)
      uint16 zipfScaled
      uint8 flags (16=CandidateTarget)
  Section 3 - Protected Words (sorted, front-coded):
    uint32 count
    repeat count:
      uint8 sharedPrefixCharCount
      string suffix (UTF-8, uint8 byteLen + bytes)
  Section 4 - Explicit Fixes (typo -> replacement):
    uint32 count
    repeat count:
      string typo (uint8 byteLen + UTF-8)
      string replacement (uint8 byteLen + UTF-8)
  Section 5 - Russian Lemma Bigrams:
    uint32 count
    repeat count:
      uint32 prevLemmaId
      uint32 nextLemmaId
      uint16 boostScaled (round(boost * 100), e.g. 180 = +1.80)
  Section 6 - Russian Surface Bigrams:
    uint32 count
    repeat count:
      uint32 prevWordIdx
      uint32 nextWordIdx
      uint16 boostScaled
  Section 7 - English Surface Bigrams:
    uint32 count
    repeat count:
      uint32 prevWordIdx
      uint32 nextWordIdx
      uint16 boostScaled
"""

from __future__ import annotations

import collections
import glob
import gzip
import math
import os
import re
import struct
import sys
from pathlib import Path

import pymorphy3
import wordfreq
from spellchecker import SpellChecker

CYR_RE = re.compile(r"^[а-яё]{2,28}$")
LAT_RE = re.compile(r"^[a-z]{2,24}$")

RU_SINGLE_VALID = {"а", "в", "и", "к", "о", "с", "у", "я"}
EN_SINGLE_VALID = {"a", "i"}

RU_MODAL_TAKES_INF = {
    "приходится", "пришлось", "придется", "придётся",
    "надо", "нужно", "можно", "нельзя", "пора", "необходимо", "возможно", "невозможно",
    "обязан", "обязана", "обязаны",
    "должен", "должна", "должно", "должны",
    "могу", "можешь", "может", "можем", "можете", "могут", "мог", "могла", "могло", "могли",
    "смогу", "сможешь", "сможет", "сможем", "сможете", "смогут", "смог", "смогла", "смогли",
    "хочу", "хочешь", "хочет", "хотим", "хотите", "хотят", "хотел", "хотела", "хотели",
    "хочется", "хотелось", "захотел", "захотела", "захочется",
    "буду", "будешь", "будет", "будем", "будете", "будут",
    "стал", "стала", "стало", "стали", "станет", "станут", "стану",
    "начал", "начала", "начало", "начали", "начнет", "начнёт", "начну", "начинаю", "начинает",
    "продолжаю", "продолжает", "продолжают", "продолжил", "продолжила",
    "пытаюсь", "пытаешься", "пытается", "пытаемся", "пытаются", "пытался", "пыталась", "пытались",
    "стараюсь", "старается", "стараемся", "стараются", "старался", "старалась",
    "успел", "успела", "успели", "успею", "успеет",
    "забыл", "забыла", "забыли", "забуду", "забудет",
    "решил", "решила", "решили", "решили",
    "боюсь", "боишься", "боится", "боимся", "боятся", "боялся", "боялась",
    "люблю", "любишь", "любит", "любим", "любят",
    "умею", "умеешь", "умеет", "умеем", "умеют", "умел", "умела",
    "готов", "готова", "готовы",
    "рад", "рада", "рады",
    "устал", "устала", "устали",
    "советую", "прошу", "хватит", "стоит", "стоило", "следует", "следовало",
    "удалось", "удается", "удаётся",
    "мешает", "помогает", "поможет", "заставляет", "заставил",
    "привык", "привыкла", "привыкли",
    "собираюсь", "собирается", "собираемся", "собираются", "собирался", "собиралась",
    "планирую", "планирует", "планируем", "планируют",
    "отказался", "отказалась", "отказались", "согласился", "согласилась",
}

RU_PRONOUN_TAKES_3PER = {
    "он", "она", "оно", "они", "кто",
}

PROTECTED_WORDS = {
    # RU chat & everyday abbreviations
    "спс", "пж", "плз", "плиз", "кст", "мб", "тк", "тд", "тп", "лс", "хз", "го", "ок", "окей",
    "норм", "щас", "ща", "чел", "пон", "непон", "имхо", "бтв", "рил", "изи", "сори", "сорри",
    "треш", "трэш", "кринж", "вайб", "рофл", "кек", "лол", "лмао", "омг", "втф", "ага", "угу",
    "ава", "ник", "бан", "мут", "чатик", "войс", "кружок", "стикер", "эмодзи", "реакт",
    "стрим", "стример", "донат", "подписка", "лайк", "репост", "скрин", "скриншот", "пруф",
    "чекни", "чекнуть", "скинь", "скинуть", "зацени", "краш", "тильт", "чили", "чиллить",
    "пк", "комп", "ноут", "макбук", "клава", "мышка", "моник", "проц", "видяха", "оперативка",
    "флешка", "роутер", "вайфай", "блютуз", "симка", "акк", "аккаунт", "логин", "пароль",
    # RU IT & work slang
    "деплой", "задеплоить", "коммит", "закоммитить", "пуш", "запушить", "пулл", "пуллреквест",
    "мерж", "смержить", "ребейз", "ветка", "репа", "репозиторий", "бэкенд", "фронтенд", "фронт",
    "бэк", "фуллстек", "девопс", "хоткей", "шорткат", "созвон", "дейлик", "стендап", "дедлайн",
    "апдейт", "релиз", "хотфикс", "баг", "багфикс", "фича", "фикс", "рефакторинг", "кодревью",
    "прод", "продакшн", "стейдж", "стейджинг", "дев", "апи", "апишка", "токен", "кэш", "кеш",
    "докер", "контейнер", "кубер", "кубернетес", "сервак", "бд", "миграция", "парсер", "скрипт",
    "линтер", "билдить", "сборка", "пайплайн", "воркфлоу", "админка", "дашборд", "кастомный",
    "дефолт", "дефолтный", "рандом", "рандомный", "таргет", "фидбек", "инсайт", "таска", "таск",
    # RU gaming
    "катка", "имба", "афк", "фпс", "пинг", "лаг", "лагает", "бафф", "нерф", "скилл", "лут",
    "квест", "спавн", "респавн", "хил", "танк", "дамаг", "ульта", "кд", "гг", "вп", "тима",
    "тиммейт", "камбэк", "ивент", "патч", "скин", "читы",
    # RU brands / OS / platforms
    "гитхаб", "гитлаб", "телега", "телеграм", "дискорд", "ватсап", "вайбер", "слак", "зум",
    "гугл", "яндекс", "ютуб", "тикток", "редит", "реддит", "виндовс", "винда", "линукс",
    "убунту", "макос", "айфон", "айпад", "андроид", "стим", "спотифай", "нетфликс", "чатгпт",
    # EN chat, IT, brands, dev tokens
    "pls", "plz", "thx", "ty", "tysm", "np", "yw", "btw", "imo", "imho", "idk", "ikr",
    "afaik", "tbh", "nvm", "brb", "afk", "gg", "wp", "gl", "hf", "lol", "lmao", "rofl",
    "omg", "wtf", "wth", "irl", "fyi", "asap", "eta", "diy", "faq", "tldr", "dm", "pm",
    "github", "gitlab", "bitbucket", "vscode", "jetbrains", "rider", "pycharm", "webstorm",
    "cursor", "neovim", "vim", "emacs", "dotnet", "csharp", "winforms", "wpf", "maui",
    "python", "pytorch", "numpy", "pandas", "fastapi", "django", "flask",
    "rust", "cargo", "rustc", "golang", "typescript", "javascript", "nodejs", "deno", "bun",
    "react", "nextjs", "vuejs", "nuxt", "svelte", "angular", "tailwind", "vite", "webpack",
    "docker", "podman", "kubernetes", "k8s", "helm", "terraform", "ansible", "nginx",
    "linux", "ubuntu", "debian", "fedora", "arch", "windows", "macos", "android", "ios",
    "nvidia", "cuda", "amd", "intel", "ryzen", "geforce",
    "telegram", "discord", "whatsapp", "slack", "zoom", "teams", "skype",
    "notion", "obsidian", "trello", "jira", "linear", "figma", "canva", "miro",
    "chrome", "chromium", "firefox", "safari", "brave", "vivaldi",
    "supabase", "firebase", "vercel", "netlify", "cloudflare", "aws", "azure", "gcp",
    "postgres", "postgresql", "mysql", "sqlite", "redis", "mongodb", "kafka", "elastic",
    "graphql", "grpc", "rest", "webhook", "websocket", "oauth", "jwt", "oidc", "cors",
    "api", "sdk", "cli", "gui", "tui", "ide", "orm", "cdn", "dns", "ssh", "ssl", "tls",
    "tcp", "udp", "http", "https", "ftp", "smtp", "imap", "url", "uri", "urn",
    "json", "yaml", "yml", "toml", "xml", "csv", "tsv", "html", "css", "scss", "sql",
    "utf", "ascii", "hex", "uuid", "guid", "sha", "rsa", "aes",
    "exe", "dll", "msi", "apk", "ipa", "dmg", "iso", "tar", "gz", "zip", "rar",
    "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md",
    "png", "jpg", "jpeg", "gif", "webp", "svg", "ico", "mp3", "wav", "flac", "mp4", "mkv",
    "llm", "gpt", "openai", "claude", "gemini", "ollama", "onnx",
    "async", "await", "const", "let", "var", "func", "fn", "def", "pub", "priv",
    "struct", "enum", "impl", "trait", "interface", "namespace", "using", "import",
    "bool", "int", "uint", "float", "str", "char", "void", "null", "nil", "none",
    "init", "config", "cfg", "env", "dev", "prod", "staging", "admin", "root", "sudo",
    "localhost", "repo", "pr", "mr", "ci", "cd", "qa", "ui", "ux", "pc", "os", "ram", "cpu", "gpu", "ssd",
}

EXPLICIT_FIXES = {
    # Unambiguous merged / orthographic Russian slips
    "незнаю": "не знаю",
    "немогу": "не могу",
    "нехочу": "не хочу",
    "небуду": "не буду",
    "ненадо": "не надо",
    "недолжен": "не должен",
    "нельзяже": "нельзя же",
    "потомучто": "потому что",
    "таккак": "так как",
    "вобщем": "в общем",
    "вобщемто": "в общем-то",
    "впринципе": "в принципе",
    "ксожалению": "к сожалению",
    "насамом": "на самом",
    "чутьчуть": "чуть-чуть",
    "изза": "из-за",
    "пожалуста": "пожалуйста",
    "пожайлуста": "пожалуйста",
    "здраствуйте": "здравствуйте",
    "здраствуй": "здравствуй",
    "зделал": "сделал",
    "зделала": "сделала",
    "зделали": "сделали",
    "зделать": "сделать",
    "зделаю": "сделаю",
    "зделаем": "сделаем",
    "агенство": "агентство",
    "чуствовать": "чувствовать",
    "чуствую": "чувствую",
    "чуствует": "чувствует",
    "учавствовать": "участвовать",
    "учавствую": "участвую",
    "будующее": "будущее",
    "будущем": "будущем",
    "следущий": "следующий",
    "следущая": "следующая",
    "следущее": "следующее",
    "следущие": "следующие",
    "черезчур": "чересчур",
    "придти": "прийти",
    "координально": "кардинально",
    "симпотичный": "симпатичный",
    "чот": "что",
    "што": "что",
    "привте": "привет",
    # Unambiguous Russian missing-hyphen conversational words
    "ктото": "кто-то",
    "чтото": "что-то",
    "гдето": "где-то",
    "кудато": "куда-то",
    "когдато": "когда-то",
    "почемуто": "почему-то",
    "зачемто": "зачем-то",
    "какойто": "какой-то",
    "какаято": "какая-то",
    "какоето": "какое-то",
    "какието": "какие-то",
    "чейто": "чей-то",
    "комуто": "кому-то",
    "кемто": "кем-то",
    "чемто": "чем-то",
    "ктонибудь": "кто-нибудь",
    "чтонибудь": "что-нибудь",
    "гденибудь": "где-нибудь",
    "куданибудь": "куда-нибудь",
    "когданибудь": "когда-нибудь",
    "какнибудь": "как-нибудь",
    "какойнибудь": "какой-нибудь",
    "какаянибудь": "какая-нибудь",
    "какиенибудь": "какие-нибудь",
    "комунибудь": "кому-нибудь",
    "коекто": "кое-кто",
    "коечто": "кое-что",
    "коегде": "кое-где",
    "коекак": "кое-как",
    "помоему": "по-моему",
    "потвоему": "по-твоему",
    "посвоему": "по-своему",
    "подругому": "по-другому",
    "поновому": "по-новому",
    "постарому": "по-старому",
    "похорошему": "по-хорошему",
    "порусски": "по-русски",
    "поанглийски": "по-английски",
    "вопервых": "во-первых",
    "вовторых": "во-вторых",
    "втретьих": "в-третьих",
    "всетаки": "всё-таки",
    "изпод": "из-под",
    "другдруга": "друг друга",
    # Unambiguous common English typos & contractions
    "teh": "the",
    "taht": "that",
    "adn": "and",
    "wiht": "with",
    "thier": "their",
    "recieve": "receive",
    "definately": "definitely",
    "seperate": "separate",
    "occured": "occurred",
    "untill": "until",
    "wich": "which",
    "becuase": "because",
    "beacuse": "because",
    "wierd": "weird",
    "acheive": "achieve",
    "adress": "address",
    "enviroment": "environment",
    "goverment": "government",
    "immediatly": "immediately",
    "neccessary": "necessary",
    "occassion": "occasion",
    "tommorow": "tomorrow",
    "truely": "truly",
    "dont": "don't",
    "doesnt": "doesn't",
    "didnt": "didn't",
    "isnt": "isn't",
    "arent": "aren't",
    "wasnt": "wasn't",
    "werent": "weren't",
    "couldnt": "couldn't",
    "wouldnt": "wouldn't",
    "shouldnt": "shouldn't",
    "havent": "haven't",
    "hasnt": "hasn't",
}

# Curated high-value Russian lemma bigrams (prev_lemma, next_lemma, boost)
CURATED_RU_LEMMA_BIGRAMS = [
    ("возвращаться", "назад", 2.30),
    ("вернуться", "назад", 2.30),
    ("возвращать", "назад", 2.10),
    ("вернуть", "назад", 2.10),
    ("идти", "назад", 2.00),
    ("пойти", "назад", 2.00),
    ("уходить", "назад", 1.90),
    ("шаг", "назад", 2.20),
    ("путь", "назад", 2.00),
    ("дорога", "назад", 1.95),
    ("смотреть", "назад", 2.00),
    ("посмотреть", "назад", 2.00),
    ("оглядываться", "назад", 2.20),
    ("оглянуться", "назад", 2.20),
    ("откатить", "назад", 2.10),
    ("откатываться", "назад", 2.10),
    ("сдать", "назад", 2.00),
    ("сдавать", "назад", 2.00),
    ("тянуть", "назад", 1.90),
    ("пять", "назад", 1.70),
    ("секунда", "назад", 2.25),
    ("минута", "назад", 2.30),
    ("час", "назад", 2.25),
    ("день", "назад", 2.20),
    ("неделя", "назад", 2.25),
    ("месяц", "назад", 2.25),
    ("год", "назад", 2.35),
    ("век", "назад", 2.10),
    ("немного", "назад", 1.85),
    ("чуть", "назад", 1.85),
    ("обратно", "назад", 1.80),
    ("туда", "назад", 1.75),
    ("добрый", "утро", 2.20),
    ("добрый", "день", 2.10),
    ("добрый", "вечер", 2.20),
    ("добрый", "ночь", 2.00),
    ("спокойный", "ночь", 2.30),
    ("большой", "спасибо", 2.25),
    ("огромный", "спасибо", 2.30),
    ("ничего", "страшный", 2.30),
    ("друг", "друг", 2.30),
    ("самый", "дело", 2.20),
    ("крайний", "мера", 2.25),
    ("любой", "случай", 2.20),
    ("скорый", "всего", 2.10),
    ("точка", "зрение", 2.30),
    ("обращать", "внимание", 2.25),
    ("обратить", "внимание", 2.25),
    ("иметь", "вид", 2.10),
    ("принимать", "решение", 2.20),
    ("принять", "решение", 2.20),
    ("задавать", "вопрос", 2.15),
    ("задать", "вопрос", 2.15),
    ("отвечать", "вопрос", 2.00),
    ("запах", "газ", 2.15),
    ("баллон", "газ", 2.15),
    ("красивый", "ваза", 2.05),
    ("стеклянный", "ваза", 2.15),
    ("военный", "база", 2.15),
    ("данные", "база", 2.00),
]


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


EXTRA_KNOWN_RU_SEEDS = {
    "автозамена", "тайминг", "хоткей", "телеграм", "дискорд", "фича", "коммит",
    "созвон", "триггер", "артефакт", "баг", "релиз", "апдейт", "дедлайн",
    "скриншот", "браузер", "мессенджер", "клавиатура", "раскладка", "песочница",
    "улучшить", "исправить", "проверить", "настроить", "переключить", "открыть",
    "добавить", "удалить", "сохранить", "скачать", "запустить", "обновить",
}

ARCHAIC_OR_BAD_TAGS = {"Arch", "Dist", "Erro", "V-be", "V-ey", "V-oy", "Abbr", "Init", "Infr"}
EXPANDABLE_POS = {"NOUN", "VERB", "INFN", "ADJS", "PRED", "NPRO"}

LOANWORD_MASC_CONSONANT = {
    "айпад", "акк", "багфикс", "бафф", "блютуз", "бэк", "бэкенд", "вайб", "вайбер",
    "ватсап", "войс", "гитлаб", "гитхаб", "дамаг", "дашборд", "девопс", "дейлик",
    "дискорд", "зум", "ивент", "камбэк", "коммит", "кринж", "кубер", "лут", "макбук",
    "мерж", "мут", "нерф", "нетфликс", "пайплайн", "прод", "продакшн", "пруф", "пулл",
    "пуллреквест", "пуш", "реакт", "ребейз", "реддит", "редит", "репост", "респавн",
    "рефакторинг", "рофл", "раннер", "линтер", "парсер", "докер", "сервак", "скилл",
    "скрин", "слак", "сниппет", "созвон", "спавн", "стейдж", "стейджинг", "стедап",
    "стендап", "стим", "стример", "тайминг", "таргет", "таск", "телеграм", "тикток",
    "тильт", "тиммейт", "токен", "треш", "трэш", "фидбек", "фронтенд", "фуллстек",
    "хотфикс", "чатик", "шорткат", "ютуб", "инсайт",
}

LOANWORD_MASC_Y = {"деплой", "спотифай", "хоткей", "вайфай"}

LOANWORD_FEM_A = {"автозамена", "админка", "апишка", "видяха", "винда", "имба", "ульта", "телега", "фича"}

LOANWORD_ADJ_HARD = {"дефолтный", "кастомный", "рандомный"}


def expand_protected_words(base_protected: set[str]) -> list[str]:
    expanded = set(w.lower() for w in base_protected if w)
    for stem in LOANWORD_MASC_CONSONANT:
        expanded.add(stem)
        last = stem[-1]
        nom_pl = "и" if last in "кгхжшчщ" else "ы"
        ins_sg = "ем" if last in "жшчщц" else "ом"
        gen_pl = "ей" if last in "жшчщ" else "ов"
        for suf in ("а", "у", ins_sg, "е", nom_pl, gen_pl, "ам", "ами", "ах"):
            expanded.add(stem + suf)
    for stem in LOANWORD_MASC_Y:
        root = stem[:-1]
        expanded.add(stem)
        for suf in ("я", "ю", "ем", "е", "и", "ев", "ям", "ями", "ях"):
            expanded.add(root + suf)
    for word_a in LOANWORD_FEM_A:
        root = word_a[:-1]
        last = root[-1]
        acc_pl = "и" if last in "кгхжшчщ" else "ы"
        ins_sg = "ей" if last in "жшчщц" else "ой"
        expanded.add(word_a)
        for suf in (acc_pl, "е", "у", ins_sg, "", "ам", "ами", "ах"):
            if suf or len(root) >= 4:
                expanded.add(root + suf)
    for adj in LOANWORD_ADJ_HARD:
        root = adj[:-2]
        expanded.add(adj)
        expanded.add(root + "о")  # adverb: дефолтно, кастомно, рандомно
        for suf in ("ого", "ому", "ым", "ом", "ая", "ой", "ую", "ое", "ые", "ых", "ыми"):
            expanded.add(root + suf)
    return sorted(expanded)


def build_ru_words(morph: pymorphy3.MorphAnalyzer):
    print("[1/5] Building Russian lexicon from wordfreq + pymorphy3...")
    raw_words = wordfreq.top_n_list("ru", 240_000)
    entries: dict[str, float] = {}

    for w in RU_SINGLE_VALID:
        z = wordfreq.zipf_frequency(w, "ru")
        entries[w] = z

    seed_lemmas_to_expand: set[str] = set(EXTRA_KNOWN_RU_SEEDS)

    for w in raw_words:
        if len(entries) >= 185_000:
            break
        if not CYR_RE.match(w):
            continue
        if w in EXPLICIT_FIXES:
            continue
        if not morph.word_is_known(w):
            continue
        z = wordfreq.zipf_frequency(w, "ru")
        if z < 1.85:
            continue
        entries[w] = z
        if z >= 3.30:
            seed_lemmas_to_expand.add(w)
        # Ensure standard 'е'-spelling of 'ё' words is also recognized as valid
        if "ё" in w:
            w_no_yo = w.replace("ё", "е")
            if w_no_yo not in entries and w_no_yo not in EXPLICIT_FIXES:
                z_no_yo = max(wordfreq.zipf_frequency(w_no_yo, "ru"), z - 0.15)
                entries[w_no_yo] = z_no_yo

    for prot_w in PROTECTED_WORDS:
        if CYR_RE.match(prot_w) and morph.word_is_known(prot_w):
            seed_lemmas_to_expand.add(prot_w)

    # Expand full clean paradigms (imperatives, noun/pronoun cases, finite verbs) as known valid forms
    expanded_lemmas_seen: set[str] = set()
    added_paradigm_forms = 0
    for seed in seed_lemmas_to_expand:
        if not morph.word_is_known(seed):
            continue
        top_parse = morph.parse(seed)[0]
        norm = top_parse.normal_form
        if norm in expanded_lemmas_seen:
            continue
        expanded_lemmas_seen.add(norm)
        if top_parse.tag.POS not in EXPANDABLE_POS:
            continue

        for form_parse in top_parse.lexeme:
            tag = form_parse.tag
            if tag.POS not in EXPANDABLE_POS:
                continue
            if tag.grammemes & ARCHAIC_OR_BAD_TAGS:
                continue
            fw = form_parse.word.lower()
            if not CYR_RE.match(fw) or fw in EXPLICIT_FIXES:
                continue
            if fw not in entries:
                # Known valid word (z=2.10 < 2.75 so not an aggressive SymSpell replacement target unless in corpus)
                entries[fw] = max(2.10, wordfreq.zipf_frequency(fw, "ru"))
                added_paradigm_forms += 1
            if "ё" in fw:
                fw_no_yo = fw.replace("ё", "е")
                if fw_no_yo not in entries and fw_no_yo not in EXPLICIT_FIXES:
                    entries[fw_no_yo] = max(2.10, wordfreq.zipf_frequency(fw_no_yo, "ru"))

    print(f"      Added {added_paradigm_forms:,} paradigm inflection forms for known lemmas.")

    sorted_words = sorted(entries.keys())
    lemma_to_id: dict[str, int] = {}
    ru_records = []

    for w in sorted_words:
        z = entries[w]
        parses = morph.parse(w)
        top = parses[0]
        lemma = top.normal_form.replace("ё", "е")
        lemma_id = lemma_to_id.setdefault(lemma, len(lemma_to_id))

        flags = 0
        # Check top parses with reasonable score weight for INFN vs 3per
        for p in parses[:3]:
            if p.score < 0.08:
                continue
            tag = p.tag
            if "INFN" in tag:
                flags |= 1
            if ("VERB" in tag or "PRTS" in tag) and "3per" in tag:
                flags |= 2

        w_norm = w.replace("ё", "е")
        if w in RU_MODAL_TAKES_INF or w_norm in RU_MODAL_TAKES_INF:
            flags |= 4
        if w in RU_PRONOUN_TAKES_3PER or w_norm in RU_PRONOUN_TAKES_3PER:
            flags |= 8
        if z >= 2.75 or w in RU_SINGLE_VALID:
            flags |= 16  # Eligible SymSpell correction target

        zipf_scaled = max(1, min(65535, int(round(z * 100))))
        ru_records.append((w, zipf_scaled, flags, lemma_id))

    print(
        f"      RU words: {len(ru_records):,} (SymSpell targets: "
        f"{sum(1 for r in ru_records if r[2] & 16):,}, unique lemmas: {len(lemma_to_id):,})"
    )
    return ru_records, lemma_to_id


def build_en_words():
    print("[2/5] Building English lexicon from wordfreq + pyspellchecker...")
    sp = SpellChecker(language="en")
    raw_words = wordfreq.top_n_list("en", 120_000)
    entries = {}

    for w in EN_SINGLE_VALID:
        entries[w] = wordfreq.zipf_frequency(w, "en")

    for w in raw_words:
        if len(entries) >= 65_000:
            break
        if not LAT_RE.match(w):
            continue
        if w in EXPLICIT_FIXES:
            continue
        if w not in sp:
            continue
        z = wordfreq.zipf_frequency(w, "en")
        if z < 2.10:
            continue
        entries[w] = z

    en_records = []
    for w in sorted(entries.keys()):
        z = entries[w]
        flags = 16 if (z >= 2.95 or w in EN_SINGLE_VALID) else 0
        zipf_scaled = max(1, min(65535, int(round(z * 100))))
        en_records.append((w, zipf_scaled, flags))

    print(
        f"      EN words: {len(en_records):,} (SymSpell targets: "
        f"{sum(1 for r in en_records if r[2] & 16):,})"
    )
    return en_records


def mine_ud_bigrams(
    ud_root: Path,
    ru_word_to_idx: dict[str, int],
    ru_lemma_to_id: dict[str, int],
    en_word_to_idx: dict[str, int],
):
    print("[3/5] Mining bigrams from Universal Dependencies corpora...")
    ru_lemma_bi: collections.Counter[tuple[int, int]] = collections.Counter()
    ru_lemma_uni: collections.Counter[int] = collections.Counter()
    ru_surf_bi: collections.Counter[tuple[int, int]] = collections.Counter()
    ru_surf_uni: collections.Counter[int] = collections.Counter()

    ru_files = sorted(glob.glob(str(ud_root / "UD_Russian-*" / "*.conllu")))
    for path in ru_files:
        prev_w_idx = None
        prev_l_id = None
        with open(path, encoding="utf-8") as f:
            for line in f:
                if not line or line[0] == "#":
                    continue
                if line == "\n":
                    prev_w_idx = None
                    prev_l_id = None
                    continue
                parts = line.split("\t")
                if len(parts) < 4 or "-" in parts[0] or "." in parts[0]:
                    continue
                if parts[3] == "PUNCT":
                    prev_w_idx = None
                    prev_l_id = None
                    continue
                w = parts[1].lower()
                w_norm = w.replace("ё", "е")
                l_norm = parts[2].lower().replace("ё", "е")

                w_idx = ru_word_to_idx.get(w)
                if w_idx is None:
                    w_idx = ru_word_to_idx.get(w_norm)
                l_id = ru_lemma_to_id.get(l_norm)

                if w_idx is not None:
                    ru_surf_uni[w_idx] += 1
                    if prev_w_idx is not None:
                        ru_surf_bi[(prev_w_idx, w_idx)] += 1
                    prev_w_idx = w_idx
                else:
                    prev_w_idx = None

                if l_id is not None:
                    ru_lemma_uni[l_id] += 1
                    if prev_l_id is not None:
                        ru_lemma_bi[(prev_l_id, l_id)] += 1
                    prev_l_id = l_id
                else:
                    prev_l_id = None

    total_ru_l = max(1, sum(ru_lemma_uni.values()))
    lemma_boosts: dict[tuple[int, int], float] = {}
    for (l1, l2), cnt in ru_lemma_bi.items():
        if cnt < 3:
            continue
        p12 = cnt / total_ru_l
        p1 = ru_lemma_uni[l1] / total_ru_l
        p2 = ru_lemma_uni[l2] / total_ru_l
        pmi = math.log10(max(1e-12, p12 / (p1 * p2)))
        if pmi <= 0.25:
            continue
        # Combine PMI with log count so both strong association and frequency matter
        boost = min(2.40, 0.45 * pmi + 0.32 * math.log10(cnt))
        if boost >= 0.55:
            lemma_boosts[(l1, l2)] = boost

    # Overlay curated high-value RU lemma collocations
    for prev_l, next_l, curated_boost in CURATED_RU_LEMMA_BIGRAMS:
        l1 = ru_lemma_to_id.get(prev_l.replace("ё", "е"))
        l2 = ru_lemma_to_id.get(next_l.replace("ё", "е"))
        if l1 is not None and l2 is not None:
            lemma_boosts[(l1, l2)] = max(lemma_boosts.get((l1, l2), 0.0), curated_boost)

    # Keep top 45,000 lemma bigrams by score*freq
    top_ru_lemma_bi = sorted(
        lemma_boosts.items(),
        key=lambda kv: (kv[1] + 0.15 * math.log10(1 + ru_lemma_bi[kv[0]])),
        reverse=True,
    )[:45_000]
    top_ru_lemma_bi.sort(key=lambda kv: kv[0])

    # Top RU surface bigrams (especially function words, prepositions, particles)
    total_ru_s = max(1, sum(ru_surf_uni.values()))
    surf_boosts: list[tuple[tuple[int, int], float]] = []
    for (w1, w2), cnt in ru_surf_bi.items():
        if cnt < 5:
            continue
        p12 = cnt / total_ru_s
        p1 = ru_surf_uni[w1] / total_ru_s
        p2 = ru_surf_uni[w2] / total_ru_s
        pmi = math.log10(max(1e-12, p12 / (p1 * p2)))
        if pmi <= 0.20:
            continue
        boost = min(2.20, 0.40 * pmi + 0.30 * math.log10(cnt))
        if boost >= 0.55:
            surf_boosts.append(((w1, w2), boost))

    surf_boosts.sort(key=lambda kv: kv[1], reverse=True)
    top_ru_surf_bi = sorted(surf_boosts[:25_000], key=lambda kv: kv[0])

    # Mine EN surface bigrams
    en_surf_bi: collections.Counter[tuple[int, int]] = collections.Counter()
    en_surf_uni: collections.Counter[int] = collections.Counter()
    en_files = sorted(glob.glob(str(ud_root / "UD_English-*" / "*.conllu")))
    for path in en_files:
        prev_idx = None
        with open(path, encoding="utf-8") as f:
            for line in f:
                if not line or line[0] == "#":
                    continue
                if line == "\n":
                    prev_idx = None
                    continue
                parts = line.split("\t")
                if len(parts) < 4 or "-" in parts[0] or "." in parts[0]:
                    continue
                if parts[3] == "PUNCT":
                    prev_idx = None
                    continue
                w = parts[1].lower()
                idx = en_word_to_idx.get(w)
                if idx is not None:
                    en_surf_uni[idx] += 1
                    if prev_idx is not None:
                        en_surf_bi[(prev_idx, idx)] += 1
                    prev_idx = idx
                else:
                    prev_idx = None

    total_en_s = max(1, sum(en_surf_uni.values()))
    en_boosts: list[tuple[tuple[int, int], float]] = []
    for (w1, w2), cnt in en_surf_bi.items():
        if cnt < 3:
            continue
        p12 = cnt / total_en_s
        p1 = en_surf_uni[w1] / total_en_s
        p2 = en_surf_uni[w2] / total_en_s
        pmi = math.log10(max(1e-12, p12 / (p1 * p2)))
        if pmi <= 0.20:
            continue
        boost = min(2.20, 0.40 * pmi + 0.32 * math.log10(cnt))
        if boost >= 0.55:
            en_boosts.append(((w1, w2), boost))

    en_boosts.sort(key=lambda kv: kv[1], reverse=True)
    top_en_surf_bi = sorted(en_boosts[:15_000], key=lambda kv: kv[0])

    print(
        f"      RU lemma bigrams: {len(top_ru_lemma_bi):,}, "
        f"RU surface bigrams: {len(top_ru_surf_bi):,}, "
        f"EN surface bigrams: {len(top_en_surf_bi):,}"
    )
    return top_ru_lemma_bi, top_ru_surf_bi, top_en_surf_bi


def serialize_lexicon(
    out_path: Path,
    ru_records,
    lemma_count: int,
    en_records,
    protected_words: list[str],
    explicit_fixes: dict[str, str],
    ru_lemma_bi,
    ru_surf_bi,
    en_surf_bi,
) -> None:
    print(f"[4/5] Serializing binary lexicon to {out_path}...")
    buf = bytearray()
    buf.extend(b"AZLX")
    buf.extend(struct.pack("<H", 1))  # version 1

    # Section 1: RU words
    buf.extend(struct.pack("<II", len(ru_records), lemma_count))
    prev = ""
    for w, zipf_scaled, flags, lemma_id in ru_records:
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        buf.extend(struct.pack("<HBI", zipf_scaled, flags, lemma_id))
        prev = w

    # Section 2: EN words
    buf.extend(struct.pack("<I", len(en_records)))
    prev = ""
    for w, zipf_scaled, flags in en_records:
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        buf.extend(struct.pack("<HB", zipf_scaled, flags))
        prev = w

    # Section 3: Protected words
    protected_sorted = sorted(set(w.lower() for w in protected_words if w))
    buf.extend(struct.pack("<I", len(protected_sorted)))
    prev = ""
    for w in protected_sorted:
        pfx = shared_prefix_len(prev, w)
        buf.append(pfx)
        write_short_utf8(buf, w[pfx:])
        prev = w

    # Section 4: Explicit fixes
    fixes_sorted = sorted(explicit_fixes.items())
    buf.extend(struct.pack("<I", len(fixes_sorted)))
    for typo, repl in fixes_sorted:
        write_short_utf8(buf, typo)
        write_short_utf8(buf, repl)

    # Section 5: RU lemma bigrams
    buf.extend(struct.pack("<I", len(ru_lemma_bi)))
    for (l1, l2), boost in ru_lemma_bi:
        buf.extend(struct.pack("<IIH", l1, l2, int(round(boost * 100))))

    # Section 6: RU surface bigrams
    buf.extend(struct.pack("<I", len(ru_surf_bi)))
    for (w1, w2), boost in ru_surf_bi:
        buf.extend(struct.pack("<IIH", w1, w2, int(round(boost * 100))))

    # Section 7: EN surface bigrams
    buf.extend(struct.pack("<I", len(en_surf_bi)))
    for (w1, w2), boost in en_surf_bi:
        buf.extend(struct.pack("<IIH", w1, w2, int(round(boost * 100))))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    raw_bytes = bytes(buf)
    with gzip.open(out_path, "wb", compresslevel=9) as gz:
        gz.write(raw_bytes)

    gz_size = out_path.stat().st_size
    print(
        f"[5/5] Done! Raw size: {len(raw_bytes) / (1024 * 1024):.2f} MB -> "
        f"GZip size: {gz_size / (1024 * 1024):.2f} MB"
    )


def main() -> None:
    repo_root = Path(__file__).resolve().parent.parent
    ud_root = Path(os.environ.get("UD_CACHE_DIR", "/tmp/ud_cache"))
    out_path = repo_root / "src" / "AutoZamena.Core" / "Data" / "lexicon_data.gz"

    if not ud_root.exists():
        print(f"Warning: {ud_root} not found; corpus bigrams will only include curated pairs.", file=sys.stderr)

    morph = pymorphy3.MorphAnalyzer()
    ru_records, ru_lemma_to_id = build_ru_words(morph)
    en_records = build_en_words()

    ru_word_to_idx = {r[0]: idx for idx, r in enumerate(ru_records)}
    en_word_to_idx = {r[0]: idx for idx, r in enumerate(en_records)}

    ru_lemma_bi, ru_surf_bi, en_surf_bi = mine_ud_bigrams(
        ud_root, ru_word_to_idx, ru_lemma_to_id, en_word_to_idx
    )

    serialize_lexicon(
        out_path=out_path,
        ru_records=ru_records,
        lemma_count=len(ru_lemma_to_id),
        en_records=en_records,
        protected_words=expand_protected_words(PROTECTED_WORDS),
        explicit_fixes=EXPLICIT_FIXES,
        ru_lemma_bi=ru_lemma_bi,
        ru_surf_bi=ru_surf_bi,
        en_surf_bi=en_surf_bi,
    )


if __name__ == "__main__":
    main()
