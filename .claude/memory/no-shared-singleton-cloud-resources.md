# Never a shared, fixed-name cloud resource

Two lab members may share one Google account (Hard Rule 9). Any cloud
resource identified by a fixed name — a bucket called `dna-entropy-bucket`,
a VM called `dna-entropy-box` — collides the moment two runs, or two users
sharing an account, happen at overlapping times. The prototype's own
`dna-entropy-box` is exactly the shape this rule forbids.

Every VM and bucket prefix carries the **job id**; every resource carries
the **installation id** as a label. Discovery is always by label, never by
a name a second run could also produce. `VmSpec` rejects a spec missing
these before the request is even built.
