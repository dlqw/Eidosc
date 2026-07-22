# Loan summary cache restore

- Borrow diagnostic snapshot schema v3 stores a separate versioned `loan-summary-v1` payload for lifetime parameters, return provenance, outlives constraints, confidence, and the referenced ownership contract.
- Borrow-only and LLVM cache restore paths reconstruct `LoanSignature` facts instead of returning an empty borrow result after a clean cache hit.
- Body-derived loan facts remain independently invalidated by MIR body hashes; they do not alter the signature-derived ownership contract.
