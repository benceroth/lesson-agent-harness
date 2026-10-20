# Security policy

This is a teaching sample, not a production security product. It still takes security reports seriously.

## Reporting a vulnerability

Please report privately through GitHub's "Report a vulnerability" (private security advisory) on this repository. Do not open a public issue for an exploitable bypass. Include the scenario, the expected control, and a failing test if you can.

You can expect an acknowledgement within a few days. There is no bug bounty and no SLA.

## Scope

In scope: a way to make the harness execute an action that validation, approval, idempotency or limits should have stopped; secret or PII leakage through the telemetry layer.

Out of scope: the known gaps listed in `docs/threat-model.md` (in-memory approval grants, non-cooperative tools, pattern-based redaction, careless human approvers), and weaknesses in the deliberately naive loop.

## Handling data

The repository contains only fake data (`example.test` addresses, fake ticket ids). Never commit real customer data, keys or tokens; CI runs a secret scan.
