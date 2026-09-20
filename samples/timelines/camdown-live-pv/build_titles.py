"""Cinematic live audience graphics, generated from the same script as the sound.
No production HUD screenshots, fabricated provider receipts, or numeric rewards.
"""
import json,sys
from pathlib import Path
root=Path(__file__).parent;out=Path(sys.argv[1]);story=json.loads((root/'story.json').read_text());lines=json.loads((out/'assets/dialogue.json').read_text())
header='''[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Dialogue,Songti SC,44,&H00F5F5F2,&H00FFFFFF,&H00130E0A,&H90000000,0,0,0,0,100,100,1,0,1,2,1,2,100,100,114,1
Style: Graphic,Songti SC,32,&H00F5F5F2,&H00FFFFFF,&H00130E0A,&H90000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1
Style: Brand,Helvetica Neue,146,&H00F4F5F0,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,1,0,1,0,0,5,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
'''
rows=[]
def tc(x):
 c=round(x*100);return f'{c//360000}:{c//6000%60:02}:{c//100%60:02}.{c%100:02}'
def event(a,b,text,style='Graphic',layer=2):
 assert 0<=a<b<=story['duration'];rows.append(f'Dialogue: {layer},{tc(a)},{tc(b)},{style},,0,0,0,,{text}')
def draw(a,b,x,y,path,color='15100C',alpha='28',layer=0,fade='180,150'):
 event(a,b,rf'{{\an7\pos({x},{y})\p1\c&H{color}&\alpha&H{alpha}&\fad({fade})}}'+path,layer=layer)
# Restrained broadcast identity and red tally stay present through the story.
draw(.15,49,100,108,'m 0 0 l 116 0 116 42 0 42','324CEC','00',fade='200,150')
event(.15,49,r'{\an7\pos(124,111)\fs25\b1\fad(200,150)}LIVE')
event(.15,49,r'{\an7\pos(236,113)\fs24\fsp3\alpha&H22&\fad(200,150)}直播中')
# Actual playback seconds are useful broadcast continuity, not invented metrics.
for second in range(49):
 event(max(.15,second),second+1,rf'{{\an9\pos(1816,112)\fnHelvetica Neue\fs24\fsp2\alpha&H44&}}00:{second:02}')
# An audience is a small conversation with persistent identities. At most two
# cards are visible. Positions are assigned by overlapping intervals, not index.
slots=[-1.,-1.]
for item in story['audience']:
 start,end=item['t'],item['end'];slot=next(i for i,t in enumerate(slots) if t<=start);slots[slot]=end
 x,y=100,728+slot*112;width=560
 color={'warm':'D7DDA2','chaos':'7CB5FF','neutral':'E3D9CF'}[item['tone']]
 path=f'm 14 0 l {width-14} 0 b {width} 0 {width} 0 {width} 14 l {width} 84 b {width} 98 {width} 98 {width-14} 98 l 14 98 b 0 98 0 98 0 84 l 0 14 b 0 0 0 0 14 0'
 draw(start,end,x,y,path,alpha='40')
 draw(start,end,x,y+17,'m 0 0 l 3 0 3 64 0 64',color,'00',1)
 event(start,end,rf'{{\an7\move({x+16},{y+11},{x+24},{y+11},0,180)\fs20\c&H{color}&\fad(160,140)}}'+item['user'])
 event(start,end,rf'{{\an7\move({x+16},{y+42},{x+24},{y+42},0,180)\fs32\fad(180,140)}}'+item['text'])
# The outcome appears after the performed reaction, never on speech submission.
for r in story['semanticReactions']:
 x,y=(1350,245) if r['kind']=='positive' else (1370,280)
 color='8BE1FF' if r['kind']=='positive' else '8988FF'
 label='夸到心坎里' if r['kind']=='positive' else '戳到痛处了'
 event(r['t'],r['end'],rf'{{\an8\pos({x},{y})\fs24\c&H{color}&\fsp3\fad(100,180)}}'+label)
 event(r['t'],r['end'],rf'{{\an8\pos({x},{y+42})\fs66\b1\c&H{color}&\frz-7\fscx115\fscy115\t(0,160,\fscx100\fscy100)\fad(80,180)}}'+r['text'])
# Camera-viewfinder punctuation on the monster's pose; no scores or payout.
for x,y,sx,sy in [(115,200,1,1),(1805,200,-1,1),(115,685,1,-1),(1805,685,-1,-1)]:
 draw(13,17,x,y,f'm 0 {40*sy} l 0 0 {40*sx} 0 {40*sx} {2*sy} {2*sx} {2*sy} {2*sx} {40*sy}','E9ECE4','70',1)
# Dialogue is last and above every graphic. A quiet subtitle well stays free.
for l in lines:
 color='' if l['role']=='hero' else r'\c&H99DCFF&'
 event(max(0,l['start']-.08),l['end']+.15,'{'+color+r'\fad(70,100)}'+l['text'],'Dialogue',20)
# Clean title, with a little live red carried over from the opening.
draw(49,54,0,0,'m 0 0 l 1920 0 1920 1080 0 1080','140F09','00',10,fade='220,0')
event(49.15,54,r'{\pos(960,427)\fad(250,250)\fscx102\fscy102\t(0,4000,\fscx100\fscy100)}CAM DOWN!','Brand',12)
event(49.35,54,r'{\an5\pos(960,579)\fs48\fsp10\fad(300,250)}直播圣体',layer=12)
draw(49.25,54,886,665,'m 0 0 l 148 0 148 4 0 4','324CEC','00',12,fade='300,250')
event(49.6,54,r'{\an5\pos(960,738)\fs32\fsp3\c&HC4D6D7&\fad(350,250)}用嘴整活，全场接戏。',layer=12)
(out/'assets/titles.ass').write_text(header+'\n'.join(rows)+'\n')
print(f'{len(rows)} timed graphic/subtitle events; maximum two audience replies')
