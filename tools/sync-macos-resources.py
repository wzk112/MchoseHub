#!/usr/bin/env python3
"""Keep the native K87 resource derived from the reviewed Core resource."""
import argparse
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser()
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
source = json.loads((root / 'src/MchoseHub.Core/Resources/keyboard-models.json').read_text())
functions = json.loads((root / 'src/MchoseHub.Core/Resources/keyboard-functions.json').read_text())
labels_source = (root / 'src/MchoseHub.Core/Protocol/KeyboardFunctions.cs').read_text()
labels = dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', labels_source))
for entry in functions['functions']:
    entry['label'] = labels.get(entry['name'], entry['name'])
resources = {'models.json': source, 'functions.json': functions,
             'factory.json': json.loads((root / 'src/MchoseHub.Core/Resources/keyboard-factory.json').read_text())}
for name, result in resources.items():
    contents = json.dumps(result, ensure_ascii=False, indent=2) + '\n'
    target = root / 'macos/Sources/MchoseCore/Resources' / name
    if args.check:
        if not target.exists() or target.read_text() != contents:
            raise SystemExit(f'{name} is stale; run python3 tools/sync-macos-resources.py')
    else:
        target.write_text(contents)
