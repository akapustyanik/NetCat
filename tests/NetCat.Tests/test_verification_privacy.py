import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
import struct

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'Test-ShareablePrivacy.py'
spec = importlib.util.spec_from_file_location('privacy', SCRIPT)
privacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(privacy)

class VerificationPrivacyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.repo = self.root / 'repo'
        self.repo.mkdir()
        self.known = dict(zip(privacy.CATEGORIES, (['private.lab.invalid'], ['198.51.100.27'], ['10.250.7.0/24'])))
        self.fixture = self.root / 'scanner-input.json'
        self.fixture.write_text(json.dumps(self.known))
    def tearDown(self):
        self.temporary.cleanup()
    def scan(self, text, name='sample.txt'):
        path = self.repo / name
        path.write_text(text, encoding='utf-8')
        return privacy.scan([path], self.fixture, self.repo)
    def test_known_values_count_without_disclosure(self):
        result = self.scan(' '.join(v[0] for v in self.known.values()))
        self.assertEqual(3, result['LeakCount'])
        for value in self.known.values(): self.assertNotIn(value[0], json.dumps(result))
    def test_unrelated_private_network_is_not_blanket_flagged(self):
        self.assertEqual(0, self.scan('192.168.250.3 is in 192.168.250.0/24')['LeakCount'])
    def test_private_key_marker_detected(self):
        self.assertEqual(1, self.scan('-----BEGIN ' + 'PRIVATE KEY-----')['MatchesByCategory']['PrivateKey'])
    def test_subscription_detected(self):
        self.assertEqual(1, self.scan('trojan' + '://synthetic-password@synthetic.invalid:443')['MatchesByCategory']['Subscription'])
    def test_literal_credential_detected(self):
        self.assertEqual(1, self.scan('password=' + 'a'*16)['MatchesByCategory']['CredentialLiteral'])
    def test_encoded_credential_xml_forbidden_by_name(self):
        self.assertEqual(1, self.scan('encoded-synthetic-bytes', 'vm_credential.xml')['MatchesByCategory']['SensitiveFileName'])
    def test_fixture_inside_repo_rejected(self):
        inside=self.repo/'scanner-input.json';inside.write_text(self.fixture.read_text())
        with self.assertRaises(ValueError):privacy.scan([self.repo],inside,self.repo)
    def test_fixture_cannot_be_audited_as_artifact(self):
        with self.assertRaises(ValueError):privacy.scan([self.fixture],self.fixture,self.repo)
    def test_missing_file_rejected(self):
        with self.assertRaises(ValueError):privacy.scan([self.repo/'absent'],self.fixture,self.repo)
    def test_zip_content_scanned_without_extracting(self):
        path=self.repo/'synthetic.zip'
        with zipfile.ZipFile(path,'w') as archive:archive.writestr('inside.txt',self.known['KnownSiteADomains'][0])
        self.assertEqual(1,privacy.scan([path],self.fixture,self.repo)['LeakCount'])
    def test_utf16_windows_evidence_scanned(self):
        path=self.repo/'evidence.json';path.write_text(self.known['KnownSiteADomains'][0],encoding='utf-16')
        self.assertEqual(1,privacy.scan([path],self.fixture,self.repo)['LeakCount'])
    def test_nested_review_zip_scanned(self):
        inner=io.BytesIO()
        with zipfile.ZipFile(inner,'w') as archive:archive.writestr('nested.txt',self.known['KnownSiteADomains'][0])
        path=self.repo/'outer.zip'
        with zipfile.ZipFile(path,'w') as archive:archive.writestr('inner.zip',inner.getvalue())
        self.assertEqual(1,privacy.scan([path],self.fixture,self.repo)['LeakCount'])
    def test_incomplete_fixture_fails_closed(self):
        self.fixture.write_text('{}')
        with self.assertRaises(ValueError):privacy.scan([self.repo],self.fixture,self.repo)
    def test_zip_signatures_in_bytecode_are_scanned_as_bytes(self):
        data=self.known['KnownSiteADomains'][0].encode()+b'PK\x05\x06'+struct.pack('<4H2LH',0,0,1,1,12,0,0)
        self.assertTrue(zipfile.is_zipfile(io.BytesIO(data)))
        with self.assertRaises(zipfile.BadZipFile):zipfile.ZipFile(io.BytesIO(data))
        path=self.repo/'outer.zip'
        with zipfile.ZipFile(path,'w') as archive:archive.writestr('zipimport.pyc',data)
        self.assertEqual(1,privacy.scan([path],self.fixture,self.repo)['LeakCount'])
    def test_invalid_nested_zip_is_not_silently_treated_as_bytecode(self):
        data=b'not-a-directoryPK\x05\x06'+struct.pack('<4H2LH',0,0,1,1,12,0,0)
        path=self.repo/'outer.zip'
        with zipfile.ZipFile(path,'w') as archive:archive.writestr('payload.zip',data)
        with self.assertRaises(zipfile.BadZipFile):privacy.scan([path],self.fixture,self.repo)
    def test_wss_is_not_misclassified_as_shadowsocks(self):
        self.assertEqual(0,self.scan('wss'+'://gateway.invalid/socket')['MatchesByCategory']['Subscription'])
        self.assertEqual(0,self.scan('`ss'+ '://` is a scheme, not a profile')['MatchesByCategory']['Subscription'])
    def test_exact_synthetic_allowlist_never_hides_known_value(self):
        profile='vless'+ '://00000000-0000-4000-8000-000000000001@vpn.example.com:443?security=tls'
        clean=self.scan(profile)
        self.assertEqual(0,clean['LeakCount']);self.assertEqual(1,clean['ReviewedSyntheticProfileOccurrences'])
        self.known['KnownVpnEndpoints'].append('vpn.example.com');self.fixture.write_text(json.dumps(self.known))
        self.assertGreater(self.scan(profile)['LeakCount'],0)

if __name__ == '__main__': unittest.main()
