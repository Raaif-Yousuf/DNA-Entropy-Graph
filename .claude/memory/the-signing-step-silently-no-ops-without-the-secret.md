# The signing step can silently no-op if its secret is unset

A known class of footgun with CI code-signing actions generally, including
Azure Trusted Signing: if a required secret (tenant id, client id, client
secret, endpoint, account, profile) is unset, the signing step can complete
"successfully" without actually having signed anything, rather than
failing loudly.

`release.yml`'s job must **assert every signing secret exists and is
non-empty before the build step runs**, and fail loudly if any is missing —
never rely on the signing action's own exit code as proof that signing
happened. `signtool verify /pa Setup.exe` on the produced artifact is the
real proof (see `OWNER_TODO.md` item 3, issue #23).
