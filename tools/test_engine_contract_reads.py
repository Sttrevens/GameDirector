"""No retired contract literals in engine readers. Run --self-test for rule probes.
Only a trailing // engine-local: <reason> exempts ambiguous engine-local reads.
Contract-owned strings, ports and error statuses can NEVER be exempted.
Unused markers fail, including comment-only lines and file/block headers.
"""
import argparse, bisect, re, sys, unittest
from decimal import Decimal
from pathlib import Path

repo=Path(__file__).resolve().parents[1]
FILES=[*(f'packages/gamedirector-three/{f}' for f in ('runtime.js','server.mjs','camera-kernels.mjs','contract.mjs','bundle.mjs','envelope.mjs','helpers.mjs','role-bounds.mjs','measure.mjs','gl-backends.mjs')),
       *(f'packages/com.gamedirector.unity/Runtime/{f}.cs' for f in ('CinematicCameraRig','DirectorRuntime','DirectorBridgeServer','UnityEngineContract'))]
# Retired scalar tokens from EngineContract; integers 0/1 and immutable
# formula ids are mathematics/control, not configurable defaults. An unrelated
# use of an ambiguous scalar must explain itself at the site, not grow an allowlist.
DENY_NUMBERS={Decimal(n) for n in ('0.03','1000','0.1','0.01','0.00001','0.25','0.2','0.4','7.3','0.5','0.05','50','24','5','170','90','8','32','4','15','30','60','100','10000','15000','30000','100000','1024','1048576')}
# Protocol ownership is unambiguous: no marker, even on a mixed hard/soft line,
# can bless a retired host, port, state, error, audio declaration or shot id.
CONTRACT_NUMBERS={Decimal(n) for n in ('39777','39778','400','409','503','504','500','403','404','405','415','413')}
CONTRACT_STRINGS={'127.0.0.1','localhost','39777','39778','current','Capturing','Completed','Failed','Cancelled','linear','in','out','inOut','offline-sandbox','post-production',
                  '/health','/manifest','/timeline/play','/timeline/stop','/take/start','/take/frame','/take/stop','/take','/status','/capture','/contract','/stage',
                  'invalid_request','state_conflict','busy','timeout','internal','forbidden','not_found','method_not_allowed','unsupported_media_type','payload_too_large',
                  'silent-picture; mix audio in the edit','lockoff','dolly','crane','orbit','tracking',
                  'subject','with','lookAt','in-front','behind','absent','empty','straddles-near','error'}
# Host/port tokens can sit inside URLs or template text, but not larger names,
# addresses or numbers (localhost.example and 127.0.0.10 are not loopback aliases).
EMBEDDED_HOST=re.compile(r'(?<![\w.-])(?:127\.0\.0\.1|localhost|39777|39778)(?![\w.-])',re.IGNORECASE)
TOKEN=re.compile(r'''(?P<number>(?<![\w.])(?:0[xX][\da-fA-F_]+[uUlL]{0,2}|(?:\d[\d_]*(?:\.[\d_]*)?|\.[\d_]+)(?:[eE][+-]?[\d_]+)?(?:[fFdDmM]|[uUlL]{1,2})?)(?![\w.]))|(?P<id>[A-Za-z_$][\w$]*)|(?P<op>\?\?=?|\|\|=?|&&=?|\?\.|.)''',re.MULTILINE)

def string_text(raw,verbatim=False):
 if verbatim:return raw.replace('""','"')
 # Decode JS/C# text escapes without executing an expression. Interpolation
 # delimiters have already been separated, so their code cannot hide in text.
 def escape(m):
  value=m.group()[1:]
  if value.startswith('u{'):return chr(int(value[2:-1],16))
  if value.startswith(('u','x')) and len(value)>1:return chr(int(value[1:],16))
  return {'n':'\n','r':'\r','t':'\t','b':'\b','f':'\f','v':'\v','0':'\0','\n':''}.get(value,value)
 return re.sub(r'\\(?:u\{[\da-fA-F]+\}|u[\da-fA-F]{4}|x[\da-fA-F]{2}|[\s\S])',escape,raw)

def lex(text):
 """Lex code, comments and string text; recursively enter JS/C# holes.

 This is a literal-read guard, not a JS/C# compiler. Braces in comments/strings
 and doubled verbatim/interpolation braces never end a code hole.
 """
 out=[];n=len(text)
 def emit(kind,value,start):out.append((kind,value,start))
 def quoted(start,quote_at,interpolation=False,verbatim=False):
  quote=text[quote_at];i=quote_at+1;chunk=i
  # Even an empty quoted/template literal is a literal fallback.
  emit('string','',start)
  while i<n:
   if not verbatim and text[i]=='\\':i+=2;continue
   if verbatim and text.startswith('""',i):i+=2;continue
   if interpolation=='cs' and (text.startswith('{{',i) or text.startswith('}}',i)):i+=2;continue
   hole=interpolation=='js' and text.startswith('${',i) or interpolation=='cs' and text[i]=='{'
   if hole:
    emit('string',string_text(text[chunk:i],verbatim),chunk)
    width=2 if interpolation=='js' else 1;emit('op',text[i:i+width],i)
    i=code(i+width,True);chunk=i;continue
   if text[i]==quote:
    emit('string',string_text(text[chunk:i],verbatim),chunk);emit('op',quote,i);return i+1
   i+=1
  emit('string',string_text(text[chunk:i],verbatim),chunk);return i
 def code(i,hole=False):
  depth=0
  while i<n:
   if hole and text[i]=='}' and depth==0:emit('op','}',i);return i+1
   if text.startswith('//',i):
    end=text.find('\n',i);end=n if end<0 else end;emit('comment',text[i:end],i);i=end;continue
   if text.startswith('/*',i):
    end=text.find('*/',i+2);end=n if end<0 else end+2;emit('comment',text[i:end],i);i=end;continue
   if text.startswith(('$@"','@$"'),i):i=quoted(i,i+2,'cs',True);continue
   if text.startswith('$"',i):i=quoted(i,i+1,'cs');continue
   if text.startswith('@"',i):i=quoted(i,i+1,False,True);continue
   if text[i] in ('"',"'",'`'):i=quoted(i,i,'js' if text[i]=='`' else False);continue
   m=TOKEN.match(text,i)
   if m:
    value=m.group()
    if not value.isspace():emit(m.lastgroup,value,i)
    if value=='{':depth+=1
    elif value=='}':depth-=1
    i=m.end()
   else:i+=1
  return i
 code(0);return out

def numeric_value(value):
 value=value.replace('_','')
 if value.lower().startswith('0x'):return Decimal(int(re.sub('[uUlL]+$','',value),16))
 return Decimal(re.sub(r'(?:[fFdDmM]|[uUlL]{1,2})$','',value))

def fallback_literal(tokens,j):
 # Skip unary/grouping syntax and C# casts, including qualified/nullable types.
 while j<len(tokens):
  if tokens[j][1] in ('+','-'):j+=1
  elif tokens[j][1]=='(':
   k=j+1
   while k<len(tokens) and (tokens[k][0]=='id' and tokens[k][1] not in ('false','true','null') or tokens[k][1] in ('.','?','[',']','<','>',',')):k+=1
   j=k+1 if k>j+1 and k<len(tokens) and tokens[k][1]==')' else j+1
  else:break
 return j<len(tokens) and (tokens[j][0] in ('number','string') or tokens[j][1] in ('false','true','null'))

def error_key_read(tokens,i,path):
 # These names overlap exported codes, but here select a graph row, not a wire
 # value. Recognize the actual server helper / Uses.errors array, never markers.
 if Path(path).name=='server.mjs':return i>=3 and tokens[i-3][1]=='fail' and tokens[i-2][1]=='(' and tokens[i-1][0]=='string'
 if Path(path).name=='contract.mjs':
  j=i-1
  while j>=0 and (tokens[j][0]=='string' or tokens[j][1] in ('"',"'",',')):j-=1
  return j>=2 and tokens[j][1]=='[' and tokens[j-1][1]==':' and tokens[j-2][1]=='errors'
 return False

def console_level_read(tokens,i,path):
 # Playwright ConsoleMessage.type() is a third-party log level, not a role state.
 return Path(path).name=='server.mjs' and tokens[i][1]=='error' and i>=9 and [t[1] for t in tokens[i-9:i-1]]==['m','.','type','(',')','=','=', '=']

def findings(text,path='runtime.js'):
 lines=text.splitlines();starts=[0]+[m.end() for m in re.finditer('\n',text)];tokens=[];exempt=set();markers=set()
 for kind,value,start in lex(text):
  line=bisect.bisect_right(starts,start)
  if kind=='comment':
   for marker in re.finditer(r'\bengine-local:',value):markers.add(bisect.bisect_right(starts,start+marker.start()))
   if re.fullmatch(r'//[ \t]*engine-local:[ \t]*\S[^\n]*',value) and lines[line-1].rstrip().endswith(value.rstrip()):exempt.add(line)
  elif not value.isspace():tokens.append((kind,value,line))
 violations=set();owned=set()
 def flag(line,kind,hard=False):
  finding=(line,kind);violations.add(finding)
  if hard:owned.add(finding)
 for i,(kind,value,line) in enumerate(tokens):
  if kind=='number':
   number=numeric_value(value);hard=number in CONTRACT_NUMBERS
   if number in DENY_NUMBERS or hard:flag(line,'retired-number',hard)
   if Path(path).name=='server.mjs' and number==int(number) and 100<=number<=599:flag(line,'bare-status',hard)
  elif kind=='string':
   if value in CONTRACT_STRINGS or EMBEDDED_HOST.search(value):
    if not console_level_read(tokens,i,path) and (value not in ('busy','timeout','internal','forbidden') or not error_key_read(tokens,i,path)):flag(line,'retired-string',True)
   continue # Text resembling an operator/identifier is never executable code.
  if value in ('??','??=','||','||=','&&=') and fallback_literal(tokens,i+1):flag(line,'literal-fallback')
  if value=='fallback' and i+1<len(tokens) and tokens[i+1][1]=='=':flag(line,'fallback-parameter')
  if value=='%':
   j=i+1
   while j<len(tokens) and tokens[j][1]=='(':j+=1
   if j<len(tokens) and tokens[j][0]=='id':
    name=tokens[j][1];j+=1
    while j+1<len(tokens) and tokens[j][1] in ('.','?.') and tokens[j+1][0]=='id':name=tokens[j+1][1];j+=2
    # fps*interval is contract cadence; fps or fps*1 is the retired form.
    scaled=j+1<len(tokens) and tokens[j][1]=='*' and tokens[j+1][0]=='id'
    if name in ('fps','_fps','FrameRate','frameRate','frame_rate') and not scaled:flag(line,'modulo-cadence')
 # Check usage against unsuppressed findings, including comment-only lines.
 active={line for line,_ in violations}
 violations={v for v in violations if v in owned or v[0] not in exempt}
 violations.update((line,'unused-engine-local') for line in markers-active)
 return sorted(violations)

# These probes catch reintroduced defaults, modulo cadence and protocol scalars;
# expectations are literals, not derived from the scanner or live contract.
PROBES=[('s.params?.orbitDeg??90','literal-fallback'),('x || "current"','literal-fallback'),('x ?? (-.25f)','literal-fallback'),
        ('float P(string key,float fallback = 0)','fallback-parameter'),('fallback=0','fallback-parameter'),
        ('i % fps == 0','modulo-cadence'),('i%_take.FrameRate==0','modulo-cadence'),('i % (this.take.frameRate) === 0','modulo-cadence'),('i%(fps*1)==0','modulo-cadence'),
        ('cam.nearClipPlane=.03f;','retired-number'),('cam.far=1000;','retired-number'),('v=.25; v=.2f;','retired-number'),
        ('frequency=7.3f;','retired-number'),('epsilon=.00001;','retired-number'),('angle=90;','retired-number'),('port=39778;','retired-number'),('port="39777";','retired-string'),
        ('host="127.0.0.1"; alias=\'localhost\';','retired-string'),('state="Capturing"; state=\'Completed\';','retired-string'),
        ('res.writeHead(409);','bare-status')]
class RuleTests(unittest.TestCase):
 def test_contract_owned_values_cannot_be_exempted(self):
  for line,kind in [('private const int Port = 39777; // engine-local: legacy port','retired-number'),
                    ('port=39778; // engine-local: legacy port','retired-number'),
                    ('host="localhost"; // engine-local: legacy host','retired-string'),
                    ('state="Capturing"; // engine-local: synthetic state','retired-string'),
                    ('code="invalid_request"; // engine-local: synthetic error','retired-string'),
                    ('res.writeHead(409); // engine-local: synthetic status','bare-status'),
                    ('audio="silent-picture; mix audio in the edit"; // engine-local: old audio','retired-string'),
                    ('shot="orbit"; x*1000 // engine-local: ms per second','retired-string')]:
   with self.subTest(line=line):self.assertIn(kind,[k for _,k in findings(line,'server.mjs')])
  self.assertEqual([],findings('x*1000 // engine-local: ms per second'))
  for value in ('busy','timeout','internal','forbidden'):
   self.assertEqual([],findings('fail('+repr(value)+',message)','server.mjs'))
   self.assertEqual([],findings('const Uses={errors:['+repr(value)+']}','contract.mjs'))
   self.assertIn('retired-string',[k for _,k in findings('code='+repr(value)+' // engine-local: synthetic error','server.mjs')])
 def test_detection_and_line_exemption(self):
  for line,kind in PROBES:
   with self.subTest(line=line):
    self.assertIn(kind,[k for _,k in findings(line,'server.mjs')])
    marked=findings(line+' // engine-local: controlled test fixture','server.mjs')
    if kind=='retired-string' or line in ('x || "current"','port=39778;','res.writeHead(409);'):self.assertTrue(marked)
    else:self.assertEqual([],marked)
    self.assertTrue(findings(line+' // engine-local:','server.mjs'))
    self.assertTrue(findings(line+' // engine-local:   ','server.mjs'))
 def test_denylist_tokens_and_exemptions(self):
  for value in DENY_NUMBERS:
   for token in (str(value),str(value)+'f'):
    with self.subTest(token=token):
     self.assertIn('retired-number',[k for _,k in findings('v='+token)])
     self.assertEqual([],findings('v='+token+' // engine-local: mathematical fixture'))
  for value in CONTRACT_NUMBERS:
   for token in (str(value),str(value)+'f',hex(int(value))):
    with self.subTest(token=token):self.assertIn('retired-number',[k for _,k in findings('v='+token+' // engine-local: synthetic port/status')])
  for value in CONTRACT_STRINGS:
   for quote in ('\'','"'):
    with self.subTest(value=value,quote=quote):
     line='v='+quote+value+quote
     self.assertIn('retired-string',[k for _,k in findings(line)])
     self.assertIn('retired-string',[k for _,k in findings(line+' // engine-local: synthetic stamp')])
 def test_role_vocabulary_is_contract_owned_not_a_console_level(self):
  for value in ('subject','with','lookAt','in-front','behind','absent','empty','straddles-near','error'):
   with self.subTest(value=value):self.assertIn('retired-string',[k for _,k in findings('state='+repr(value)+' // engine-local: synthetic role')])
  self.assertEqual([],findings("page.on('console',m=>{if(m.type()==='warning'||m.type()==='error')log(m.text());});",'server.mjs'))
  self.assertIn('retired-string',[k for _,k in findings("role.state==='error'",'server.mjs')])
 def test_comments_strings_boundaries_and_graph_reads(self):
  for line in ('// orbit ??90, .03f','/* .03f\ni%fps */\nvalue=contract.camera.rig.nearClip;',
               'msg="example ??90 / .03f / // engine-local: not a comment";',
               'value=10001; other=.0301; word=port39778;', 'x=param("orbitDeg");',
               'x??contract.take.requestDefaults.captureFromFrame;', 'i%(fps*interval)===0;',
               'i % (fps * contract.review.intervalSeconds) === 0;', 'value=contract.bridge.errors.busy.status;',
               'fallbackCoverage=camera.frames;', 'option=(name,fallback)=>fallback;'):
   with self.subTest(line=line):self.assertEqual([],findings(line))
  self.assertTrue(findings('x??90; msg="// engine-local: not a comment";'))
  self.assertTrue(findings('// engine-local: header does not exempt next line\nx??90;'))
  self.assertEqual([(2,'retired-number')],findings('// .03f\nv=.03f;'))
 def test_unused_markers(self):
  for line in ('value=contract.camera.rig.nearClip; // engine-local: stale exemption',
               '// engine-local: file header', '/* engine-local: block header */',
               '// ordinary comment // engine-local: stale comment-only marker'):
   with self.subTest(line=line):self.assertIn('unused-engine-local',[k for _,k in findings(line)])
  self.assertEqual([],findings('x??0; // engine-local: diagnostic sentinel'))
  self.assertIn((2,'literal-fallback'),findings('x??0; // engine-local: diagnostic sentinel\ny??0;'))
 def test_embedded_hosts_and_ports(self):
  for line in ('_listener.Prefixes.Add("http://127.0.0.1:" + port + "/");',
               'prefix="http://localhost:";', "new URL(req.url,'http://127.0.0.1')",
               '`127.0.0.1:${port}`', '`http://127.0.0.1:${server.address().port}`',
               'url="http://example:39777/";', '`http://${host}:39778/`'):
   with self.subTest(line=line):self.assertIn('retired-string',[k for _,k in findings(line)])
  for line in ('s="127.0.0.10";', 's="localhost.example";', 's="mylocalhost";',
               's="139777 397780 port39778 192.127.0.0.1";'):
   with self.subTest(line=line):self.assertEqual([],findings(line))
 def test_missing_retired_values(self):
  for value in ('10000','15000','30000','100000'):
   with self.subTest(value=value):self.assertIn('retired-number',[k for _,k in findings('timeout='+value)])
  for value in ('silent-picture; mix audio in the edit','lockoff','dolly','crane','orbit','tracking'):
   with self.subTest(value=value):self.assertIn('retired-string',[k for _,k in findings('v='+repr(value))])
  for op in ('??','||'):
   for value in ('false','true','null'):
    with self.subTest(op=op,value=value):self.assertIn('literal-fallback',[k for _,k in findings('v'+op+value)])
  self.assertEqual([],findings("motion='orbit-up'; v??falseValue; v||nullObject;"))
 def test_number_operator_cast_and_chain_forms(self):
  for value in ('1000L','1000U','1000UL','1000LU','1000ul','1_048_576','30_000',
                '1_000.0F','0x3E8','0x3E8UL','0x0_3e8u'):
   with self.subTest(value=value):self.assertIn('retired-number',[k for _,k in findings('v='+value)])
  for line in ('p.offsetX ||= 0','p.offsetX &&= 0','v??(float)0','v??((float)-.25f)',
               'v??(System.Single)0', 'v??(bool)false'):
   with self.subTest(line=line):self.assertIn('literal-fallback',[k for _,k in findings(line)])
  self.assertIn('modulo-cadence',[k for _,k in findings('i % this.take?.frameRate')])
  self.assertEqual([],findings('i % (this.take?.frameRate * contract.review.intervalSeconds)'))
 def test_recursive_interpolation_holes(self):
  for line in ('`${x??0}`', '`${`${x??0}`}`', '`${({a:x??0}).a}`',
               '`${/* } */ x??0}`', '$"{x??0}"', '$"{Format(\"ok\", x??0)}"',
               '$@"{x??0}"', '@$"{x??0}"', '$"{new { x = x??0 }}"'):
   with self.subTest(line=line):self.assertIn('literal-fallback',[k for _,k in findings(line)])
  self.assertEqual([(2,'literal-fallback')],findings('`first line\n${x??0}`'))
  for line in ('`example ??90 / .03f / ${contract.camera.rig.nearClip}`',
               r'`escaped \${x??0}`', '$"escaped {{x??0}}"', '$"text ??90 / .03f"'):
   with self.subTest(line=line):self.assertEqual([],findings(line))
 def test_status_scope(self):
  self.assertEqual([],findings('v=200;','CinematicCameraRig.cs'))
  self.assertIn('retired-number',[k for _,k in findings('v=409; // engine-local: legacy status','DirectorBridgeServer.cs')])
  for status in (200,204,400,403,404,405,409,413,415,500,503,504):
   self.assertIn('bare-status',[k for _,k in findings('res.writeHead('+str(status)+');','server.mjs')])

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--self-test',action='store_true');a=p.parse_args()
 if a.self_test:
  from test_godot_contract_reads import GodotRuleTests
  suite=unittest.TestSuite([unittest.defaultTestLoader.loadTestsFromTestCase(RuleTests), unittest.defaultTestLoader.loadTestsFromTestCase(GodotRuleTests)])
  return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1
 violations=[]
 for file in FILES:
  text=(repo/file).read_text();lines=text.splitlines()
  for line,kind in findings(text,file):violations.append(f'{file}:{line}: {kind}: {lines[line-1].strip()}')
 from test_godot_contract_reads import violations as godot_violations, FILES as godot_files
 violations.extend(godot_violations())
 print('\n'.join(violations) if violations else f'PASS: engine contract reads ({len(FILES)} existing + {len(godot_files)} Godot files)')
 return 1 if violations else 0
if __name__=='__main__':sys.exit(main())
