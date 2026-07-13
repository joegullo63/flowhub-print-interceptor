# Security and operational safety

Print Interceptor is an operational safeguard for Windows POS stations. It disables reliance on automatic driver drawer timing and only sends a drawer pulse after a validated Flowhub transaction receipt or explicit operator approval.

The software fails to an authorization prompt when source capture, parsing, correlation, or classification is missing or ambiguous. A fulfillment match keeps the drawer locked.

## Trust boundary

This project is not a security boundary against malicious software or an administrator on the same Windows account. A process with equivalent access can change printer settings, submit its own raw printer command, stop the interceptor, or alter local configuration.

Use dedicated, least-privilege POS accounts; restrict software installation; retain audit logs; and test every printer/driver combination before production use.

## Reporting

Do not open a public issue containing receipts, customer information, authentication data, store identifiers, or logs with sensitive data. Contact the repository owner privately for sensitive reports.

