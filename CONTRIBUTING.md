# Contributing to BitBroom Rescue

Thanks for helping make data recovery honest and safe.

## Ground rules

1. **The source drive is read-only. Period.** All device access goes through
   `ISectorSource` (no write path). PRs that add any write capability toward a source
   device will not be accepted, regardless of feature value. Read
   [docs/SAFETY.md](docs/SAFETY.md) before touching the engine.
2. **Honesty is the product.** Confidence scores must reflect reality (and say *why*),
   health advisories must tell the truth about TRIM/SSD physics, and carve results must
   pass a format validator before being shown. No fake green flags.
3. **Parsers are tested against synthetic images.** If you touch NTFS/FAT/exFAT/carving,
   extend the in-memory image builders in `tests/.../Synthetic/` and prove the behaviour
   byte-exact. If you fix a real-world parsing bug, encode the offending structure into
   a synthetic image as a regression test.
4. **Tests must stay green**: `dotnet test`.

## Adding a carve format (checklist)

- [ ] Signature in `SignatureLibrary` (header, size strategy, sane `MaxSize`)
- [ ] A validator that rejects garbage with a valid-looking header
- [ ] Synthetic-image test carving the format byte-exact
- [ ] `dotnet test` green

## Code style

- C# latest, nullable enabled, file-scoped namespaces (`.editorconfig` is authoritative).
- No new NuGet dependencies without discussion — engine/CLI: zero dependencies;
  GUI: only the MIT-licensed WPF UI library.
- Comments explain *why*, not *what*.

## Reporting bugs

Include: Windows version, BitBroom Rescue version, the file system involved, whether
you scanned a live device or an image, and the audit log from the recovery destination
folder if applicable. For parser bugs, a small image reproducing the issue is gold.

## Security issues

See [SECURITY.md](SECURITY.md) — please do not open public issues for vulnerabilities.
