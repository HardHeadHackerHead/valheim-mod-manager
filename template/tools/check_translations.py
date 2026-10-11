"""(From Claude Tools' project template.) Checks every mod's translations/*.json against its English.json: the same keys, the same {0} {1} placeholders, valid JSON,
and every key the code uses (T("key") and "$key") present in English. Run: python tools/check_translations.py [Mod ...]"""
import io, json, os, re, sys

MODS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mods')
SHIPPED = ["English", "Russian", "German", "Chinese", "Portuguese_Brazilian", "Spanish", "French", "Polish", "Turkish"]


def placeholders(text):
    return sorted(set(re.findall(r'\{\d+\}', text)))


def check(mod):
    folder = os.path.join(MODS, mod, 'translations')
    problems = []
    try:
        english = json.load(io.open(os.path.join(folder, 'English.json'), encoding='utf-8'))
    except Exception as e:
        return [f'English.json: {e}']
    for lang in SHIPPED[1:]:
        path = os.path.join(folder, lang + '.json')
        if not os.path.exists(path):
            problems.append(f'{lang}.json missing'); continue
        try:
            words = json.load(io.open(path, encoding='utf-8'))
        except Exception as e:
            problems.append(f'{lang}.json: {e}'); continue
        for k in english:
            if k not in words: problems.append(f'{lang}: no "{k}"')
            elif placeholders(words[k]) != placeholders(english[k]):
                problems.append(f'{lang}: "{k}" has {placeholders(words[k])}, English has {placeholders(english[k])}')
            elif '<' in english[k] and words[k].count('<') != english[k].count('<'):
                problems.append(f'{lang}: "{k}" has different <tags> than English')
        for k in words:
            if k not in english: problems.append(f'{lang}: "{k}" is not in English.json')
    # keys the code uses
    prefix_keys = set()
    for root, _, files in os.walk(os.path.join(MODS, mod)):
        if any(p in root for p in ('bin', 'obj', 'thunderstore')): continue
        for f in files:
            if f.endswith('.cs'):
                src = io.open(os.path.join(root, f), encoding='utf-8').read()
                prefix_keys |= set(re.findall(r'\bT\("([a-z0-9_]+)"', src))
                prefix_keys |= {k for k in re.findall(r'"\$([a-z0-9_]+)', src) if k in english or k.split('_')[0] == next(iter(english), '_').split('_')[0]}
    for k in sorted(prefix_keys):
        if k not in english: problems.append(f'code uses "{k}", not in English.json')
    return problems


mods = sys.argv[1:] or sorted(m for m in os.listdir(MODS) if os.path.exists(os.path.join(MODS, m, 'translations', 'English.json')))
bad = 0
for mod in mods:
    p = check(mod)
    bad += len(p)
    print(f'{mod}: ' + ('ok' if not p else f'{len(p)} problem(s)'))
    for line in p: print('   ' + line)
sys.exit(1 if bad else 0)
