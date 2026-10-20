# Exact-action approval trace

| Stage | Evidence |
|---|---|
| Request | `issue_refund` for `T-1001`, **$499.00**; proposed hash `7029d1d2128c537563c06d087b2d19899d9ef0124e226950e93b8958b40d74e5` |
| Approval | Approver returned approved, bound to the previously reviewed **$10.00** action hash `e4cd554597971378686182e2e439651fa827dccb3dfceaa67217144481a9e89a` |
| Tamper check | Candidate hash `7029d1d2128c537563c06d087b2d19899d9ef0124e226950e93b8958b40d74e5` did not match the approved hash |
| Harness decision | `approval_rejected_ActionMismatch` |
| Side effects | `0` |
