# Billing off, API disabled, and stockout look identical

MEASURED in the prototype's own real misconfigured GCP project. A project
with billing disabled, the Compute API disabled, and a genuine zonal
stockout all produce visually identical `Insert` failures from the caller's
point of view — every one reads as "cannot create the resource right now."

`CloudErrorClassifier` buckets every error into `billing | api_disabled |
quota | stockout | already_exists | permission | org_policy | network |
other`. `billing`, `api_disabled`, `permission` and `org_policy` abort
immediately with a named user action; only `quota` and `stockout` are worth
retrying or walking a zone ladder for. The app also runs preflight in a
fixed order — project ACTIVE, billing enabled, Compute API enabled, GPU
quota > 0, bucket exists — specifically so a caller never has to guess which
of these four causes an `Insert` failure actually was.

See `quota-is-per-region-and-fixable-stockout-is-not.md` for the one
distinction inside "worth retrying."
