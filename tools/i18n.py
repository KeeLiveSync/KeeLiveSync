"""Übersetzungen von KeeLiveSync pflegen und prüfen.

Deutsch und Englisch stehen im Code: T("deutsch", "english"), Text_("deutsch", "english") und
Plural(n, "de 1", "de n", "en 1", "en n"). Weitere Sprachen liegen in src/KeeLiveSync/Languages/<iso>.json und
übersetzen den englischen Text (Schlüssel = englischer Text). Polnisch braucht für Mehrzahlformen zusätzlich
"<englische Mehrzahl>#few" (2–4, 22–24 …).

  python tools/i18n.py update   neue Texte als leere Einträge in alle Sprachdateien eintragen, veraltete entfernen
  python tools/i18n.py check    Fehler, wenn ein Text fehlt, leer ist oder Platzhalter ({0} …) nicht passen
"""
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src', 'KeeLiveSync')
LANG_DIR = os.path.join(ROOT, 'Languages')
FEW = {'pl'}

LIT = r'"((?:[^"\\\n]|\\.)*)"'
CALL = re.compile(r'(?:Text\.T|Text_|(?<![\w.])T)\(\s*' + LIT + r'\s*,\s*' + LIT)
PLURAL = re.compile(r'Plural\(\s*[^,()]+,\s*' + LIT + r'\s*,\s*' + LIT + r'\s*,\s*' + LIT + r'\s*,\s*' + LIT)
PLACEHOLDER = re.compile(r'\{(\d+)[^}]*\}')


def unescape(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', '"': '"', '\\': '\\'}.get(m.group(1), m.group(1)), s)


def source_texts():
    """Englische Texte in Reihenfolge des Vorkommens; zweites Ergebnis: englische Mehrzahlformen"""
    texts, plurals = [], set()
    for folder, _, files in os.walk(ROOT):
        if os.sep + 'bin' in folder or os.sep + 'obj' in folder:
            continue
        for name in sorted(files):
            if not name.endswith('.cs'):
                continue
            code = open(os.path.join(folder, name), encoding='utf-8-sig').read()
            code = re.sub(r'^\s*//.*$', '', code, flags=re.M)
            for m in CALL.finditer(code):
                texts.append(unescape(m.group(2)))
            for m in PLURAL.finditer(code):
                one, many = unescape(m.group(3)), unescape(m.group(4))
                texts += [one, many]
                if one != many:
                    plurals.add(many)
    seen, ordered = set(), []
    for t in texts:
        if t not in seen:
            seen.add(t)
            ordered.append(t)
    return ordered, plurals


def keys_for(lang, texts, plurals):
    keys = []
    for t in texts:
        keys.append(t)
        if lang in FEW and t in plurals:
            keys.append(t + '#few')
    return keys


def languages():
    return sorted(f[:-5] for f in os.listdir(LANG_DIR) if f.endswith('.json'))


def load(lang):
    with open(os.path.join(LANG_DIR, lang + '.json'), encoding='utf-8') as f:
        return json.load(f)


def save(lang, data):
    with open(os.path.join(LANG_DIR, lang + '.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write('\n')


def update():
    texts, plurals = source_texts()
    for lang in languages():
        old = load(lang)
        new = {k: v for k, v in old.items() if k.startswith('_')}
        for k in keys_for(lang, texts, plurals):
            new[k] = old.get(k, '')
        removed = [k for k in old if k not in new]
        save(lang, new)
        missing = sum(1 for k, v in new.items() if not k.startswith('_') and not v)
        print('%s: %d Texte, %d offen, %d entfernt' % (lang, len(new) - sum(1 for k in new if k.startswith('_')), missing, len(removed)))


def check():
    texts, plurals = source_texts()
    errors = 0
    for lang in languages():
        data = load(lang)
        for k in keys_for(lang, texts, plurals):
            v = data.get(k)
            if not v:
                print('%s: fehlt: %r' % (lang, k))
                errors += 1
                continue
            want = sorted(set(PLACEHOLDER.findall(k.split('#')[0])))
            got = sorted(set(PLACEHOLDER.findall(v)))
            if want != got:
                print('%s: Platzhalter %s statt %s: %r' % (lang, got, want, k))
                errors += 1
        for k in data:
            if not k.startswith('_') and k not in keys_for(lang, texts, plurals):
                print('%s: veraltet (nicht mehr im Code): %r' % (lang, k))
                errors += 1
    print('%d Texte im Code, %d Sprachdateien, %d Fehler' % (len(texts), len(languages()), errors))
    return errors


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'check'
    if cmd == 'update':
        update()
    elif cmd == 'check':
        sys.exit(1 if check() else 0)
    else:
        print(__doc__)
        sys.exit(2)
