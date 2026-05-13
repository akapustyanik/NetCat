# GitHub Release Notes

Use this style for public NetCat GitHub releases:

```text
NetCat X.Y.Z

Updated Month DD, YYYY.

Changes:
- Short user-facing change.
- Short user-facing fix.
- Packaging/update note when release contents changed.
```

Keep the description practical and similar to v0.3.1, v0.3.2, and v0.3.3. Avoid one-line bodies like "Release X.Y.Z. Fix ...".

Packaging note: Windows release archives should be self-contained by default. A framework-dependent archive is much smaller, but can miss runtime/native files on another PC and break VPN/Zapret startup.
