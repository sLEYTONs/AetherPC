# Security Policy

## Supported versions

Security fixes are considered for the latest public release of AetherPC published on GitHub Releases.

## Reporting a vulnerability

AetherPC can change Windows configuration and often runs elevated. Please **do not** open a public GitHub Issue for vulnerabilities that could put users at risk if disclosed early.

### Preferred channel

Use **GitHub Private Vulnerability Reporting** on this repository (Security → Report a vulnerability), once it is enabled on the repo.

If private reporting is not enabled yet, contact the maintainer through GitHub ([@sLEYTONs](https://github.com/sLEYTONs)) with a private message / security-related contact path — do not paste exploit details in a public issue.

### What to include

- AetherPC version
- Windows 10/11 build if known
- Clear description of the issue
- Steps to reproduce
- Impact assessment (what an attacker or bug could do)
- Optional: minimal PoC (no mass-distribution guidance)

Please allow reasonable time for triage before public disclosure.

## Scope notes

- Administrator-required behavior by design is not by itself a vulnerability.
- Reports about third-party dependencies should ideally also be filed upstream when appropriate.
