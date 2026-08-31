from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "rebuild" / "MultiplayerCampaignSubModule.cs"
OUT = ROOT / "rebuild" / "split"

# The rebuild file accumulated experimental layers over time.  The first
# occurrence of any of these markers starts an appended layer, not the
# original core implementation.
CUTOFF_MARKERS = [
    "// ================= MPC FINAL SAFETY LAYER V2 =================",
    "namespace MultiplayerCampaignRebuildLayer",
    "// ============================================================\n// FINAL NETWORK MESSAGE ROUTER",
]

if not SRC.exists():
    raise SystemExit(f"Missing source: {SRC}")

text = SRC.read_text(encoding="utf-8")

positions = [(text.find(m), m) for m in CUTOFF_MARKERS if text.find(m) >= 0]
if positions:
    cutoff, marker = min(positions, key=lambda x: x[0])
    text = text[:cutoff].rstrip() + "\n"
    print(f"Cutoff marker: {marker!r} at offset {cutoff}")
else:
    print("No cleanup cutoff marker found; preserving full source")

# Preserve the common using block from the original source.
ns_match = re.search(r"\bnamespace\s+MultiplayerCampaign\s*\{", text)
if not ns_match:
    raise SystemExit("Could not find MultiplayerCampaign namespace")

header = text[:ns_match.start()].strip() + "\n"
body_start = ns_match.end()
body = text[body_start:]

# Remove the final namespace closing brace before parsing top-level types.
# The scanner below tracks braces safely enough for normal C# strings/comments.

def mask_csharp(s: str) -> str:
    out = list(s)
    i = 0
    n = len(s)
    state = "code"
    while i < n:
        c = s[i]
        nxt = s[i + 1] if i + 1 < n else ""
        if state == "code":
            if c == '/' and nxt == '/':
                out[i] = out[i + 1] = ' '
                i += 2
                state = "line"
                continue
            if c == '/' and nxt == '*':
                out[i] = out[i + 1] = ' '
                i += 2
                state = "block"
                continue
            if c == '@' and nxt == '"':
                out[i] = out[i + 1] = ' '
                i += 2
                state = "verbatim"
                continue
            if c == '"':
                out[i] = ' '
                i += 1
                state = "string"
                continue
            if c == "'":
                out[i] = ' '
                i += 1
                state = "char"
                continue
            i += 1
            continue
        if state == "line":
            if c == '\n':
                state = "code"
            else:
                out[i] = ' '
            i += 1
            continue
        if state == "block":
            if c == '*' and nxt == '/':
                out[i] = out[i + 1] = ' '
                i += 2
                state = "code"
            else:
                if c != '\n':
                    out[i] = ' '
                i += 1
            continue
        if state == "string":
            if c == '\\':
                out[i] = ' '
                if i + 1 < n:
                    out[i + 1] = ' '
                i += 2
            elif c == '"':
                out[i] = ' '
                i += 1
                state = "code"
            else:
                if c != '\n':
                    out[i] = ' '
                i += 1
            continue
        if state == "verbatim":
            if c == '"':
                if nxt == '"':
                    out[i] = out[i + 1] = ' '
                    i += 2
                else:
                    out[i] = ' '
                    i += 1
                    state = "code"
            else:
                if c != '\n':
                    out[i] = ' '
                i += 1
            continue
        if state == "char":
            if c == '\\':
                out[i] = ' '
                if i + 1 < n:
                    out[i + 1] = ' '
                i += 2
            elif c == "'":
                out[i] = ' '
                i += 1
                state = "code"
            else:
                if c != '\n':
                    out[i] = ' '
                i += 1
            continue
    return ''.join(out)

masked = mask_csharp(body)

# Find top-level type declarations at namespace depth 1.
type_re = re.compile(
    r"(?m)^[ \t]*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|unsafe)\s+)*"
    r"(?:class|struct|enum|interface|delegate)\s+([A-Za-z_][A-Za-z0-9_]*)"
)

# First compute brace depth for each character in masked source.
depth = [0] * len(masked)
d = 0
for i, ch in enumerate(masked):
    depth[i] = d
    if ch == '{':
        d += 1
    elif ch == '}':
        d -= 1

# Namespace body is depth 0 in `body` because the namespace opening brace was removed.
candidates = []
for m in type_re.finditer(masked):
    if depth[m.start()] != 0:
        continue
    name = m.group(1)
    # delegate has no class body; collect to terminating semicolon.
    kind = re.search(r"\b(class|struct|enum|interface|delegate)\b", m.group(0)).group(1)
    candidates.append((m.start(), m.end(), name, kind))

# Extract complete type spans, including directly attached attributes/comments.
items = []
for idx, (start, decl_end, name, kind) in enumerate(candidates):
    # Prefer the previous blank line as the file-local start so Harmony attributes
    # and section comments remain attached to the type.
    prefix_start = body.rfind("\n\n", 0, start)
    prefix_start = 0 if prefix_start < 0 else prefix_start + 2

    if kind == "delegate":
        semi = masked.find(';', decl_end)
        if semi < 0:
            raise SystemExit(f"Could not terminate delegate {name}")
        end = semi + 1
    else:
        open_brace = masked.find('{', decl_end)
        if open_brace < 0:
            raise SystemExit(f"Could not find body for {name}")
        target_depth = depth[open_brace]
        end = open_brace + 1
        while end < len(masked) and depth[end] > target_depth:
            end += 1
        if end >= len(masked):
            raise SystemExit(f"Unbalanced body for {name}")
        # include the closing brace
        while end < len(masked) and masked[end].isspace():
            end += 1
        if end <= len(masked) and masked[end - 1] == '}':
            pass

    raw = body[prefix_start:end].strip()
    items.append((name, raw))

if not items:
    raise SystemExit("No top-level types detected")

# Keep the first definition of each type name.  This removes exact duplicate
# definitions introduced by iterative rebuilds while retaining the first, original implementation.
unique = []
seen = set()
for name, raw in items:
    if name in seen:
        print(f"Dropping duplicate type: {name}")
        continue
    seen.add(name)
    unique.append((name, raw))

OUT.mkdir(parents=True, exist_ok=True)
for p in OUT.rglob("*.cs"):
    p.unlink()

# A small number of coherent modules rather than one file per tiny helper.
def bucket(name: str) -> str:
    if name in {"CampaignWorld", "CampaignMessageFeed", "MultiplayerCampaignSubModule", "MultiplayerCampaignBehavior"}:
        return "Core.cs"
    if name in {"InitialMenuPatch", "MultiplayerCampaignScreen", "MultiplayerCampaignVM"}:
        return "UI.cs"
    if "RemotePlayer" in name or "Player" in name or "Party" in name or "Character" in name:
        return "Players.cs"
    if "Session" in name or "State" in name or "GameState" in name:
        return "Session.cs"
    if any(k in name for k in ("Network", "Packet", "Protocol", "Connection", "World", "Host", "Transfer", "Sync")):
        return "Network.cs"
    return "Support.cs"

files = {}
for name, raw in unique:
    files.setdefault(bucket(name), []).append((name, raw))

for fname, entries in files.items():
    pieces = [header.rstrip(), "", "namespace MultiplayerCampaign", "{"]
    for _, raw in entries:
        # Remove accidental namespace-level final brace from the truncated source.
        pieces.append("    " + raw.replace("\n", "\n    "))
        pieces.append("")
    pieces.append("}")
    (OUT / fname).write_text("\n".join(pieces) + "\n", encoding="utf-8")

# Replace the giant source with a compatibility shim that contains no types.
# The real types now live in the split files, and SDK-style projects include them automatically.
SRC.write_text(
    "// Source split completed. Core implementations are under rebuild/split/*.cs.\n",
    encoding="utf-8",
)

print(f"Original types: {len(items)}")
print(f"Unique types: {len(unique)}")
print("Generated modules:")
for fname, entries in sorted(files.items()):
    print(f"  {fname}: {len(entries)} types")
