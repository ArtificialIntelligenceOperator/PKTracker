"""
Разведка формата hand history: какие виды строк вообще встречаются в корпусе.

    python tools/scan_lines.py "C:\\путь\\к\\истории" [--all] [--top 200]

Ники, суммы и карты заменяются заглушками, одинаковые по форме строки схлопываются в шаблон с примером.
Именно так находятся редкие случаи, которые ломают наивный парсер (Run It Twice, мёртвые блайнды, посты вне очереди,
ники с пробелами и т.п.). Скрипт ничего не пишет и не отправляет — только читает файлы.
"""
import collections
import glob
import os
import re
import sys

args = [a for a in sys.argv[1:] if not a.startswith("--")]
folder = args[0] if args else "handHistory"
show_all = "--all" in sys.argv
top = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 200

# Строка, с которой начинается раздача (подстройте под свой рум)
HAND_START = re.compile(r"^(?:Game )?Hand #|^PokerStars (?:Hand|Game) #|^Poker Hand #", re.M)
# Строка места в шапке: из неё берутся ники
SEAT_LINE = re.compile(r"^Seat \d+: (.+?) \(\$?[\d.,]+", re.M)

counter = collections.Counter()
example = {}
hands = 0

files = sorted(glob.glob(os.path.join(folder, "**", "*.txt"), recursive=True))
if not files:
    sys.exit(f"Не найдено *.txt в {folder}")

for path in files:
    text = open(path, encoding="utf-8", errors="replace").read().replace("\r\n", "\n")
    for block in HAND_START.split(text):
        if not block.strip():
            continue
        hands += 1
        names = sorted(set(SEAT_LINE.findall("Seat" + block if not block.startswith("Seat") else block)), key=len, reverse=True)
        for line in block.split("\n"):
            if not line.strip():
                continue
            pattern = line
            for n in names:
                pattern = pattern.replace(n, "<N>")
            pattern = re.sub(r"\[[^\]]*\]", "[C]", pattern)
            pattern = re.sub(r"\$?\d[\d.,]*", "#", pattern)
            counter[pattern] += 1
            example.setdefault(pattern, line.strip())

print(f"Файлов: {len(files)}   Раздач: {hands}   Видов строк: {len(counter)}\n")
rows = counter.most_common() if show_all else counter.most_common(top)
for pattern, n in rows:
    print(f"{n:7d}  {pattern[:110]}")
    if counter[pattern] <= 5:
        print(f"         пример: {example[pattern][:110]}")
if not show_all and len(counter) > top:
    print(f"\n…ещё {len(counter) - top} видов строк, покажет --all. Редкие строки в конце списка — самые опасные.")
