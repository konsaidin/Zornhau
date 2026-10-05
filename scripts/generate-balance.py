#!/usr/bin/env python3
"""Generate runtime profiles and balance tables from a resolved engine audit."""
from collections import defaultdict
from pathlib import Path
import argparse
import csv
import json
import math
import statistics

ROOT = Path(__file__).resolve().parents[1]
DIRECTIONS = ('Overhead', 'Right', 'Thrust', 'Left')
NATIVE = {'Overhead': 'Top', 'Thrust': 'Bottom', 'Right': 'Right', 'Left': 'Left'}
POLICY = json.loads((ROOT / 'balance/policy.json').read_text())


def family(code, tool, hands):
    path = code.split(':')[-1]
    if 'great' in path or 'claymore' in path: return 'greatsword'
    if 'pike' in path: return 'pike'
    if 'quarterstaff' in path: return 'staff'
    if 'poleaxe' in path or 'halberd' in path: return 'polearm'
    if 'axe-long' in path: return 'longaxe'
    if tool == 'Knife' or 'dagger' in path or 'knife' in path: return 'knife'
    if tool == 'Axe' or 'axe' in path or 'hatchet' in path: return 'longaxe' if hands == 2 else 'axe'
    if tool == 'Spear' or any(s in path for s in ('spear', 'trident', 'javelin')): return 'longspear' if hands == 2 else 'spear'
    if any(s in path for s in ('mace', 'hammer', 'club')): return 'club' if hands == 2 else 'mace'
    return 'longsword' if hands == 2 else 'sword'


def old_timing(code, tool, co):
    path = code.split(':')[-1].replace('claymore', 'great')
    if not co and tool in ('Spear', 'Axe', 'Knife'): path = tool.lower()
    if 'great' in path: return [500, 380, 650]
    if 'pike' in path: return [450, 320, 580]
    if any(s in path for s in ('poleaxe', 'halberd', 'quarterstaff')): return [500, 360, 600]
    if any(s in path for s in ('dagger', 'knife')): return [220, 180, 320]
    if any(s in path for s in ('axe', 'hatchet')): return [450, 300, 550]
    if any(s in path for s in ('mace', 'hammer', 'club')): return [430, 300, 520]
    if any(s in path for s in ('spear', 'javelin', 'trident')): return [380, 280, 450]
    return [330, 260, 480]


def material(code):
    value = code.split('-')[-1]
    if value.startswith('bone'): return 'bone'
    if value in ('chert', 'granite', 'andesite', 'basalt', 'obsidian', 'peridotite', 'flint'): return 'stone'
    if value in ('ornategold', 'ornatesilver'): return value[6:]
    return value if value in POLICY['materials'] else 'special'


def attack(stance, direction, diagonals=True):
    attacks = stance.get('DirectionalAttacks', {})
    key = NATIVE[direction]
    if key in attacks: return attacks[key]
    if diagonals and direction in ('Right', 'Left'):
        for diagonal in ('Bottom' + key, 'Top' + key):
            if diagonal in attacks: return attacks[diagonal]
    return stance.get('Attack') or next(iter(attacks.values()), {})


def source_damage(item, stance, direction, diagonals=True):
    stats = attack(stance, direction, diagonals)
    types = stats.get('DamageTypes', [])
    if not types:
        return {'power': item['power'], 'type': 'PiercingAttack' if direction == 'Thrust' else 'SlashingAttack',
                'tier': item['tier'], 'ap': 0, 'reach': None, 'radius': None}
    strongest = max(types, key=lambda t: t.get('Damage', {}).get('Damage', 0))
    damage = strongest['Damage']
    # CO's MaxReach is only a search envelope. Our server uses collider + 1.5 m.
    from math import sqrt
    extent = max((sqrt(sum((t['Collider'][i] + t['Collider'][i + 3]) ** 2 for i in range(3)))
                  for t in types if len(t.get('Collider', [])) == 6), default=0)
    reach = min(6, max(1.5, min(stats.get('MaxReach', 9), extent + 1.5)))
    return {'power': damage['Damage'], 'type': damage['DamageType'], 'tier': damage.get('Tier', 0),
            'ap': damage.get('ArmorPiercingTier', 0), 'reach': round(reach, 3), 'radius': strongest.get('Radius', .1)}


def bonus(tool, direction):
    return 1.12 if direction == 'Overhead' else 1.15 if tool == 'Spear' and direction == 'Thrust' else 1


def cost_index(data):
    index = defaultdict(list)
    for kind, recipes in data['recipes'].items():
        for recipe in recipes:
            code = recipe.get('output', {}).get('Code')
            if not code: continue
            inputs = list(recipe.get('ingredients', {}).values()) or [recipe.get('ingredient', {})]
            if any(i.get('Code') == code for i in inputs): continue  # decoration/quenching/repair, not manufacture
            index[code].append((kind, recipe))
    return index


def crafting(code, index, visited=()):
    if code in visited or len(visited) >= 5: return [], []
    candidates = index.get(code, [])
    if not candidates: return [], []
    kind, recipe = candidates[0]
    # Retain the actual resolved head + assembly chain; do not convert voxels
    # into fictional ingot counts or minutes of gathering/working.
    ingredients = []
    for symbol, ingredient in recipe.get('ingredients', {}).items():
        ingredient = dict(ingredient)
        repeats = recipe.get('grid', '').count(symbol) if kind == 'GetGridRecipes' and recipe.get('grid') else 1
        ingredient['Quantity'] = ingredient.get('Quantity', 1) * repeats
        ingredients.append(ingredient)
    ingredients = ingredients or [recipe.get('ingredient', {})]
    output_quantity = recipe['output'].get('Quantity', 1)
    steps, forged = [], []
    if kind == 'GetSmithingRecipes':
        pattern = recipe.get('pattern', [])
        def voxels(x): return x.count('#') if isinstance(x, str) else sum(voxels(v) for v in x)
        forged.append({'part': code, 'voxelsPerOutput': voxels(pattern) / output_quantity,
                       'ingredient': ingredients[0].get('Code'), 'recipe': recipe['name']})
    else:
        actual = [f"{i.get('Quantity', 1)}×{i.get('Code')}" for i in ingredients if not i.get('IsTool')]
        tools = [i.get('Code') for i in ingredients if i.get('IsTool')]
        steps.append({'output': code, 'quantity': output_quantity, 'inputs': actual, 'tools': tools,
                      'kind': kind.replace('Get', '').replace('Recipes', ''), 'recipe': recipe['name']})
    for i in ingredients:
        part = i.get('Code', '')
        if not i.get('IsTool') and ('head' in part or ':part-' in part or ':handle-' in part):
            child, smithing = crafting(part, index, visited + (code,))
            steps.extend(child); forged.extend(smithing)
    return steps, forged


def refresh():
    result = []
    for context in ('vanilla', 'co'):
        data = json.loads((ROOT / f'.artifacts/balance-{context}.json').read_text())
        recipes = cost_index(data)
        for item in data['items']:
            attrs = item['attributes']
            co = bool('Modes' in attrs or 'OneHandedStance' in attrs or 'TwoHandedStance' in attrs)
            if not co and item['tool'] not in ('Sword', 'Spear', 'Axe', 'Knife'): continue
            if 'admin' in item['code'] or 'devimpale' in item['code']: continue
            steps, forged = crafting(item['code'], recipes)
            modes = attrs.get('Modes', {'': attrs})
            for mode, stats in modes.items():
                stances = {'one': stats.get('OneHandedStance', {})}
                if co and 'TwoHandedStance' in stats: stances['two'] = stats['TwoHandedStance']
                if not co and (item['tool'] == 'Spear' or 'claymore' in item['code']):
                    stances['two'] = {}
                for pattern, stance in stats.get('MainHandDualWieldStances', {}).items(): stances['dual:' + pattern] = stance
                for name, stance in stances.items():
                    can_attack = not co or stance.get('CanAttack', False)
                    # A true two-handed-only vanilla ruin is not an ordinary
                    # one-handed craft. Keep the stock offhand exception visible.
                    result.append({'context': context, 'code': item['code'], 'mode': mode, 'stance': name,
                        'hands': 2 if name == 'two' else 1, 'canAttack': can_attack,
                        'tool': item['tool'], 'material': material(item['code']), 'durability': item['durability'],
                        'native': co, 'oldTiming': old_timing(item['code'], item['tool'], co),
                        'directions': {d: source_damage(item, stance, d) for d in DIRECTIONS},
                        'oldDirections': {d: source_damage(item, stance, d, False) for d in DIRECTIONS},
                        'crafting': steps, 'smithing': forged})
    (ROOT / 'balance/catalog.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    print('Captured resolved profiles:', len(result))


def generate():
    catalog = json.loads((ROOT / 'balance/catalog.json').read_text())
    runtime = {c: defaultdict(list) for c in ('vanilla', 'co')}
    classes = defaultdict(list)
    detail = []
    for row in catalog:
        fam = family(row['code'], row['tool'], row['hands'])
        timing = POLICY['families'][fam]
        w, s, r = (timing[k] for k in ('windup', 'strike', 'recovery'))
        cycle = w + s + 50 + r
        goal = POLICY['materials'][row['material']]['dps'] * (POLICY['twoHandedDpsMultiplier'] if row['hands'] == 2 else 1) * timing['dpsMultiplier']
        mean_bonus = statistics.mean(bonus(row['tool'], d) for d in DIRECTIONS)
        power = round(goal * cycle / 1000 / mean_bonus, 6)
        profile = {'Mode': row['mode'], 'Stance': row['stance'], 'CanAttack': row['canAttack'],
                   'Class': row['material'] + '-' + str(row['hands']) + 'h', 'Family': fam,
                   'Windup': w, 'Strike': s, 'Recovery': r, 'Power': power,
                   'SourcePower': [row['directions'][d]['power'] for d in DIRECTIONS]}
        runtime[row['context']][row['code']].append(profile)
        oldw, olds, oldr = row['oldTiming']
        oldpower = [row['oldDirections'][d]['power'] * bonus(row['tool'], d) for d in DIRECTIONS]
        newpower = [power * bonus(row['tool'], d) for d in DIRECTIONS]
        summary = dict(row, family=fam, timing=[w, s, r], cycle=cycle, target=goal, newPower=newpower,
                       newDps=statistics.mean(newpower) * 1000 / cycle,
                       oldDpsEarly=statistics.mean(oldpower) * 1000 / (oldw + oldr),
                       oldDpsMid=statistics.mean(oldpower) * 1000 / (oldw + olds / 2 + oldr),
                       oldDpsLate=statistics.mean(oldpower) * 1000 / (oldw + olds + 50 + oldr))
        detail.append(summary)
        if row['canAttack']: classes[(row['material'], row['hands'], row['context'])].append(summary)
    asset = ROOT / 'assets/warbandcombat/config/balance-profiles.json'
    asset.write_text(json.dumps(runtime, ensure_ascii=False, separators=(',', ':')) + '\n')
    write_docs(detail, classes)
    print(f'Generated {len(detail)} balance profiles and detailed tables')


def write_docs(rows, classes):
    doc = ['# Баланс оружия 0.6.0', '',
        'Снимок реально загруженных предметов и рецептов VS 1.22.7; CO Fork 0.18.8, Armory Fork 1.11.3, Overhaullib Legacy 1.2.14. '
        'Варианты и ByType уже раскрыты самим движком. `balance/catalog.json` содержит исходные числа и цепочки рецептов; '
        '`balance/policy.json` задаёт цели; `scripts/generate-balance.py` генерирует эту таблицу и runtime-профили из одного источника.', '',
        '## Методика и границы сравнения', '',
        '- Класс = стадия материала × число занятых рук. Числа не претендуют на точные минуты добычи: '
        'доступ к рудам и технологиям зависит от мира. Рецепты, головки, рукояти, инструменты и кузнечные воксели записаны отдельно.',
        '- Цепочка показывает один доступный рецепт изготовления с раскрытыми вариантами и количеством ячеек. '
        'Количество относится к партии с указанным выходом в CSV. Кузнечные воксели приведены на одну заготовку; '
        'это геометрия детали, а не расход слитков с отходами. Альтернативные пути литья и расход топлива не оценены.',
        '- Материал задаёт базовый бюджет класса, семейство — поправку за скорость, дистанцию и цену промаха. '
        'Равенство DPS всех семейств заменено диапазоном ×0,90–1,08. Внутри одинакового семейства/материала/хвата цели одинаковы. '
        'Двуручный хват получает ×1,10 за отказ от щита/другого предмета; дорогие материалы дают умеренный прирост.',
        '- Подробное сравнение с принципами Mount & Blade, Mordhau и Chivalry, источники и обоснование коэффициентов: '
        '[BALANCE-RESEARCH.md](BALANCE-RESEARCH.md). Проценты — наша проверяемая гипотеза, не числа, скопированные из этих игр.',
        '- Цикл `T = подготовка + рез + 50 мс доворота + восстановление`. Попадание/блок заканчивает активный рез сразу, '
        'но следующая атака разрешена после исходного T. Ранний контакт больше не ускоряет темп.',
        '- DPS = среднее арифметическое урона четырёх направлений / T в секундах. '
        'Верхний удар ×1,12; укол предметом с Tool=Spear ×1,15; прочие ×1. '
        'Удержание сверх минимального замаха не повышает урон и увеличивает реальный цикл.',
        '- Числа относятся к основному повреждающему контакту, нейтральным характеристикам и цели без брони. '
        'Броня, область тела, бронепробитие, закалка, качества предмета, баффы, парирования и процент попаданий меняют фактический DPS. '
        'Слабые контакты рукоятью/древком сохраняют отношение исходного урона к основному контакту.',
        '- Старый DPS показан для раннего контакта, середины реза и позднего контакта; '
        'новый DPS одинаков по темпу для всех моментов контакта. Цикл промаха также T.',
        '- Стрельба, метание, удар щитом, CO HandleAttack/Bash и административные предметы не включены в нормализацию. '
        'Обычное использование ножей/топоров как инструментов сохраняется.',
        '- Новые профили выбираются по реальной стойке и режиму на клиенте и сервере. '
        'Стойка с CanAttack=false не получает разрешения на начало удара.', '',
        '## Классы стоимости', '',
        '| Материал | Руки | Набор | Профилей | Старый средний DPS (середина реза) | Новый средний / min–max DPS | Технология |',
        '| --- | ---: | --- | ---: | ---: | ---: | --- |']
    for (mat, hands, context), values in sorted(classes.items(), key=lambda pair: (POLICY['materials'][pair[0][0]]['dps'], pair[0])):
        m = POLICY['materials'][mat]
        old = statistics.mean(v['oldDpsMid'] for v in values)
        new = [v['newDps'] for v in values]
        doc.append(f"| {m['label']} | {hands} | {context} | {len(values)} | {old:.3f} | {statistics.mean(new):.3f} / {min(new):.3f}–{max(new):.3f} | {m['work']} |")
    doc += ['', 'Пустой класс не создаёт новых предметов. Например, медного большого меча в установленном Armory нет; '
            'медные двуручные стойки доступны у посоха, дубины, копья и трезубца.', '', '## Тайминги семейств', '',
            '| Семейство | Подготовка, мс | Рез, мс | Доворот, мс | Базовый возврат, мс | Полный цикл, мс | Ударов/с | Множитель DPS класса | Роль |',
            '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |']
    for f in POLICY['families'].values():
        t = f['windup'] + f['strike'] + 50 + f['recovery']
        doc.append(f"| {f['label']} | {f['windup']} | {f['strike']} | 50 | {f['recovery']} | {t} | {1000/t:.3f} | ×{f['dpsMultiplier']:.2f} | {f['role']} |")
    doc += ['', '## Разовый урон и пороги убийства', '',
            'Контрольный пример: **железо, боковой удар**, цель с **15 HP без брони**, каждый рез попадает основным '
            'контактом, нет баффов, блока, промахов, регенерации или движения. Это расчёт стенда, не результат дуэли. '
            '15 HP — базовое здоровье в assets/game/entities/humanoid/player.json VS 1.22.7; питание и настройки мира могут его изменить. '
            'Количество ударов `N = ceil(HP / урон)`. Время от начала первого замаха: '
            '`TTK = подготовка + время контакта внутри реза + (N−1)×цикл`. Поздний контакт добавляет рез + 50 мс. '
            'В CSV есть эти значения по каждому направлению для 15 и условных 30 HP.', '',
            '| Семейство | Хват примера | Боковой урон | DPS | Ударов до 15 HP | TTK ранний–поздний, с |',
            '| --- | --- | ---: | ---: | ---: | --- |']
    for key, family_stats in POLICY['families'].items():
        hands = 2 if key in ('longsword', 'greatsword', 'longaxe', 'club', 'longspear', 'pike', 'polearm', 'staff') else 1
        sample = next((r for r in rows if r['canAttack'] and r['material'] == 'iron' and r['family'] == key and r['hands'] == hands), None)
        if sample is None: continue
        damage = sample['newPower'][1]; hits = math.ceil(15 / damage)
        first = (sample['timing'][0] + (hits-1)*sample['cycle']) / 1000
        last = first + (sample['timing'][1]+50)/1000
        doc.append(f"| {family_stats['label']} | {hands}р | {damage:.3f} | {sample['newDps']:.3f} | {hits} | {first:.3f}–{last:.3f} |")
    doc += ['', 'После раннего контакта фактическое восстановление = остаток реза/доворота + базовый возврат. '
            'Удерживаемая защита не расходует этот цикл.', '', '## Остальные боевые таймеры', '',
            '| Механика | мс | Условия |', '| --- | ---: | --- |',
            '| Подъём блока оружием | 100 | Направление фиксируется при нажатии |',
            '| Подъём щита | 180 | Все четыре направления, только спереди |',
            '| Снятие защиты | 80 | Перед новой атакой |',
            '| Отмена замаха / финт | 120 | Выпущенный удар отменять нельзя |',
            '| Оглушение атакующего парированием | 480 | Минимум от контакта; max с оставшимся восстановлением, не добавляется к нему |',
            '| Отскок от щита | 180 | Минимум от контакта; max с оставшимся восстановлением, не добавляется к нему |',
            '| Поломка щита | 350 | Восстановление владельца щита |',
            '| Блокировка спринта после атаки | 1500 | От фактического контакта либо конца промаха |',
            '| Истечение удержания без heartbeat | 800 | Защита от зависшей кнопки/разрыва связи |',
            '| Серверный тик боёвки | 20 | Добавляет до одного тика ожидания перехода |',
            '| Heartbeat клиента | 200 | Поддерживает удержание, не меняет направление |',
            '| Периодический снимок состояния | 150 | Начало/контакт отправляются также сразу |',
            '| История цели: интервал / допустимая давность | 50 / 150–350 | По ping, смещение не более 2 м |',
            '| Сброс истории оружия/цели при разрыве наблюдения | 125 | Не соединять несмежные снимки одним sweep |',
            '| Таймаут диагностического подтверждения попадания | 2000 | Не означает автоматически принятый удар |', '',
            '## Все предметы и стойки', '',
            'Урон указан в порядке **верх / право / укол / лево**. «1», «2», «dual» — активный хват. '
            '«—» означает недоступную атаку, а не оружие с нулевым уроном. '
            'CSV рядом содержит отдельные строки для каждого направления, тип урона, tier, бронепробитие, прочность и источники рецептов.', '',
            '| Набор | Предмет | Режим / хват | Класс | Старые П/Р/В, мс | Новые П/Р/В, мс | Цикл, мс | Новый урон В/П/У/Л | Старый DPS ранний / средний / поздний | Новый DPS | Рецепт / ковка |',
            '| --- | --- | --- | --- | --- | --- | ---: | --- | --- | ---: | --- |']
    csv_rows = []
    for row in sorted(rows, key=lambda v: (v['context'], v['material'], v['code'], v['mode'], v['stance'])):
        m = POLICY['materials'][row['material']]['label']
        craft = '; '.join(f"{s['output']} ← {', '.join(s['inputs'])}" for s in row['crafting'])
        smith = '; '.join(f"{s['part']}: {s['voxelsPerOutput']:g} вокс./шт." for s in row['smithing'])
        costs = (craft + '; ' + smith).strip('; ') or 'Обычный рецепт не найден в загруженном наборе'
        damage = '/'.join(f'{d:.2f}' for d in row['newPower']) if row['canAttack'] else '—'
        dps = f"{row['newDps']:.3f}" if row['canAttack'] else '—'
        olddps = ' / '.join(f"{row[k]:.3f}" for k in ('oldDpsEarly', 'oldDpsMid', 'oldDpsLate')) if row['canAttack'] else '—'
        doc.append(f"| {row['context']} | `{row['code']}` | {row['mode'] or 'обычный'} / {row['stance']} | {m}, {row['hands']}р | "
                   f"{'/'.join(map(str,row['oldTiming']))} | {'/'.join(map(str,row['timing']))} | {row['cycle']} | {damage} | {olddps} | {dps} | {costs} |")
        for i, direction in enumerate(DIRECTIONS):
            origin = row['directions'][direction]
            csv_rows.append({'context': row['context'], 'item': row['code'], 'mode': row['mode'], 'stance': row['stance'],
                'can_attack': row['canAttack'], 'material': row['material'], 'hands': row['hands'], 'family': row['family'], 'direction': direction,
                'family_dps_multiplier': POLICY['families'][row['family']]['dpsMultiplier'],
                'old_power': row['oldDirections'][direction]['power'], 'new_base_power': row['newPower'][i]/bonus(row['tool'], direction),
                'direction_multiplier': bonus(row['tool'], direction), 'new_direction_power': row['newPower'][i],
                'old_windup_ms': row['oldTiming'][0], 'old_strike_ms': row['oldTiming'][1], 'old_recovery_ms': row['oldTiming'][2],
                'windup_ms': row['timing'][0], 'strike_ms': row['timing'][1], 'tail_ms': 50, 'recovery_ms': row['timing'][2],
                'cycle_ms': row['cycle'], 'average_dps': row['newDps'] if row['canAttack'] else '',
                'direction_dps': row['newPower'][i]*1000/row['cycle'] if row['canAttack'] else '',
                'damage_type': origin['type'], 'tier': origin['tier'], 'armor_piercing': origin['ap'],
                'server_reach_bound_m': origin['reach'] or '', 'source_collider_radius_m': origin['radius'] or '',
                'effective_collider_radius_m': max(.1, origin['radius']) + .03 if origin['radius'] is not None else '',
                'durability': row['durability'], 'crafting': json.dumps(row['crafting'], ensure_ascii=False),
                'smithing': json.dumps(row['smithing'], ensure_ascii=False)})
            for hp in (15, 30):
                hits = math.ceil(hp / row['newPower'][i]) if row['canAttack'] else None
                csv_rows[-1][f'hits_to_kill_{hp}hp'] = hits or ''
                for name, fraction in (('early', 0), ('mid', .5), ('late', 1)):
                    csv_rows[-1][f'ttk_{hp}hp_{name}_s'] = ((hits-1)*row['cycle'] + row['timing'][0]
                        + fraction*(row['timing'][1]+50))/1000 if hits else ''
    doc += ['', '## Что ещё проверять в дуэлях', '',
        'Сходный бюджет класса не означает одинаковую вероятность победы. Проверить щит против двуручника, '
        'дистанцию пики против ножа, тупой/колющий урон против разных доспехов, довороты, попадания по разным частям тела, '
        'пинг и цену расхода прочности. Успешное попадание пока не имеет отдельного гарантированного stun/отмены атаки жертвы; '
        'это остаётся отдельной механикой, не частью формулы DPS. Числа этой ревизии — исходная точка для таких дуэлей, '
        'а не измеренный винрейт.', '']
    (ROOT / 'WEAPON-BALANCE.md').write_text('\n'.join(doc))
    with (ROOT / 'balance/weapon-balance.csv').open('w', newline='', encoding='utf-8-sig') as handle:
        writer = csv.DictWriter(handle, fieldnames=list(csv_rows[0]))
        writer.writeheader(); writer.writerows(csv_rows)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--refresh-catalog', action='store_true')
    if parser.parse_args().refresh_catalog: refresh()
    generate()
