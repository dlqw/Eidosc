# Closed-case identity resolution

- Closed-case joins and typed injections now use `SymbolId` or `TypeId` as their only nominal identity source.
- Removed hierarchy-wide case-name guessing, preventing same-named cases in different branches from being conflated.
- Added coverage for joining same-named leaf cases from distinct branches into their shared sealed root.
