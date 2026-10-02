<p align="center">
  <img src="docs/images/facehubmgr-icon.png" alt="FaceHubMgr" width="128">
</p>

<h1 align="center">FaceHubMgr</h1>

<p align="center">
  <strong>Desktop management and monitoring for FaceHub systems on Windows and Ubuntu.</strong>
</p>

<p align="center">
  Monitor system resources, manage services, firewall rules, logs, and system status
  from a single desktop application.
</p>

<p align="center">
  <a href="README.md">Tiếng Việt</a> ·
  <strong>English</strong>
</p>

<p align="center">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet">
  <img alt="Avalonia UI" src="https://img.shields.io/badge/UI-Avalonia-8B44AC">
  <img alt="Ubuntu" src="https://img.shields.io/badge/Ubuntu-24.04-E95420?logo=ubuntu&logoColor=white">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows">
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#screenshots">Screenshots</a> •
  <a href="#architecture">Architecture</a> •
  <a href="#installation">Installation</a> •
  <a href="#build-from-source">Build</a> •
  <a href="#configuration">Configuration</a>
</p>

---

<p align="center">
  <img src="docs/images/facehubmgr-feature.png"
       alt="FaceHubMgr - FaceHub system management and monitoring"
       width="800">
</p>

## Overview

**FaceHubMgr** is a cross-platform desktop application designed to monitor
and manage computers running **FaceHub**.

Instead of switching between multiple tools and commands such as:

```text
systemctl
journalctl
ufw
nvidia-smi
free
df
ip
```

FaceHubMgr brings common administration tasks together into a single
graphical interface.

It is intended for:

- FaceHub deployment engineers
- System operators
- Servers or workstations running FaceHub
- Systems that require quick service and resource monitoring
- Environments where direct command-line administration should be minimized

> FaceHubMgr does not replace operating-system administration tools.
> Terminal and native system tools are still recommended for advanced diagnostics.

---

## Features

### System resource monitoring

FaceHubMgr provides live information about:

- CPU Usage
- RAM Usage
- Disk Usage
- NVIDIA GPU Usage
- VRAM Usage
- GPU Model
- NVIDIA Driver
- CUDA Driver API
- Network information

---

### Service management

Monitor and control services used by a FaceHub deployment.

Typical services include:

- FaceHub
- Nginx
- MongoDB

FaceHubMgr can display:

- `Running`
- `Starting`
- `Stopping`
- `Stopped`
- `Failed`
- `Unknown`
- PID
- Process uptime
- Configured ports

Supported actions include:

- Start
- Stop
- Restart

---

### Firewall management

FaceHubMgr provides an interface for managing TCP ports commonly used by
the system.

You can:

- View configured ports
- Add ports
- Allow ports
- Deny ports
- Refresh firewall state

> **Warning**
>
> Firewall changes can interrupt remote access to the machine.
> Be especially careful with SSH and other administration ports.

---

### Logs

FaceHubMgr provides:

- Recent Logs on the Dashboard
- Dedicated Logs page
- Detailed log inspection
- Agent rolling logs
- Operational and diagnostic information

---

### Cross-platform support

| Operating system | Status | Service backend |
|---|---|---|
| Ubuntu Linux | ✅ Supported | systemd |
| Windows 10 | ✅ Supported | Windows SCM |
| Windows 11 | ✅ Supported | Windows SCM |

---

## Screenshots

### Dashboard

<p align="center">
  <img src="docs/images/dashboard.png"
       alt="FaceHubMgr Dashboard"
       width="800">
</p>

### Services

<p align="center">
  <img src="docs/images/services.png"
       alt="FaceHubMgr Services"
       width="800">
</p>

### Firewall

<p align="center">
  <img src="docs/images/firewall.png"
       alt="FaceHubMgr Firewall"
       width="800">
</p>

### System Information

<p align="center">
  <img src="docs/images/system-info.png"
       alt="FaceHubMgr System Information"
       width="800">
</p>

### Logs

<p align="center">
  <img src="docs/images/logs.png"
       alt="FaceHubMgr Logs"
       width="800">
</p>

---

## Architecture

FaceHubMgr separates the desktop user interface from privileged
operating-system operations.

```text
┌───────────────────────────────┐
│       FaceHubMgr Desktop      │
│          Avalonia UI          │
└───────────────┬───────────────┘
                │
                │ Local IPC
                ▼
┌───────────────────────────────┐
│    FaceHub Management Agent   │
│       Privileged Service      │
└───────────────┬───────────────┘
                │
        ┌───────┼────────┐
        ▼       ▼        ▼
     systemd/   UFW/    NVML/
       SCM       OS     NVIDIA
```

The desktop UI does not need to run entirely as `root` or Administrator
during normal operation.

Privileged operations are handled by the dedicated
**FaceHub Management Agent**.

---

### IPC on Ubuntu

The desktop application communicates with the Agent through a Unix Domain
Socket:

```text
/run/facehub-management/agent.sock
```

The socket is restricted to the:

```text
facehub-management
```

group.

---

### IPC on Windows

Windows uses a local Named Pipe:

```text
FaceHubManagement.Agent
```

The Named Pipe uses ACL-based access control and rejects remote clients.

---

## Technology

FaceHubMgr is primarily built with:

- **C#**
- **.NET 8**
- **Avalonia UI**
- **systemd** on Linux
- **Windows Service Control Manager** on Windows
- **NVML** for NVIDIA GPU information
- **Unix Domain Socket** on Linux
- **Named Pipe** on Windows
- **UFW / iptables** on Ubuntu

---

## Installation

### Ubuntu

FaceHubMgr can be distributed as a Debian package.

Example:

```bash
sudo apt install ./facehub-management_<version>_amd64.deb
```

The package installs:

- FaceHubMgr Desktop
- FaceHub Management Agent
- Configuration
- systemd service
- Desktop launcher

After installation, verify the Agent:

```bash
systemctl status facehub-management-agent.service
```

Verify the IPC socket:

```bash
ls -l /run/facehub-management/agent.sock
```

If your current user was newly added to the:

```text
facehub-management
```

group, **log out and log back in once** before launching FaceHubMgr.

---

### Windows

Use the installer generated by the project.

The installer deploys:

- Desktop application
- FaceHub Management Agent
- Configuration
- Windows Service
- Start Menu shortcut
- Optional Desktop shortcut

Verify the Agent from PowerShell:

```powershell
Get-Service FaceHubManagementAgent
```

---

## Build from source

### Requirements

- .NET SDK 8
- Git
- Ubuntu 24.04 or Windows 10/11
- NVIDIA Driver with NVML support if NVIDIA GPU monitoring is required

Check the installed .NET SDK:

```bash
dotnet --version
```

or:

```bash
dotnet --list-sdks
```

The project includes `global.json` to specify the expected SDK version.

---

## Build on Ubuntu

Clone the repository:

```bash
git clone <YOUR_REPOSITORY_URL>
cd FaceHubManagement
```

Restore dependencies:

```bash
dotnet restore FaceHubManagement.sln
```

Build Release:

```bash
dotnet build FaceHubManagement.sln -c Release
```

Make the Linux scripts executable:

```bash
chmod +x scripts/publish-linux.sh
chmod +x packaging/linux/build-deb.sh
```

Build the Debian package:

```bash
./packaging/linux/build-deb.sh linux-x64
```

The generated package is placed under:

```text
artifacts/packages/
```

### ARM64

To build an ARM64 package:

```bash
./packaging/linux/build-deb.sh linux-arm64
```

---

## Build on Windows

Restore and build:

```powershell
dotnet restore .\FaceHubManagement.sln
dotnet build .\FaceHubManagement.sln -c Release
```

Publish:

```powershell
.\scripts\publish-windows.ps1
```

To generate the installer, install **Inno Setup 6** and run:

```powershell
.\scripts\build-installer.ps1
```

The generated installer is placed under:

```text
artifacts\installer\
```

---

## Configuration

FaceHubMgr manages services that already exist on the operating system.

FaceHubMgr **does not launch Nginx, FaceHub, or MongoDB as child processes
of the Desktop application**.

This keeps the lifecycle of the managed services independent from the
FaceHubMgr user interface.

---

### Ubuntu

Agent configuration:

```text
/etc/facehub-management/agentsettings.json
```

After changing the configuration:

```bash
sudo systemctl restart facehub-management-agent.service
```

---

### Windows

Agent configuration:

```text
C:\ProgramData\FaceHub Management\agentsettings.json
```

After changing the configuration:

```powershell
Restart-Service FaceHubManagementAgent
```

Service names and ports should be adjusted to match the actual FaceHub
deployment.

---

## NVIDIA GPU Monitoring

FaceHubMgr uses the **NVIDIA Management Library — NVML** to retrieve GPU
information.

First verify the NVIDIA driver:

```bash
nvidia-smi
```

If `nvidia-smi` works correctly, FaceHubMgr can retrieve the corresponding
GPU information through NVML.

Available information can include:

- GPU Name
- GPU Usage
- VRAM Usage
- VRAM Total
- Driver Version
- CUDA Driver API Version

If NVML is unavailable, GPU monitoring may not work, while:

- CPU monitoring
- RAM monitoring
- Service management
- Other system functionality

can continue to operate.

> CUDA Toolkit is not required solely for reading GPU utilization and VRAM.
> The main requirements are the NVIDIA Driver and NVML.

---

## System verification

FaceHubMgr includes an integration smoke-test utility.

### Ubuntu

```bash
/opt/facehub-management/smoketest/FaceHubManagement.SmokeTest
```

### Windows

```powershell
.\FaceHubManagement.SmokeTest.exe
```

The default smoke test does not change the state of managed services.

To explicitly test a real service action:

```text
FaceHubManagement.SmokeTest --actions --service nginx
```

or:

```text
FaceHubManagement.SmokeTest --actions --service facehub
```

> `--actions` may Start or Restart a real service.
> Only use it on a machine or service that is safe to interrupt.

---

## Repository structure

```text
FaceHubManagement/
├── src/
│   ├── FaceHubManagement.Core/
│   ├── FaceHubManagement.Agent/
│   ├── FaceHubManagement.Desktop/
│   └── FaceHubManagement.SmokeTest/
│
├── config/
│   └── agentsettings.json
│
├── packaging/
│   ├── linux/
│   └── windows/
│
├── scripts/
├── docs/
├── .github/
│   └── workflows/
│
├── FaceHubManagement.sln
├── README.md
└── README.en.md
```

### Main projects

| Project | Purpose |
|---|---|
| `FaceHubManagement.Core` | Shared models, protocol, and IPC components |
| `FaceHubManagement.Agent` | Local Agent responsible for privileged operations |
| `FaceHubManagement.Desktop` | Avalonia-based desktop application |
| `FaceHubManagement.SmokeTest` | Integration and deployment verification |

---

## Security model

FaceHubMgr is designed to separate the Desktop UI from privileged system
operations.

Key principles include:

- The Desktop does not need to run as `root`
- Privileged operations are isolated inside the Agent
- IPC is local-only
- Unix socket permissions are restricted
- Windows Named Pipe uses ACL-based access control
- Service actions are controlled
- IPC frame size is bounded
- Requests use IDs and timeouts
- The Desktop cannot execute arbitrary shell commands

For implementation details, see:

---

## Important notes

FaceHubMgr is a **management application**, not an installer or repair tool
for the software it manages.

FaceHubMgr does not automatically install or repair:

- FaceHub
- Nginx
- MongoDB
- NVIDIA Driver
- Other operating-system services

If an underlying service fails, native operating-system diagnostic tools may
still be required.

For example:

```bash
systemctl status facehub.service
```

and:

```bash
journalctl -u facehub.service
```

When changing firewall rules on a remote machine, make sure SSH or another
required administration port remains allowed before applying changes.

---

## Contributing

Issues and Pull Requests are welcome for:

- Bug fixes
- UI/UX improvements
- Platform compatibility
- Performance
- Monitoring
- Packaging
- Documentation

When reporting a problem, please include:

- Operating system and version
- FaceHubMgr version
- Relevant service state
- Agent logs
- Steps to reproduce
- GPU and NVIDIA Driver information for GPU-related issues