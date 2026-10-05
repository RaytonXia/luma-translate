"""Build shared Windows/macOS resources from ECDICT CSV plus original Luma notes.

Usage: python source/build_dictionary.py PATH_TO_ECDICT_CSV --source-commit SHA
The upstream CSV is MIT licensed: https://github.com/skywind3000/ECDICT.
"""
import argparse, base64, csv, gzip, hashlib, json, re, unicodedata
from pathlib import Path

parser=argparse.ArgumentParser()
parser.add_argument('csv_file',type=Path)
parser.add_argument('--source-commit',required=True)
args=parser.parse_args()
root=Path(__file__).resolve().parent.parent
def normalize(value):
    value=unicodedata.normalize('NFKC',value).lower().replace('‘',"'").replace('’',"'").replace('–','-').replace('—','-')
    return re.sub(r'\s+',' ',value).strip(' .,;:!?"\'()[]{}\n\r\t')
def unescape(value):
    return re.sub(r'\\([\\nr])',lambda m:{'\\':'\\','n':'\n','r':'\r'}[m[1]],value)
rows={}
with args.csv_file.open(encoding='utf-8',newline='') as file:
    for row in csv.DictReader(file):
        if not row['translation'].strip() or not re.search('[A-Za-z]',row['word']): continue
        key=normalize(row['word'])
        if not key: continue
        phonetic=unescape(row['phonetic']).replace('ә','ə')
        rows[key]=[key, ('词库 /'+phonetic+'/') if phonetic else '', unescape(row['definition']),unescape(row['translation']),unescape(row['exchange']),'','','','ECDICT']
supplement=json.loads((root/'source/data/academic-supplement.json').read_text(encoding='utf-8'))
for item in supplement:
    key=normalize(item['word'])
    old=rows.get(key,[key,'','','','','','','',''])
    append=item['mode']=='append'
    translation=(old[3]+'\n\n[学术义项补充]\n' if append and old[3] else '')+item['translation']
    definition=(old[2]+'\n\n[Academic usage]\n' if append and old[2] else '')+item['definition']
    rows[key]=[key,item['phonetic'],definition,translation,old[4],item['example_en'],item['example_zh'],item['academic_notes'],'ECDICT + Luma 学术/语块补充（例句为原创示例）']
raw=('#SGFT-ECDICT-1\tECDICT-expanded-2026-10-05+academic-1\n'+'\n'.join('\t'.join(base64.b64encode(v.encode()).decode() for v in row) for key,row in sorted(rows.items()))+'\n').encode()
resources=root/'macos/Sources/LumaTranslate/Resources'
(root/'source/data/offline_ecdict_core.tsv.gz').write_bytes(gzip.compress(raw,compresslevel=9,mtime=0))
(resources/'offline_ecdict_core.tsv').write_bytes(raw)
# Keep the archive copy consistent for users who relied on the earlier backup layout.
(resources/'offline_ecdict_core.tsv.gz').write_bytes(gzip.compress(raw,compresslevel=9,mtime=0))
manifest={
 'version':'ECDICT-expanded-2026-10-05+academic-1','entries':len(rows),
 'phrases':sum(' ' in k for k in rows),
 'english_definitions':sum(bool(r[2]) for r in rows.values()),
 'phonetics':sum(bool(r[1]) for r in rows.values()),
 'supplement_entries':len(supplement),'examples_are':'Original illustrative examples, not quotations or research findings',
 'source_url':'https://github.com/skywind3000/ECDICT','source_commit':args.source_commit,
 'source_csv_sha256':hashlib.sha256(args.csv_file.read_bytes()).hexdigest(),
 'resource_sha256':hashlib.sha256(raw).hexdigest(),'license':'MIT; see ECDICT_LICENSE.txt',
 'coverage':'All eligible Chinese entries in the pinned upstream CSV. Missing English definitions, phonetics and examples are explicitly labelled. Neither this data nor AI output guarantees every historical or discipline-specific sense.',
 'academic_reference_notes':[
  'https://doi.org/10.1080/00031305.2016.1154108',
  'https://www.itl.nist.gov/div898/handbook/eda/section3/eda352.htm',
  'https://www.itl.nist.gov/div898/handbook/prc/section2/prc222.htm',
  'https://www.cochrane.org/authors/handbooks-and-manuals/handbook/current/chapter-08'
 ]
}
for target in [resources/'dictionary-manifest.json',root/'source/data/dictionary-manifest.json']:
    target.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in manifest.items() if k in ['entries','phrases','english_definitions','phonetics','supplement_entries']},ensure_ascii=False))
