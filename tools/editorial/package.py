"""Create existing Duora publication DTOs offline; approval is an explicit, bound input."""
import argparse
import base64
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import struct


def approval_digest(data):
    covered = {key: value for key, value in data.items() if key != 'review'}
    return hashlib.sha256(json.dumps(covered, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def text(value, maximum):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError('Missing or overlong editorial text')
    return value


def slug(value):
    if not isinstance(value, str) or len(value) > 64 or not re.fullmatch(r'[a-z0-9]+(?:-[a-z0-9]+)*', value):
        raise ValueError('Invalid catalog identifier')
    return value


def structure(content, kind):
    if not isinstance(content, dict):
        raise ValueError('Missing locale')
    text(content.get('title'), 280)
    text(content.get('objective'), 120)
    items = content.get('cards' if kind == 'constellation' else 'stops')
    if not isinstance(items, list) or not 1 <= len(items) <= 6:
        raise ValueError('Invalid card/stop count')
    result = []
    for item in items:
        if not isinstance(item, dict):
            raise ValueError('Missing card/stop')
        key = slug(item.get('cardId' if kind == 'constellation' else 'stepId'))
        text(item.get('prompt'), 280)
        options = item.get('options')
        if not isinstance(options, list) or not 2 <= len(options) <= 4:
            raise ValueError('Invalid options')
        ids = []
        for option in options:
            ids.append(slug(option.get('optionId')))
            text(option.get('label'), 280)
        if len(set(ids)) != len(ids):
            raise ValueError('Duplicate options')
        result.append((key, ids))
    if len({key for key, _ in result}) != len(result):
        raise ValueError('Duplicate cards/stops')
    if kind == 'unlikely-routes':
        text(content.get('example'), 280)
        clues = content.get('clueSets')
        if not isinstance(clues, list) or len(clues) != 2:
            raise ValueError('Two private clue sets required')
        for group in clues:
            if not isinstance(group, list) or not 1 <= len(group) <= 4:
                raise ValueError('Invalid private clues')
            for clue in group:
                text(clue.get('text'), 280)
        if {clue['text'] for clue in clues[0]} & {clue['text'] for clue in clues[1]}:
            raise ValueError('Private clue sets must be disjoint')
        result.append(('clue-counts', [len(group) for group in clues]))
    return result


def audio_bytes(root, variant):
    relative = variant.get('path')
    if not isinstance(relative, str) or Path(relative).is_absolute():
        raise ValueError('Audio path must stay in the package')
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError('Audio path must stay in the package')
    try:
        raw = path.read_bytes()
    except OSError as error:
        raise ValueError('Missing audio file') from error
    if hashlib.sha256(raw).hexdigest() != variant.get('sha256'):
        raise ValueError('Audio hash mismatch')
    if not 46 <= len(raw) <= 6 * 1024 * 1024:
        raise ValueError('Audio byte bounds exceeded')
    riff, size, wave, fmt, fmt_size, pcm, channels, rate, byte_rate, align, bits, data, data_size = struct.unpack('<4sI4s4sIHHIIHH4sI', raw[:44])
    if (riff, wave, fmt, fmt_size, pcm, bits, data) != (b'RIFF', b'WAVE', b'fmt ', 16, 1, 16, b'data') or size != len(raw)-8 or data_size != len(raw)-44:
        raise ValueError('Expected canonical PCM WAV')
    if not 1 <= channels <= 2 or not 8000 <= rate <= 48000 or align != channels * 2 or byte_rate != rate * align or data_size % align or data_size > byte_rate * 60:
        raise ValueError('Unsupported PCM dimensions')
    if not any(raw[44:]):
        raise ValueError('Silent placeholder audio is not editorial speech')
    return raw


def package_catalog(data, root, approval=None):
    approved = False
    if approval is not None:
        required = ('reviewer', 'approvedAt', 'catalogSha256', 'editorialApproved', 'rightsVerified', 'audioListened')
        if not all(approval.get(key) for key in required) or any(approval.get(key) is not True for key in ('editorialApproved', 'rightsVerified', 'audioListened')) or approval.get('catalogSha256') != approval_digest(data):
            raise ValueError('Incomplete or stale human approval')
        try:
            text(approval['reviewer'], 200)
            if datetime.fromisoformat(approval['approvedAt'].replace('Z', '+00:00')).tzinfo is None:
                raise ValueError('Approval time requires a timezone')
        except (TypeError, ValueError, AttributeError) as error:
            raise ValueError('Invalid human approval') from error
        approved = True
    kind = data.get('kind')
    if kind not in ('constellation', 'unlikely-routes') or type(data.get('contentVersion')) is not int or data['contentVersion'] < 1:
        raise ValueError('Invalid experience/version')
    if structure(data.get('enUS'), kind) != structure(data.get('ptBR'), kind):
        raise ValueError('Locale structure/IDs must match in order')
    cues = data.get('audio')
    if not isinstance(cues, list) or len(cues) != 1 or cues[0].get('cueId') != 'intro':
        raise ValueError('This editorial package requires one public intro cue')
    audio, asset_ids, total_bytes = [], set(), 0
    for cue in cues:
        output = {'cueId': cue['cueId']}
        for locale in ('enUS', 'ptBR'):
            variant = cue.get(locale)
            if not isinstance(variant, dict):
                raise ValueError('Both audio locales required')
            asset_id = slug(variant.get('assetId'))
            if asset_id in asset_ids:
                raise ValueError('Duplicate audio asset')
            asset_ids.add(asset_id)
            if text(variant.get('transcript'), 120) != data[locale]['objective'] or variant.get('accessibleDescription') != data[locale]['title']:
                raise ValueError('Shared intro must contain only public objective/title')
            text(variant.get('provenance'), 1000)
            raw = audio_bytes(root, variant)
            total_bytes += len(raw)
            output[locale] = {key: variant[key] for key in ('assetId', 'transcript', 'accessibleDescription', 'provenance', 'sha256')}
            output[locale]['bytes'] = base64.b64encode(raw).decode('ascii')
        audio.append(output)
    if total_bytes > 16 * 1024 * 1024:
        raise ValueError('Combined audio budget exceeded')
    result = {'contentVersion': data['contentVersion'], 'enUS': data['enUS'], 'ptBR': data['ptBR'], 'audio': audio, 'editorialApproved': approved}
    if len(json.dumps(result, ensure_ascii=False).encode()) > 24 * 1024 * 1024:
        raise ValueError('Publication body budget exceeded')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('catalog', type=Path)
    parser.add_argument('--approval', type=Path, help='Explicit human approval JSON bound to the printed digest')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--digest', action='store_true')
    args = parser.parse_args()
    data = json.loads(args.catalog.read_text())
    approval = json.loads(args.approval.read_text()) if args.approval else None
    payload = package_catalog(data, args.catalog.parent, approval)
    if args.digest:
        print(approval_digest(data))
    if args.output:
        args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2)+'\n')
    elif not args.digest:
        print(json.dumps(payload, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
