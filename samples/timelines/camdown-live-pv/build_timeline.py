"""Rebuild measured mouth/expression timing without changing authored blocking."""
import json
from pathlib import Path
r=Path(__file__).parent
t=json.loads((r/'timeline-base.json').read_text())
for l in json.loads((r/'dialogue-timing.json').read_text()):
 t['cues'] += [dict(t=l['start'],type='actor.anim',role=l['role']+'_face',clip='Panic' if 30<=l['start']<41 else 'Talk',fade=.05),dict(t=l['end'],type='actor.anim',role=l['role']+'_face',clip='Worried' if 30<=l['start']<43 else 'Idle',fade=.1)]
t['cues'] += [dict(t=23.05,type='actor.anim',role='crew_face',clip='Worried',fade=.1),dict(t=26,type='actor.anim',role='hero_face',clip='Worried',fade=.1)]
t['cues'].sort(key=lambda c:c['t'])
(r/'timeline.json').write_text(json.dumps(t,ensure_ascii=False,indent=2)+'\n')
