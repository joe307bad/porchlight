#!/usr/bin/env bash
# Compile-checks the Unity scripts without opening Unity: builds copies of the
# csproj files Unity generated (open the project once first), with each
# assembly's sources globbed from its folder so new files are included.
# Usage: unity/Tools~/check.sh [out-dir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-${TMPDIR:-/tmp}/porchlight-check}"
mkdir -p "$OUT"
python3 - "$HERE" "$OUT" <<'PY'
import os, re, sys
root, out = sys.argv[1], sys.argv[2]
folders = {
    'Porchlight.Shared': 'Assets/Porchlight/Runtime/Shared',
    'Porchlight.Game': 'Assets/Porchlight/Runtime/Game',
    'Porchlight.Editor': 'Assets/Porchlight/Editor',
}
for name, folder in folders.items():
    src = os.path.join(root, name + '.csproj')
    if not os.path.exists(src):
        continue
    s = open(src, encoding='utf-8-sig').read()
    s = re.sub(r'\s*<Compile Include="[^"]*" />', '', s)
    s = s.replace('<ItemGroup>\n    <ProjectReference', '<ItemGroup>\n    <ProjectReference', 1)
    glob = os.path.join(root, folder, '**', '*.cs')
    s = s.replace('</Project>', f'  <ItemGroup>\n    <Compile Include="{glob}" />\n  </ItemGroup>\n</Project>')
    s = re.sub(r'(Include|HintPath)="(?!/)(Assets|Library|Packages)/', lambda m: f'{m.group(1)}="{root}/{m.group(2)}/', s)
    s = re.sub(r'<HintPath>(?!/)(Assets|Library|Packages)/', lambda m: f'<HintPath>{root}/{m.group(1)}/', s)
    s = s.replace('<BaseIntermediateOutputPath>Temp/', f'<BaseIntermediateOutputPath>{out}/')
    s = s.replace('<OutputPath>Temp/', f'<OutputPath>{out}/')
    open(os.path.join(out, name + '.csproj'), 'w').write(s)
PY
target="$OUT/Porchlight.Game.csproj"
[ -f "$OUT/Porchlight.Editor.csproj" ] && target="$OUT/Porchlight.Editor.csproj"
dotnet build "$target" -nologo -v q 2>&1 | grep -E "error|warning CS" | grep -v "warning CS0649\|warning CS0169\|warning CS0414" | sort -u || true
echo "checked $(basename "$target")"
