# Security policy

## Reporting a vulnerability

Please report a vulnerability privately, not in a public issue or pull request.

1. Go to the **Security** tab of
   [Raaif-Yousuf/DNA-Entropy-Graph](https://github.com/Raaif-Yousuf/DNA-Entropy-Graph).
2. Choose **Report a vulnerability** (GitHub's private vulnerability reporting). Only you and
   the maintainer can see the report.

Include what you found, the app or worker version, and the steps to reproduce it. Do not
attach a real sequence, a token, or a key; describe it instead. If the Security tab shows no
**Report a vulnerability** button, open a public issue that says only "I need a private way to
report a security problem" and contains no details; the maintainer will reply with one.

You can expect an acknowledgement within 7 days. This is a one-person research project, so a fix
timeline depends on severity; the maintainer will say what they plan and will credit you in the
release notes unless you ask not to be named.

## What is in scope

The app, the worker container, the VM startup script, the installer and the update channel.
[`docs/threat_model.md`](docs/threat_model.md) is the account of what each part can and cannot
reach, and what we already treat as a risk. Examples of in-scope reports: a way to make the app
run code from a file it opened, a token or key written where it should not be, a resource the
app creates in a user's Google Cloud project that another user or project can read, or a
release asset that does not match what the build produced.

## What is out of scope

- Problems in Google Cloud, Evo 2, igv.js or another dependency itself. Report those upstream;
  tell us too if our use of them makes it worse.
- The user's own Google Cloud project settings, bills and quotas.
- Findings that need an attacker who already controls the user's Windows account.

## Supported versions

There is no release yet. Once v1.0 ships, only the latest release is supported, and the app
updates itself through its update channel.

## Secrets

The repository holds no secrets by rule (Hard Rule 12). If you find one, report it the same
way, privately.
