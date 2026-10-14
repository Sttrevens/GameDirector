"""Small dependency-free Markdown link/heading checker for GitHub-rendered docs."""
from html import unescape
from pathlib import Path
import re
import unicodedata
from urllib.parse import unquote, urlsplit

INLINE = re.compile(r'!?\[(?:[^\[\]\n]|\[[^\]\n]*\])*\]\(\s*(<[^>]*>|(?:[^()\s]+|\([^)]*\))+)(?:\s+[^)]*)?\)')
REFERENCE = re.compile(r'!?\[([^\]\n]+)\]\[([^\]\n]*)\]')
DEFINITION = re.compile(r'^\s{0,3}\[([^\]]+)\]:\s*(<[^>]+>|\S+)', re.M)
HTML_LINK = re.compile(r'\b(?:src|href)\s*=\s*["\']([^"\']+)["\']', re.I)


def visible(text):
    """Ignore fenced examples; do not mistake sample links for rendered links."""
    lines, fence = [], ''
    for line in text.splitlines():
        match = re.match(r'^\s{0,3}(`{3,}|~{3,})', line)
        if match and not fence:
            fence = match[1]
            lines.append('')
        elif fence:
            if match and match[1][0] == fence[0] and len(match[1]) >= len(fence):
                fence = ''
            lines.append('')
        else:
            lines.append(line)
    return '\n'.join(lines)


def anchors(text):
    text = visible(text)
    found = set(re.findall(r'\b(?:id|name)=["\']([^"\']+)["\']', text))
    lines = text.splitlines()
    used = set()
    for index, line in enumerate(lines):
        heading = re.match(r'^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$', line)
        title = heading[1] if heading else ''
        if not title and index + 1 < len(lines) and re.match(r'^\s{0,3}(?:=+|-+)\s*$', lines[index + 1]):
            title = line.strip()
        if not title:
            continue
        title = re.sub(r'!?\[([^\]]+)\]\([^)]*\)', r'\1', title)
        title = re.sub(r'<[^>]*>', '', unescape(title)).replace('`', '')
        slug = ''.join(c for c in title.lower() if c in '-_' or c.isspace()
                       or unicodedata.category(c)[0] in 'LNM')
        slug = re.sub(r'\s', '-', slug)
        candidate, suffix = slug, 0
        while candidate in used:
            suffix += 1
            candidate = f'{slug}-{suffix}'
        found.add(candidate)
        used.add(candidate)
    return found


def destinations(text):
    text = visible(text)
    # Inline code isn't a rendered link. Keep code labels inside real links.
    text = re.sub(r'(`+)(.*?)\1', lambda m: m[2] if '](' in text[m.end():m.end()+3] else '', text)
    refs = {m[1].strip().casefold(): m[2].strip('<>') for m in DEFINITION.finditer(text)}
    for match in INLINE.finditer(text):
        yield match[1].strip('<>')
    for match in HTML_LINK.finditer(text):
        yield unescape(match[1])
    for match in REFERENCE.finditer(text):
        yield refs.get((match[2] or match[1]).strip().casefold(), '!undefined-reference!')
    # Shortcut references render only when a definition exists.
    stripped = DEFINITION.sub('', REFERENCE.sub('', INLINE.sub('', text)))
    for match in re.finditer(r'!?\[([^\]\n]+)\]', stripped):
        key = match[1].strip().casefold()
        if key in refs:
            yield refs[key]


def check(root, files):
    errors, count, cache = [], 0, {}
    root = root.resolve()
    for source in files:
        source = source.resolve()
        for target in destinations(source.read_text(encoding='utf-8')):
            url = urlsplit(target)
            if url.scheme or url.netloc:
                continue
            count += 1
            path = (source.parent / unquote(url.path)).resolve() if url.path else source.resolve()
            problem = ''
            if not path.is_relative_to(root) or not path.exists():
                problem = 'missing relative target'
            elif url.fragment:
                if path.is_dir():
                    path = path / 'README.md'
                if not path.is_file():
                    problem = 'anchor target has no document'
                else:
                    if path not in cache:
                        cache[path] = anchors(path.read_text(encoding='utf-8'))
                    fragment = unquote(url.fragment)
                    if fragment not in cache[path]:
                        # GitHub source-view line links (not Markdown heading IDs).
                        line = re.fullmatch(r'L(\d+)(?:-L(\d+))?', fragment)
                        if not line or path.suffix.lower() == '.md':
                            problem = 'missing anchor'
                        elif max(int(n) for n in line.groups() if n) > len(path.read_text().splitlines()):
                            problem = 'source line out of range'
            if problem:
                errors.append(f'{source.relative_to(root)}: {target}: {problem}')
    return errors, count
