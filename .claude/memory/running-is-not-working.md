# RUNNING is not working

Instance status (`RUNNING`, `PROVISIONING`, etc.) says nothing about
whether the job inside the VM is actually making progress. The **only**
real health signal is the worker's own heartbeat in `status.json`.

`worker/vm/startup.sh` writes `nvidia-smi`'s result as its **first**
progress line specifically so "the VM booted but has no usable GPU" is
distinguishable from "still booting" from the very first line of output.
The app fails the run and stops the VM if no heartbeat lands within the
boot deadline, or if that first line reports no GPU.

Corollary for a manual check: `gcloud compute instances describe` or its
equivalent proves the VM exists, never that the job is healthy. Read
`status.json`.
