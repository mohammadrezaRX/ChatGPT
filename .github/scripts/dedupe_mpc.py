from pathlib import Path

path = Path('rebuild/MultiplayerCampaignSubModule.cs')
s = path.read_text(encoding='utf-8')
start_marker = '// ============================================================\n// CAMPAIGN MAP REMOTE PLAYER VISUAL SYSTEM - CORRECTED'
end_marker = '// ===== MPC REBUILD LAYER 2026 ====='
start = s.find(start_marker)
end = s.find(end_marker)
if start < 0 or end < 0 or end <= start:
    raise SystemExit(f'Markers not found: start={start}, end={end}')
clean = s[:start].rstrip() + '\n\n' + s[end:].lstrip()
path.write_text(clean, encoding='utf-8')
print(f'Deduplicated MPC source: {len(s)} -> {len(clean)} chars')
