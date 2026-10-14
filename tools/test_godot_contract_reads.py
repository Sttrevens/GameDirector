"""Equivalent GDScript contract-read guard; invoked by test_engine_contract_reads.py."""
import bisect, re, unittest
from pathlib import Path
from test_engine_contract_reads import lex, numeric_value, DENY_NUMBERS, CONTRACT_NUMBERS, CONTRACT_STRINGS

REPO=Path(__file__).resolve().parents[1]
FILES=sorted((REPO/'packages/gamedirector-godot/addons/gamedirector').glob('*.gd'))
# Payload field names select the schema, not a role-kind/state declaration.
HARD_STRINGS=(CONTRACT_STRINGS-{'subject','with','lookAt','error'}) | {'actor.spawn','actor.despawn','actor.anim','actor.move','actor.face','camera.shot','world.timescale','audio.play','audio.stop'}

def comments(text):
    out=[]; i=0
    while i<len(text):
        if text[i] in ('"',"'"):
            quote=text[i]; triple=text.startswith(quote*3,i); end=quote*3 if triple else quote
            start=i; i+=len(end)
            while i<len(text):
                if text[i]=='\\':i+=2;continue
                if text.startswith(end,i):i+=len(end);break
                i+=1
            out.append(text[start:i])
        elif text[i]=='#':
            end=text.find('\n',i); end=len(text) if end<0 else end
            out.append('//'+text[i+1:end]); i=end
        else:out.append(text[i]);i+=1
    return ''.join(out)

def findings(text):
    text=comments(text); starts=[0]+[m.end() for m in re.finditer('\n',text)]; lines=text.splitlines()
    bad=set(); markers=set(); soft=set()
    for kind,value,start in lex(text):
        line=bisect.bisect_right(starts,start)
        if kind=='comment':
            if 'engine-local:' in value:markers.add(line)
            continue
        if kind=='number':
            number=numeric_value(value)
            if number in CONTRACT_NUMBERS or number==39779:bad.add((line,'owned-protocol-number'))
            elif number in DENY_NUMBERS:soft.add((line,'retired-number'))
        if kind=='string' and (value in HARD_STRINGS or re.search(r'(?<![\w.-])(?:127\.0\.0\.1|localhost|39777|39778|39779)(?![\w.-])',value)):
            bad.add((line,'owned-contract-string'))
    # Shot defaults must come from row.default even when that default is mathematical zero.
    for i,line in enumerate(lines,1):
        if re.search(r'\bparams\.get\([^,]+,\s*[-+]?\d',line):bad.add((i,'shot-literal-default'))
    for line,kind in soft:
        if not re.search(r'//\s*engine-local:\s*\S',lines[line-1]):bad.add((line,kind))
    active={line for line,_ in soft}
    for line in markers-active:bad.add((line,'unused-engine-local'))
    return sorted(bad)

def violations():
    result=[]
    for file in FILES:
        text=file.read_text(); lines=text.splitlines()
        for line,kind in findings(text):result.append(f'{file.relative_to(REPO)}:{line}: {kind}: {lines[line-1].strip()}')
    return result

class GodotRuleTests(unittest.TestCase):
    def test_contract_literals_cannot_be_exempted(self):
        for source in ('camera.fov=50','epsilon=0.00001','fps=24','params.get("pan",0)','port=39779','state="Completed"','shot="dolly"','cue="actor.anim"'):
            self.assertTrue(findings(source),source)
        for source in ('port=39779','state="Completed"','shot="dolly"','params.get("pan",0)'):
            self.assertTrue(findings(source+' # engine-local: fake exception'),source)
    def test_native_math_comment_and_graph_reads(self):
        self.assertFalse(findings('deadline=graph.bridge.budgets.stageReadySeconds*1000 # engine-local: seconds to milliseconds'))
        self.assertFalse(findings('epsilon=graph.frameClock.cueEpsilonSeconds\nfov=graph.camera.lens.defaultFov\npan=defaults["pan"]'))
        self.assertFalse(findings('# camera.fov=50\nvar x="text # marker"'))
        self.assertTrue(findings('var x=0 # engine-local: unused'))

if __name__=='__main__':
    import sys
    errors=violations(); print('\n'.join(errors) if errors else f'PASS: Godot contract reads ({len(FILES)} files)');sys.exit(bool(errors))
