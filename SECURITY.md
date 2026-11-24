# Security policy

## Reporting a vulnerability

Do not open a public issue for a security problem. Report it privately in one of these ways:

1. Use GitHub's private vulnerability reporting: the **Security** tab of this repository, then **Report a vulnerability**.
2. Email **security@reelbolt.ai**.

Include what you found, the affected component and version, steps to reproduce, and the impact you expect. Do not include other people's data.

## What to expect

- We aim to acknowledge a report within a few working days.
- We will tell you when it is fixed and, if you want, credit you.
- Please give us reasonable time to fix the problem before you disclose it publicly.

## Scope

In scope: code in this repository and the default configuration it ships.

Out of scope: vulnerabilities in third-party services or dependencies without a way to exploit them through ReelBolt, denial of service by volume, and social engineering.

## Operating a self-hosted instance

Change every default credential in `.env.example` before you expose an instance, set a strong `JWT_SIGNING_KEY`, and keep the stack behind TLS. Keep your images and dependencies updated.
