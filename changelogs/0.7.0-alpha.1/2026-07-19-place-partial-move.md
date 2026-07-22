# Place-level partial move tracking

- Affine verification now records field/index place moves instead of treating every projection as an untracked local.
- Reading an aggregate or overlapping projection after a place move reports a partial-move diagnostic.
- Writing the moved place explicitly reinitializes that place and restores aggregate readability; no implicit Clone is inserted.
