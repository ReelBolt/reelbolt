# Child safety policy

ReelBolt does not allow child sexual abuse material (CSAM). This page states the policy. It is deliberately short: the operational procedures that go with it are kept private by the people who run the hosted service.

## What is not allowed

You may not upload, generate, store or share sexual content involving children, and you may not use ReelBolt to try to produce it. This applies to the hosted service at reelbolt.ai and is the standard we ask every operator of a self-hosted installation to hold.

## What the software can do

ReelBolt contains optional controls an operator can switch on:

- **Hash matching at ingest.** Image and video files can be checked against a known-material list from an external provider before they become project files. A match quarantines the file and writes an audit record. Both the control and the provider are configured by the operator, and the control is off by default on self-hosted installations.
- **Content moderation.** Prompts, uploads, generated text and exported outputs can be screened by a moderation model. See [content-moderation.md](content-moderation.md).

These controls reduce risk. They do not guarantee that prohibited material is detected, and an operator who does not enable them is not protected by them.

## If you run a self-hosted installation

You are the operator and you are responsible for the content on your installation and for the laws that apply to you. In most countries that includes duties to report CSAM you become aware of to the competent authority, for example the national hotline or the National Center for Missing & Exploited Children in the United States. Do not copy, forward or keep the material beyond what the law requires.

## If you find something

- **On the hosted service:** report it to **abuse@reelbolt.ai**. Do not attach or describe the material beyond what is needed to find it.
- **Anywhere else:** contact your national hotline (for example through <https://www.inhope.org>) or the police.

Security vulnerabilities are a different matter; see [SECURITY.md](../SECURITY.md).
