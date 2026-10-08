"""Armor benchmarks from loaded CO armor/lookup values; no armor overrides."""
import csv
import json
import math

TYPES = ('SlashingAttack', 'PiercingAttack', 'BluntAttack')


def combine_set(items, definition):
    """Torso only. Refuse impossible overlapping slots/layers or partial cover."""
    layers = 0
    protection = dict.fromkeys(TYPES, 0)
    flat = dict.fromkeys(TYPES, 0)
    for code in definition['items']:
        item = items[code]
        if not item['type']['Slots'] & 8 or layers & item['type']['Layers']:
            raise ValueError('Incompatible torso armor layers: ' + definition['id'])
        if item['resistsByType'] or item['resists']['DirectionsCoverage']:
            raise ValueError('Benchmark requires unconditional torso coverage: ' + code)
        layers |= item['type']['Layers']
        for kind in TYPES:
            protection[kind] += item['resists']['Resists'].get(kind, 0)
            flat[kind] += item['resists']['FlatDamageReduction'].get(kind, 0)
    return protection, flat


def multiplier(table, protection, tier):
    protection, tier = int(protection), int(tier)
    if protection == 0: return 1
    if tier == 0: return 0
    return table[max(0, min(len(table) - 1, protection - 1))][max(0, min(len(table[0]) - 1, tier - 1))]


def write_armor_docs(root, rows, policy):
    catalog = json.loads((root / 'balance/armor-catalog.json').read_text())
    items = {r['code']: r for r in catalog['items']}
    table = catalog['damageReduction']
    sets = policy['armor']['sets']
    combined = {s['id']: combine_set(items, s) for s in sets}
    results = []
    for row in rows:
        if row['context'] != 'co' or not row['canAttack']: continue
        for index, (direction, contact) in enumerate(row['directions'].items()):
            kind = contact['type']
            piercing = max(contact['ap'], row['armorPiercing'].get(kind, 0))
            for armor in sets:
                protection, flat = combined[armor['id']]
                native_multiplier = multiplier(table, protection[kind], contact['tier'] + contact['ap'])
                new_multiplier = multiplier(table, protection[kind], contact['tier'] + piercing)
                raw = row['newPower'][index]
                damage = max(0, raw - flat[kind]) * new_multiplier
                result = dict(item=row['code'], mode=row['mode'], stance=row['stance'], family=row['family'],
                    material=row['material'], direction=direction, damage_type=kind, tier=contact['tier'],
                    native_ap=contact['ap'], pvp_ap=piercing, armor=armor['id'], protection=protection[kind],
                    flat_reduction=flat[kind], raw_damage=raw,
                    previous_damage=max(0, raw - flat[kind]) * native_multiplier,
                    damage=damage, damage_multiplier=new_multiplier, cycle_ms=row['cycle'],
                    dps=damage * 1000 / row['cycle'])
                for hp in (15, 30):
                    hits = math.ceil(hp / damage) if damage > 0 else None
                    result[f'hits_{hp}hp'] = hits if hits else 'immune'
                    result[f'ttk_{hp}hp_mid_s'] = (row['timing'][0] + (row['timing'][1] + 50) / 2
                        + (hits - 1) * row['cycle']) / 1000 if hits else 'immune'
                results.append(result)
    with (root / 'balance/armor-matchups.csv').open('w', newline='', encoding='utf-8-sig') as handle:
        writer = csv.DictWriter(handle, fieldnames=list(results[0]), lineterminator='\n')
        writer.writeheader(); writer.writerows(results)
    doc = ['# Zornhau — оружие против брони, 0.8.0', '',
        'Ориентир: тяжёлый доспех заметно продлевает бой. Меняем оружие под действующую броню: '
        'урон/темп по открытой цели сохранены, для PvP с CO введены минимумы бронепробития. '
        'Сами предметы брони, рецепты, прочность, штрафы и таблица защиты остаются штатными.', '',
        '## Откуда числа', '',
        'VS 1.22.7, CO Fork 0.18.8, Armory Fork 1.11.3, Legacy 1.2.14. '
        'Стенд экспортирует предметы в GameReady: после раскрытия вариантов, patches, поведения брони '
        'и шаблонов повреждений оружия. [balance/armor-catalog.json](../balance/armor-catalog.json) '
        'содержит все загруженные предметы брони и фактическую таблицу защиты; '
        '[balance/policy.json](../balance/policy.json) — минимумы пробития и контрольные комплекты. '
        '[CSV](../balance/armor-matchups.csv) — все доступные профили CO, четыре направления, '
        'восемь комплектов, урон, DPS и пороги для 15/30 HP.', '',
        'В CO слои, защищающие поражённую область, складывают уровни защиты и плоское поглощение. '
        'Затем `урон = max(0, сырой урон − суммарное плоское поглощение) × таблица[защита−1][tier+AP−1]`. '
        'Нулевой tier+AP против положительной защиты даёт ноль; индексы ограничены размером таблицы. '
        'Числа ниже для исправной незакалённой брони, попадания основным контактом в торс ×1, '
        'без бонусов, блока, промахов, питания, регенерации и второго шанса. '
        'TTK считается от первого замаха при контакте в середине реза. '
        'Пороги считают броню постоянной: её разрушение в длинном бою может уменьшить число попаданий.', '',
        '## Пробитие оружия', '',
        'Итоговый AP = `max(родной AP контакта, минимум семейства) + бонус предмета`. '
        'Перед генерацией добавка ограничена так, чтобы базовый tier+AP не превышал '
        '8 для рубящего и 9 для колющего/дробящего контакта. Это потолок нашей добавки: '
        'штатное высокое AP и бонусы качества не срезаются. У стали поэтому меньше добавка, чем у железа. '
        'Минимум действует только для перечисленных металлических материалов и только против игроков. '
        'Не повышаем tier удара: он по-прежнему участвует в остальных правилах CO. '
        'Сервер применяет AP после проверки штатного пакета и до обработки брони, клиентский AP не является источником баланса. '
        'Контакты древком/рукоятью сохраняют меньший урон и свой тип: например, копьё не получает бонус против брони '
        'на дробящий контакт древком. Рубящие удары одиночным кинжалом сохраняют штатную конверсию CO из колющего типа.', '',
        '| Семейство | Рубящий AP | Колющий AP | Дробящий AP |', '| --- | ---: | ---: | ---: |']
    for family, floors in policy['armor']['families'].items():
        doc.append('| ' + policy['families'][family]['label'] + ' | ' + ' | '.join(str(floors.get(t, 0)) for t in TYPES) + ' |')
    doc += ['', 'Это стартовая настройка, а не измеренный винрейт. Ножи платят за скорость низким AP; '
        'булавы и колющее оружие лучше работают по тяжёлым комплектам. Посох и обычная дубина '
        'не получают пробитие металлической булавы автоматически.', '',
        '## Контрольные слои торса', '',
        '| Комплект | Рубящая защита | Колющая | Дробящая |', '| --- | ---: | ---: | ---: |']
    for armor in sets:
        protection, _ = combined[armor['id']]
        doc.append('| ' + armor['label'] + ' | ' + ' | '.join(str(protection[t]) for t in TYPES) + ' |')
    doc += ['', '## Порог 15 HP по торсу', '',
        'В ячейке: **попаданий / TTK, с**. Для мечей/булав — боковой удар; для копья/пики/кинжала — укол. '
        '', '',
        '| Оружие | Без брони | Поддоспешник + кольчуга | Полный железный комплект | Полный стальной комплект |',
        '| --- | ---: | ---: | ---: | ---: |']
    samples = [('armory:dagger-long-plain-', '', 'one', 'Thrust', 'Кинжал'),
        ('armory:sword-arming-plain-', '', 'one', 'Right', 'Одноручный меч'),
        ('armory:sword-great-plain-', '', 'two', 'Right', 'Двуручный меч'),
        ('armory:mace-plain-', '', 'one', 'Right', 'Булава'),
        ('game:spear-generic-', 'low', 'two', 'Thrust', 'Копьё, две руки'),
        ('armory:pike-plain-', '', 'two', 'Thrust', 'Пика')]
    for material in ('iron', 'steel'):
        for prefix, mode, stance, direction, label in samples:
            chosen = [r for r in results if r['item'] == prefix + material and r['mode'] == mode
                and r['stance'] == stance and r['direction'] == direction]
            if not chosen: raise ValueError('Missing armor benchmark weapon: ' + prefix + material)
            values = {r['armor']: r for r in chosen}
            cells = []
            for armor in ('bare', 'mail-iron', 'full-iron', 'full-steel'):
                value = values[armor]
                cells.append(f"{value['hits_15hp']} / {value['ttk_15hp_mid_s']:.1f}")
            doc.append('| ' + label + ' — ' + policy['materials'][material]['label'].lower() + ' | ' + ' | '.join(cells) + ' |')
    doc += ['', '## Что учитывать в живом бою', '',
        '- Удар по голове/шее в штатном CO ×2, по лицу ×1,5, по конечностям ×0,5. '
        'Торсовая таблица не заменяет проверку прикрытия головы, шеи, рук и ног: у Armory это отдельные детали.',
        '- Штатный CO «второй шанс» может остановить смертельный урон и дать 8 секунд защиты. '
        'Он не отключён этой ревизией; перед сравнением TTK проверять фактическую настройку сервера. '
        'Таблица показывает порог урона, а не обещанное время смерти при этой механике.',
        '- Качество, закалка оружия и брони, бонус tier/AP, прочность и съём слоёв меняют пороги. '
        'Таблица для новых нейтральных предметов; значения модов/config отличаются — повторить экспорт.',
        '- Щит, парирование, промахи и дистанция увеличивают длительность боя. '
        'Попадание в Zornhau прерывает атаку жертвы: быстрый кинжал остаётся опасным вблизи даже при низком AP.',
        '- Без CO применяется штатная броня Vintage Story; минимумы AP и эта матрица там не действуют. '
        'PvE, стрелы, метание, удары щитом/рукоятью через отдельный HandleAttack/Bash не перенастраиваются.', '',
        'Проверка дуэлей: одинаковое здоровье/материал, комплекты выше, оба игрока меняются оружием. '
        'Отдельно измерять торс и открытые области, число попаданий, промахов/блоков, износ и длительность; '
        'после этого менять минимумы AP, не раздувая сырой урон.', '']
    (root / 'docs/ARMOR-BALANCE.md').write_text('\n'.join(line.rstrip() for line in doc).rstrip() + '\n')
