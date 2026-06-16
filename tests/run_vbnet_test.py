import sys
sys.path.insert(0, 'C:/Users/abdullinvf/graphify-fork')
from pathlib import Path
from graphify.extract import extract_vbnet

result = extract_vbnet(Path('C:/Users/abdullinvf/graphify-fork/tests/test_sample.vb'))

print("NODES:")
for n in result['nodes']:
    print(f"  {n['source_location']:6}  {n['label']}")

print()
print("EDGES:")
for e in result['edges']:
    src = e['source'].split(':')[-1]
    tgt = e['target'].split(':')[-1]
    print(f"  {src}  --{e['relation']}--> {tgt}  [{e['confidence']}]")

print()
print(f"Итого: {len(result['nodes'])} узлов, {len(result['edges'])} рёбер")
