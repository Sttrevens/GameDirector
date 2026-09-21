"""Real Workbench HTTP + local synthetic Jev, never a remote provider test."""
import copy
import json
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
root = repo / 'captures/decisions-http' / uuid.uuid4().hex[:12]
root.mkdir(parents=True)
source = root / 'source'
source.mkdir()
manifest = dict(game='DecisionFixture', gameVersion='1', manifestVersion='0.1',
    roles=[dict(id='hero', defaultActor='body', presentAtStart=True)],
    actors=[dict(id='body', clips=['wave'])], locations=[dict(id='front', position=[0, 1, 3])],
    audio=[], shotTypes=['lockoff'], frameTypes=['full', 'closeup'],
    capabilities={'director.mode': 'offline-sandbox', 'project.sourceRoot': str(source),
                  'presentation.sourceFingerprint': 'test-v1'})
calls = []
mode = 'selected'


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send(self, value):
        payload = json.dumps(value).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        self.send(manifest)

    def do_POST(self):
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            data = b''
            while True:
                count = int(self.rfile.readline().split(b';')[0].strip(), 16)
                if count == 0:
                    self.rfile.readline()
                    break
                data += self.rfile.read(count)
                self.rfile.read(2)
        else:
            data = self.rfile.read(int(self.headers['Content-Length']))
        body = json.loads(data)
        calls.append(body)
        answers = {}
        for key, question in body['questions'].items():
            if question['type'] == 'noul':
                answers[key] = dict(type='noul', noul=.8)
                continue
            if question['type'] == 'score':
                answers[key] = dict(type='score', score=1, confidence=.95, probabilities={'1': 1})
                continue
            choice = 'closer' if mode == 'selected' else 'unoffered'
            answers[key] = dict(type='choice', choice=choice, confidence=.95,
                                probabilities={choice: .95})
        self.send(dict(id='fixture-response', model='fixture-resolved-version',
                       provider='fixture', usage=dict(cost=.001), answers=answers))


fixture = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=fixture.serve_forever, daemon=True).start()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
base = ''


def call(path, body=None, expected=200):
    request = urllib.request.Request(base + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={'Content-Type': 'application/json'})
    try:
        response = opener.open(request, timeout=75)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.code == expected, (path, response.code, payload)
        return json.loads(payload) if payload else None


with (root / 'server.log').open('w', encoding='utf-8') as log:
    process = subprocess.Popen(['dotnet', str(repo / 'src/GameDirector.Workbench/bin/Debug/net8.0/gamedirector-workbench.dll'),
                                '--port', '0', '--data', str(root / 'store')], stdout=log, stderr=log)
    try:
        descriptor = root / 'store/.service.json'
        for _ in range(300):
            assert process.poll() is None, 'Workbench stopped; see server.log'
            if descriptor.exists():
                break
            time.sleep(.1)
        base = json.loads(descriptor.read_text(encoding='utf-8'))['address'] + '/api/'
        assert call('health')['product'] == 'GameDirector'
        assert not call('decisions/provider')['configured']
        endpoint = f'http://127.0.0.1:{fixture.server_port}'
        call('projects', dict(id='fixture', name='Fixture', engine='Unity', endpoint=endpoint))
        call('projects/fixture/connect', {})
        film = call('projects/fixture/starter')
        missing = call('projects/fixture/decisions/performance-match', dict(requestId='missing', intent='relief'))
        assert missing['outcome'] == 'no-match' and not calls
        call('decisions/provider', dict(endpoint=endpoint + '/decisions', model='fixture-model', apiKey='fixture-secret-never-save'))
        policy = call('decisions/policies')
        # Explicit threshold is a fixture control, not a recommended production policy.
        policy['cameraChoice']['minConfidence'] = .9
        call('decisions/policies', policy)
        shot = film['scenes'][0]['shots'][0]
        camera = copy.deepcopy(shot['camera'])
        camera['frame'] = 'closeup'
        request = dict(requestId='camera-one', shotId=shot['id'], intent='Show relief ' + str(source),
                       film=film, candidates=[dict(id='closer', camera=camera)])
        result = call('projects/fixture/decisions/camera-choice', request)
        assert result['outcome'] == 'selected', result
        assert result['selectedCandidateId'] == 'closer' and result['proposal']
        assert result['answers'][0]['resolvedModel'] == 'fixture-resolved-version'
        assert len(calls) == 1 and str(source) not in json.dumps(calls)
        assert call('projects/fixture/decisions/camera-choice', request) == result and len(calls) == 1
        changed = dict(request, intent='different')
        call('projects/fixture/decisions/camera-choice', changed, 409)
        mode = 'malformed'
        invalid = call('projects/fixture/decisions/camera-choice', dict(request, requestId='malformed'))
        assert invalid['outcome'] == 'needs-review' and invalid.get('proposal') is None
        assert call('projects/fixture/decisions/camera-choice/malformed') == invalid
        assert len(call('projects/fixture/decisions')) >= 3
        ranking_request = dict(requestId='ranking', brief='A clear portrait',
            candidates=[dict(id='first', film=film), dict(id='second', film=dict(film, title='Alternate'))])
        ranking = call('projects/fixture/decisions/film-ranking', ranking_request)
        assert ranking['outcome'] == 'needs-review' and len(ranking['ranking']) == 2, ranking
        changed_rationale = copy.deepcopy(ranking_request)
        changed_rationale['candidates'][0]['rationale'] = 'A newly supplied rationale changes the model input'
        call('projects/fixture/decisions/film-ranking', changed_rationale, 409)
        review = call('projects/fixture/decisions/semantic-review', dict(requestId='review', subject='The hero shows relief.',
            checks=[dict(id='relief', kind='noul', question='Does this text mention relief?'),
                    dict(id='clarity', kind='score', question='How clear is the text?', legend=['unclear', 'clear'])]))
        assert review['outcome'] == 'completed' and len(review['answers']) == 2, review
        call('projects/fixture/catalog/scaffold', {})
        catalog = call('projects/fixture/catalog/content')
        assert catalog['performances'] and catalog['manifestSha256']
        preserved = call('projects/fixture/catalog/scaffold', {})
        assert preserved['scaffolded'] is False
        assert call('projects/fixture/catalog/content') == catalog
        assert call('projects/fixture/starter') == film, 'Decisions must not change the film'
        for file in (root / 'store').rglob('*.json'):
            assert 'fixture-secret-never-save' not in file.read_text(encoding='utf-8')
        evidence = dict(passed=True, provider='local synthetic fixture', calls=len(calls),
                        checks=['HTTP routes and DI', 'empty evidence no-match', 'choice proposal',
                                'actual model audit', 'path scrubbing', 'idempotency and conflict',
                                'malformed answer preserves original', 'audit read/list', 'memory-only key',
                                'uncalibrated ranking stays review-only', 'semantic noul and score checks',
                                'downloadable catalog and non-destructive scaffolding'])
        (root / 'result.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
        print(json.dumps(dict(**evidence, evidence=str(root))))
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=20)
        fixture.shutdown()
        fixture.server_close()
