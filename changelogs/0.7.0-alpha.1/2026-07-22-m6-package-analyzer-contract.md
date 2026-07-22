# M6 package analyzer contract

- Verify `meta.Package -> Seq[meta.Diagnostic]` analyzers emit structured diagnostics with IDE-applicable fixes.
- Reject emit protocols when configured as analyzers, preserving the package as read-only.
