## Fix explicit generic argument ordering

- Preserve declaration order when applying explicit type arguments after constrained type inference, preventing distinct generic parameters from being swapped in trait-heavy and ownership-safe standard-library calls.
