"""Offline fail-closed privacy scan. Secret fixture must live OUTSIDE the repository.

Fixture JSON categories (all required, nonempty arrays): KnownSiteADomains,
KnownVpnEndpoints, KnownCorporateTopology. Values never appear in output/errors.
No credential XML is accepted as a fixture. The scanner does not modify sources.
"""
import argparse
import hashlib
import io
import json
import pathlib
import re
import sys
import zipfile

CATEGORIES = ("KnownSiteADomains", "KnownVpnEndpoints", "KnownCorporateTopology")
EXCLUDED = {".git", "bin", "obj", "TestResults", "artifacts", ".vs", "__pycache__"}
PATTERNS = {
    "PrivateKey": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
    "Subscription": re.compile(r"(?<![\w-])(?:vless|vmess|trojan|ss)://[^\s`\"'<>]+|https?://[^\s`\"'<>]*(?:token|subscription|auth)[=/][A-Za-z0-9_-]{12,}", re.I),
    "CredentialLiteral": re.compile(r"(?im)^\s*(?:password|passwd|secret|token|api_key)\s*[:=]\s*[\"']?([A-Za-z0-9+/=_-]{12,})"),
    "Pkcs12Secret": re.compile(r"(?is)<pkcs12>\s*[A-Za-z0-9+/=\r\n]{32,}|(?:pkcs12|p12|pfx)[^\r\n]{0,40}(?:password|passphrase)\s*[:=]\s*[\"'][^\"'\r\n]{6,}[\"']"),
}
# Exact, reviewed source literals only: importer/configuration tests use reserved
# example domains and TEST-NET addresses with synthetic credentials. New or edited
# strings are NOT exempt. Known real values are checked before this allowlist and
# can never be waived by it. No entire file, subnet or domain is excluded.
REVIEWED_SYNTHETIC_PROFILES = {
    '4090427a862771ee510234b18eb670096a624c909389f5b892fa096c7e9716e1',
    '9b2f534376708931f925e12555aa836365a3a22271e869a8184f6bebbc6b6926',
    '4e13351dead938a8de48c18aebcf8b7655b763689bd0bc92316595aa1ca9bc8a',
    '542707dd33f94d0477802b1e9e89465c20c169ac24a4e7e8f3c8c8c94fa7e747',
    'c12055c5d77df8dd40db78046ce4de133d093ea206bc0fecd49c0b92a2b5c174',
    '02cc033c80c98a8f8ddba89e6271b12d98c2930bacb85b1cfbe3109d469a0e51',
    'cb9596940677ee70c8f34509c1f3ae292039baf0fbca44401b9a00c472f088d5',
    '272156d31bef2eb3e1392c9aba3b6e88ecf9edb74250cf50cea48dbb8c574808',
}

def scan_text(text, known):
    counts = {key: sum(len(re.findall(re.escape(value), text, re.I)) for value in values)
              for key, values in known.items()}
    counts.update({key: len(pattern.findall(text)) for key, pattern in PATTERNS.items()})
    reviewed=sum(hashlib.sha256(m.group(0).encode()).hexdigest() in REVIEWED_SYNTHETIC_PROFILES for m in PATTERNS['Subscription'].finditer(text))
    counts['Subscription']-=reviewed
    counts['ReviewedSyntheticProfiles']=reviewed
    return counts

def scan(paths, fixture, repo):
    fixture = fixture.resolve()
    if fixture.is_relative_to(repo.resolve()) or fixture.suffix.lower() != '.json' or 'credential' in fixture.name.lower():
        raise ValueError('Excluded JSON fixture outside repository required')
    known = json.loads(fixture.read_text(encoding='utf-8-sig'))
    if set(known) != set(CATEGORIES) or any(not isinstance(known[k], list) or not known[k] or any(not isinstance(v, str) or len(v) < 4 for v in known[k]) for k in CATEGORIES):
        raise ValueError('Complete known-value fixture required')
    totals = {k: 0 for k in (*CATEGORIES, *PATTERNS, 'SensitiveFileName')}
    files = 0
    synthetic = 0
    expanded_bytes = 0
    def content(data, name):
        nonlocal files, synthetic
        files += 1
        # Include UTF16 Windows logs; do not silently skip binary material.
        texts = [data.decode('utf-8-sig', errors='replace')]
        if data.startswith((b'\xff\xfe', b'\xfe\xff')) or b'\x00' in data[:256]:
            texts.append(data.decode('utf-16', errors='replace'))
        counts = [scan_text(t, known) for t in texts]
        synthetic += max(c['ReviewedSyntheticProfiles'] for c in counts)
        for key in (*CATEGORIES, *PATTERNS):
            totals[key] += max(c[key] for c in counts)
        if re.search(r'(?i)(?:credential.*\.xml|privacy-fixture\.json|\.ovpn$|\.p12$|\.pfx$|\.dpapi$|auth\.pass$|management\.pass$)', name):
            totals['SensitiveFileName'] += 1
    def archive_content(archive, depth=0):
        nonlocal expanded_bytes
        if depth > 4: raise ValueError('Archive nesting limit')
        for item in archive.infolist():
            if item.is_dir(): continue
            expanded_bytes += item.file_size
            if expanded_bytes > 1024**3: raise ValueError('Expanded archive limit')
            data = archive.read(item)
            candidate = io.BytesIO(data)
            if zipfile.is_zipfile(candidate):
                try:
                    nested = zipfile.ZipFile(candidate)
                except zipfile.BadZipFile:
                    # Python's zipimport bytecode contains EOCD constants which
                    # fool is_zipfile. Still scan all bytes; a named archive must
                    # remain fail-closed if its directory cannot be read.
                    if pathlib.PurePosixPath(item.filename).suffix.lower() in {'.zip', '.whl', '.pyz'}: raise
                    content(data, item.filename)
                else:
                    with nested: archive_content(nested, depth+1)
            else: content(data, item.filename)
    for path in paths:
        if not path.exists(): raise ValueError('Input does not exist')
        if path.is_symlink() or path.is_junction(): raise ValueError('Reparse input forbidden')
        path = path.resolve()
        if path == fixture: raise ValueError('Fixture cannot be audited/packaged as output')
        entries = [path] if path.is_file() else sorted(p for p in path.rglob('*') if p.is_file() and not any(part in EXCLUDED for part in p.relative_to(path).parts))
        for entry in entries:
            if entry.resolve() == fixture: raise ValueError('Fixture cannot be included in audit payload')
            if entry.is_symlink() or entry.is_junction(): raise ValueError('Symlink input forbidden')
            if entry.suffix.lower() == '.zip':
                with zipfile.ZipFile(entry) as archive:
                    archive_content(archive)
            else: content(entry.read_bytes(), entry.name)
    return {'AuditedFiles': files, 'MatchesByCategory': totals, 'LeakCount': sum(totals.values()), 'ReviewedSyntheticProfileOccurrences': synthetic, 'KnownValueAudit': 'Completed'}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', required=True, type=pathlib.Path)
    parser.add_argument('--repo', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--file-list', type=pathlib.Path)
    parser.add_argument('paths', nargs='*', type=pathlib.Path)
    args = parser.parse_args()
    try:
        paths = args.paths + ([pathlib.Path(x) for x in json.loads(args.file_list.read_text(encoding='utf-8-sig'))] if args.file_list else [])
        if not paths: raise ValueError('At least one input is required')
        result = scan(paths, args.fixture, args.repo)
        result['Status'] = 'ConfirmedPass' if result['LeakCount'] == 0 and result['AuditedFiles'] > 0 else 'ConfirmedFail'
    except Exception:
        # Exceptions may include a secret filename, JSON value or endpoint.
        result = {'AuditedFiles': 0, 'MatchesByCategory': {}, 'LeakCount': None, 'KnownValueAudit': 'Blocked', 'Status': 'Blocked'}
    args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
    return 0 if result['Status'] == 'ConfirmedPass' else 1

if __name__ == '__main__': sys.exit(main())
