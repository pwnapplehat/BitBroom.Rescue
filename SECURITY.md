# Security policy

## Supported versions

The latest release is supported with security fixes.

## Threat model highlights

BitBroom Rescue reads raw disks as Administrator and writes recovered files to a
user-chosen destination. The invariants we actively defend:

- **The source device is never written.** All device access goes through
  `ISectorSource`, which exposes no write methods; raw handles are opened
  `GENERIC_READ` only. Any code path that could write to a source device is a
  critical vulnerability — report it as such.
- **Destination containment:** recovered files are written only under the chosen
  destination folder. Recovered names are sanitized before joining paths, collisions
  are suffixed rather than overwritten, and the destination guard refuses the source
  volume, relative paths, and destinations inside the source root.
- **Hostile file-system input:** the NTFS/FAT/exFAT parsers and the carver treat all
  on-disk structures as untrusted (bounded reads, cycle guards on directory
  recursion and cluster chains, fixup validation, box-size sanity checks). Corrupt
  or malicious volumes must fail scans gracefully, never corrupt memory or hang.
- **Supply chain:** the engine and CLI have zero third-party runtime dependencies.
  The GUI's single dependency is the MIT-licensed WPF UI library (pinned version,
  source-auditable). The product makes **no network requests**.

## Reporting a vulnerability

Please report privately via GitHub Security Advisories ("Report a vulnerability" on
the repository) or by email to **contact@bitbroom.app**, rather than public issues.
Expect an acknowledgement within 72 hours. For parser issues, an image file (or a
script that builds one) reproducing the problem is the perfect report.
