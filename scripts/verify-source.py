#!/usr/bin/env python3
from __future__ import annotations

import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []
checks: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def ok(message: str) -> None:
    checks.append(message)


# JSON
for path in ROOT.rglob("*.json"):
    try:
        json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        fail(f"JSON invalid: {path.relative_to(ROOT)}: {exc}")
if not any("JSON invalid" in item for item in errors):
    ok("JSON files parse successfully")

# XML / Avalonia XAML / project files
xml_paths = list(ROOT.rglob("*.axaml")) + list(ROOT.rglob("*.csproj"))
for path in xml_paths:
    try:
        ET.parse(path)
    except Exception as exc:
        fail(f"XML invalid: {path.relative_to(ROOT)}: {exc}")
if not any("XML invalid" in item for item in errors):
    ok("XAML and project XML are well-formed")

# Required implementation files per phase
required = [
    "src/FaceHubManagement.Desktop/Views/MainWindow.axaml",
    "src/FaceHubManagement.Desktop/ViewModels/MainWindowViewModel.cs",
    "src/FaceHubManagement.Desktop/ViewModels/FirewallPortItemViewModel.cs",
    "src/FaceHubManagement.Desktop/Views/AddPortDialog.axaml",
    "src/FaceHubManagement.Desktop/Views/AddPortDialog.axaml.cs",
    "src/FaceHubManagement.Desktop/Views/ConfirmationDialog.axaml",
    "src/FaceHubManagement.Desktop/Views/ConfirmationDialog.axaml.cs",
    "src/FaceHubManagement.Desktop/Views/LogDetailsDialog.axaml",
    "src/FaceHubManagement.Desktop/Views/LogDetailsDialog.axaml.cs",
    "src/FaceHubManagement.Core/Models/FirewallStatusInfo.cs",
    "src/FaceHubManagement.Core/Models/FirewallPortInfo.cs",
    "src/FaceHubManagement.Agent/Firewall/IFirewallManager.cs",
    "src/FaceHubManagement.Agent/Firewall/FirewallPortStore.cs",
    "src/FaceHubManagement.Agent/Firewall/WindowsFirewallManager.cs",
    "src/FaceHubManagement.Agent/Firewall/LinuxUfwFirewallManager.cs",
    "src/FaceHubManagement.Desktop/ViewModels/PortStatusItemViewModel.cs",
    "src/FaceHubManagement.Core/Models/PortStatusInfo.cs",
    "src/FaceHubManagement.Agent/Resources/PortMonitor.cs",
    "src/FaceHubManagement.Agent/Resources/WindowsResourceMonitor.cs",
    "src/FaceHubManagement.Agent/Resources/LinuxResourceMonitor.cs",
    "src/FaceHubManagement.Agent/Resources/NetworkInfoCollector.cs",
    "src/FaceHubManagement.Agent/Resources/NvmlMonitor.cs",
    "src/FaceHubManagement.Core/Models/NetworkInterfaceInfo.cs",
    "src/FaceHubManagement.Agent/Services/WindowsServiceManager.cs",
    "src/FaceHubManagement.Agent/Services/LinuxSystemdServiceManager.cs",
    "src/FaceHubManagement.Agent/Ipc/WindowsNamedPipeServer.cs",
    "src/FaceHubManagement.Agent/Ipc/LinuxUnixSocketServer.cs",
    "src/FaceHubManagement.Agent/Platform/WindowsServiceHost.cs",
    "packaging/linux/facehub-management-agent.service",
    "packaging/linux/build-deb.sh",
    "packaging/windows/FaceHubManagement.iss",
    "src/FaceHubManagement.SmokeTest/Program.cs",
]
for relative in required:
    if not (ROOT / relative).is_file():
        fail(f"Required implementation file missing: {relative}")
if all((ROOT / relative).is_file() for relative in required):
    ok("All phase-critical implementation files exist")

# Project references
for project in ROOT.rglob("*.csproj"):
    tree = ET.parse(project)
    for node in tree.getroot().iter():
        if node.tag.endswith("ProjectReference"):
            include = node.attrib.get("Include")
            if not include:
                fail(f"ProjectReference without Include: {project.relative_to(ROOT)}")
                continue
            target = (project.parent / include.replace("\\", "/")).resolve()
            if not target.is_file():
                fail(f"Broken ProjectReference: {project.relative_to(ROOT)} -> {include}")
if not any("ProjectReference" in item for item in errors):
    ok("Project references resolve to existing files")

# No unfinished implementation markers in source.
for path in list((ROOT / "src").rglob("*.cs")) + list((ROOT / "src").rglob("*.axaml")):
    text = path.read_text(encoding="utf-8")
    for pattern in (r"\bTODO\b", r"\bFIXME\b", r"NotImplementedException", r"throw\s+new\s+NotSupportedException\(\s*\)"):
        if re.search(pattern, text, flags=re.IGNORECASE):
            fail(f"Unfinished implementation marker in {path.relative_to(ROOT)}: {pattern}")
if not any("Unfinished implementation" in item for item in errors):
    ok("No TODO/FIXME/NotImplemented markers in application source")

# Best-effort C# delimiter scanner. Removes comments and string/char literals first.
def strip_csharp_noncode(text: str) -> str:
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        if text.startswith("//", i):
            j = text.find("\n", i + 2)
            i = n if j < 0 else j
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if text.startswith('"""', i):
            j = text.find('"""', i + 3)
            i = n if j < 0 else j + 3
            continue
        if text[i] == '"':
            verbatim = i > 0 and text[i - 1] == '@'
            i += 1
            while i < n:
                if verbatim and text.startswith('""', i):
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                if not verbatim and text[i] == '\\':
                    i += 2
                else:
                    i += 1
            continue
        if text[i] == "'":
            i += 1
            while i < n:
                if text[i] == "'":
                    i += 1
                    break
                if text[i] == "\\":
                    i += 2
                else:
                    i += 1
            continue
        out.append(text[i])
        i += 1
    return "".join(out)

pairs = {"{": "}", "(": ")", "[": "]"}
for path in (ROOT / "src").rglob("*.cs"):
    code = strip_csharp_noncode(path.read_text(encoding="utf-8"))
    stack: list[tuple[str, int]] = []
    closing = {v: k for k, v in pairs.items()}
    for index, char in enumerate(code):
        if char in pairs:
            stack.append((char, index))
        elif char in closing:
            if not stack or stack[-1][0] != closing[char]:
                fail(f"C# delimiter mismatch in {path.relative_to(ROOT)} near stripped offset {index}")
                break
            stack.pop()
    else:
        if stack:
            fail(f"C# unclosed delimiter in {path.relative_to(ROOT)}: {stack[-1][0]}")
if not any("C# " in item for item in errors):
    ok("C# delimiter scan passes")

# Bash syntax.
for path in list((ROOT / "scripts").glob("*.sh")) + list((ROOT / "packaging/linux").glob("*.sh")):
    result = subprocess.run(["bash", "-n", str(path)], capture_output=True, text=True)
    if result.returncode != 0:
        fail(f"Bash syntax invalid: {path.relative_to(ROOT)}: {result.stderr.strip()}")
if not any("Bash syntax" in item for item in errors):
    ok("Bash scripts pass bash -n")

# Solution should reference all projects.
sln = (ROOT / "FaceHubManagement.sln").read_text(encoding="utf-8")
for project_name in (
    "FaceHubManagement.Core",
    "FaceHubManagement.Agent",
    "FaceHubManagement.Desktop",
    "FaceHubManagement.SmokeTest",
):
    if project_name not in sln:
        fail(f"Solution is missing project {project_name}")
if all(project_name in sln for project_name in (
    "FaceHubManagement.Core",
    "FaceHubManagement.Agent",
    "FaceHubManagement.Desktop",
    "FaceHubManagement.SmokeTest",
)):
    ok("Solution contains all four projects")

print("FaceHub Management source verification")
for check in checks:
    print(f"PASS: {check}")
if errors:
    for error in errors:
        print(f"FAIL: {error}", file=sys.stderr)
    sys.exit(1)
print("PASS: all static source checks completed")
