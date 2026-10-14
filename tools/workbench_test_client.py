"""Tiny shared bootstrap for harness-owned Workbench instances; never print tokens."""
import json
import time
import urllib.request


def auth_headers(service):
    return {'Authorization': 'Bearer ' + service['token']}


def wait_service(descriptor, process, prior=None, timeout=30):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        assert process.poll() is None, 'Workbench exited; inspect its log'
        if descriptor.is_file() and descriptor.read_bytes() != prior:
            service = json.loads(descriptor.read_text(encoding='utf-8'))
            assert len(service.get('token', '')) == 64, 'Workbench descriptor lacks an instance token; rebuild/package the Workbench'
            try:
                with opener.open(service['address'] + '/api/health', timeout=2) as response:
                    health = json.load(response)
                if health['product'] == 'GameDirector' and health['instanceId'] == service['instanceId']:
                    return service
            except OSError:
                pass
        time.sleep(.1)
    raise AssertionError('Workbench did not publish a live descriptor; inspect its log')
