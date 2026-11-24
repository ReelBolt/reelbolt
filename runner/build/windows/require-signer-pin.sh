#!/bin/sh
# Fail a PUBLISHING Windows release that has no signer pin. Environment: PUBLISH
# (true|false), SIGNING (none|trusted-signing|pfx), EXPECTED_SIGNER_SUBJECT and/or
# EXPECTED_SIGNER_THUMBPRINT (from RUNNER_WINDOWS_SIGNER_SUBJECT / _THUMBPRINT).
# A dry run only warns. See signer-check.sh for the formats.
set -eu
if [ "${SIGNING:-none}" = none ]; then
	exit 0
fi
if [ -n "${EXPECTED_SIGNER_SUBJECT:-}${EXPECTED_SIGNER_THUMBPRINT:-}" ]; then
	exit 0
fi
if [ "${PUBLISH:-false}" = true ]; then
	echo "::error::Publishing a signed Windows release needs a signer pin: set the repository variable RUNNER_WINDOWS_SIGNER_SUBJECT (the exact subject DN from 'osslsigncode verify', for Trusted Signing) and/or RUNNER_WINDOWS_SIGNER_THUMBPRINT (leaf SHA-256, for PFX). Without one, any validly signed file passes. Nothing was published."
	exit 1
fi
echo "::warning::No RUNNER_WINDOWS_SIGNER_SUBJECT or RUNNER_WINDOWS_SIGNER_THUMBPRINT is set; a real publish would be refused."
