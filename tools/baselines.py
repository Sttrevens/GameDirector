"""Compare a Sandring take with the durable, developer-only Sandring baseline."""
import argparse,json,sys
from pathlib import Path

def hashes(path):
 rows=[json.loads(line) for line in path.read_text().splitlines()]
 if not rows:raise ValueError(f'empty frames receipt: {path}')
 # Take receipts number captured frames from zero, including --from-frame takes.
 return [row['sha256'] for row in rows]

def main():
 p=argparse.ArgumentParser(description=__doc__);commands=p.add_subparsers(dest='command',required=True)
 check=commands.add_parser('check-sandring');check.add_argument('take',type=Path)
 check.add_argument('--from',dest='start',type=int,choices=[0,60],default=0);a=p.parse_args()
 baseline=Path.home()/'.gamedirector-dev/baselines/sandring'/('from60.jsonl' if a.start else 'frames.jsonl')
 try:
  expected=hashes(baseline);actual=hashes(a.take/'frames.jsonl')
  if len(actual)!=len(expected):raise ValueError(f'frame count mismatch: expected {len(expected)}, got {len(actual)}')
  for i,(want,got) in enumerate(zip(expected,actual)):
   if want!=got:raise ValueError(f'frame mismatch at row {i+1}: expected {want}, got {got}')
 except (OSError,ValueError,KeyError,TypeError) as e:p.exit(1,f'baseline check refused: {e}\n')
 print(f'{len(actual)} hashes equal {baseline}')

if __name__=='__main__':main()
