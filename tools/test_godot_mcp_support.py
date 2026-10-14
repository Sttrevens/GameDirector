"""The stdio harness must surface startup diagnostics and bound each request."""
import json, sys, tempfile, time, unittest
from unittest.mock import patch
from pathlib import Path
from godot_mcp_support import Mcp

class StdioTests(unittest.TestCase):
    def test_initialize_eof_quotes_child_diagnostic_and_cleans_up(self):
        with tempfile.TemporaryDirectory() as work:
            log = Path(work) / 'mcp.log'
            with self.assertRaisesRegex(RuntimeError, 'distribution missing'):
                Mcp([sys.executable, '-c', 'import sys; sys.stderr.write("distribution missing\\n")'], 'http://127.0.0.1:1', log)
            self.assertEqual('distribution missing\n', log.read_text())

    def test_request_timeout_is_a_single_deadline_and_cleans_up(self):
        with tempfile.TemporaryDirectory() as work:
            start = time.monotonic()
            with self.assertRaises(TimeoutError):
                Mcp([sys.executable, '-c', 'import time; time.sleep(10)'], 'http://127.0.0.1:1', Path(work)/'mcp.log', timeout=0.1)
            self.assertLess(time.monotonic() - start, 2)

    def test_normal_close_waits_for_delayed_clean_exit(self):
        server = '''import sys, json, time
for line in sys.stdin:
    request = json.loads(line)
    if 'id' in request:
        print(json.dumps(dict(jsonrpc='2.0', id=request['id'], result={})), flush=True)
time.sleep(1.5)
'''
        with tempfile.TemporaryDirectory() as work:
            with Mcp([sys.executable, '-c', server], 'http://127.0.0.1:1', Path(work)/'mcp.log') as client:
                self.assertEqual({}, client.request('tools/list', {}))
            self.assertEqual(0, client.p.returncode)
            self.assertGreaterEqual(client.timeout, 60)

    def test_cold_call_deadline_can_exceed_thirty_seconds(self):
        server = '''import sys, json
for line in sys.stdin:
    request = json.loads(line)
    if 'id' in request:
        print(json.dumps(dict(jsonrpc='2.0', id=request['id'], result={})), flush=True)
'''
        with tempfile.TemporaryDirectory() as work:
            with Mcp([sys.executable, '-c', server], 'http://127.0.0.1:1', Path(work)/'mcp.log', timeout=75) as client:
                self.assertEqual({}, client.request('tools/list', {}))

    def test_host_rid_matches_machine_instead_of_the_developer_host(self):
        from godot_test_support import host_rid
        for system, machine, expected in [('Darwin','arm64','osx-arm64'), ('Darwin','x86_64','osx-x64'),
                                         ('Windows','AMD64','win-x64'), ('Linux','x86_64','linux-x64')]:
            with self.subTest(system=system, machine=machine), patch('platform.system', return_value=system), patch('platform.machine', return_value=machine):
                self.assertEqual(expected, host_rid())

if __name__ == '__main__': unittest.main()
