# Security Policy

Partner Center Bridge holds delegated credentials for customer tenants — most notably an
encrypted Secure Application Model refresh token that can act across every GDAP relationship
the partner has. Treat any weakness in that path as high severity.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Use GitHub's private vulnerability reporting: **Security → Report a vulnerability** on this
repository. Include reproduction steps and the deployment shape (docker-compose vs Kubernetes)
if it matters.

You should get an initial response within a few days. Coordinated disclosure is appreciated;
credit is given unless you prefer otherwise.

## Scope notes for self-hosters

- The API is designed to sit behind your own OIDC provider (`Auth` section). Running with
  `Auth:Enabled=false` outside local development is a misconfiguration, not a vulnerability.
- The committed `deploy/` tree is a deployment **template**, not a live environment. Its
  `secrets.sops.yaml` contains only `CHANGEME` placeholders and is not evidence that SOPS is
  configured in this repository. Do not replace those placeholders with real values.
- A real GitOps deployment belongs in the separate `homelab_ac` repository and must use that
  repository's SOPS-encrypted secret convention; non-GitOps deployments must inject secrets from
  their environment or secret manager. Never commit plaintext credentials to either repository.
  A leaked SAM refresh token should be treated as a full partner-tenant compromise: revoke the
  Entra app's sessions and re-seed.
