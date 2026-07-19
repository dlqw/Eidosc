# Typed `meta.Function` handle

- Body-transform generators now receive a stable `meta.Function` value instead of a generic declaration handle.
- Function handles expose typed read-only identity, name, declaration, and source-span properties to Meta queries.
- Identity transforms preserve the public function contract and continue through atomic body validation.
