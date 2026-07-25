"""Apply the audited local compatibility patch to the pinned mem0 API image."""

from pathlib import Path


MAIN_PATH = Path("/app/main.py")
REPLACEMENTS = (
    (
        '    "graph_store": {\n'
        '        "provider": "neo4j",\n'
        '        "config": {"url": NEO4J_URI, "username": NEO4J_USERNAME, '
        '"password": NEO4J_PASSWORD},\n'
        "    },\n",
        "",
    ),
    (
        '"model": "gpt-4o"',
        '"model": os.environ.get("MEM0_DEFAULT_LLM_MODEL", "gpt-4o-mini")',
    ),
    (
        '"model": "text-embedding-3-small"',
        '"model": os.environ.get('
        '"MEM0_DEFAULT_EMBEDDER_MODEL", "text-embedding-3-small")',
    ),
)


source = MAIN_PATH.read_text(encoding="utf-8")
for original, replacement in REPLACEMENTS:
    occurrences = source.count(original)
    if occurrences != 1:
        raise RuntimeError(
            f"Refusing to patch {MAIN_PATH}: expected one occurrence, found "
            f"{occurrences}: {original.splitlines()[0]!r}"
        )
    source = source.replace(original, replacement)

required_invariants = (
    'os.environ.get("MEM0_DEFAULT_LLM_MODEL", "gpt-4o-mini")',
    'os.environ.get("MEM0_DEFAULT_EMBEDDER_MODEL", "text-embedding-3-small")',
)
if '"graph_store": {' in source:
    raise RuntimeError("Refusing patched image: default graph_store remains enabled")
for invariant in required_invariants:
    if source.count(invariant) != 1:
        raise RuntimeError(f"Refusing patched image: invariant missing: {invariant}")

MAIN_PATH.write_text(source, encoding="utf-8")
