# Inter

Simulates the Banco Inter Pix, billing and banking API surface listed below for local tests. Not affiliated with Banco Inter.

## Covered

- Pix charges: immediate (cob) and due-date (cobv), create, read, pay and remove.
- Pix refunds (devolucao) with their lifecycle.
- Billing (Cobranca v3): boleto with Pix, issuance, payment, overdue and failed issuance.
- Banking: balance, statement, outgoing Pix and boleto payment.
- Webhooks, delivered over mTLS.
- OAuth client credentials with scopes and client certificate (mTLS).

## Sources

- https://developers.inter.co/references/pix
