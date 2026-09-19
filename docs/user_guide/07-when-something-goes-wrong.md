# 7. When something goes wrong

This page is organized by what you actually see on screen, not by an error code. Find the
heading closest to your situation. Every error card in the app also has a **Copy
details** button; if the steps here do not fix things, click that and include it when you
file an issue, since it carries the exact technical error and your run's id without
carrying your sequence data.

**When to file an issue in general:** if you followed the one thing to do and it still
does not work, or the app's own message does not match anything on this page. Open an
issue from the app (**Settings > Diagnostics > Report a problem**, which pre-fills a
GitHub issue for you to review before sending, nothing is sent automatically) or directly
on the project's GitHub page.

## Signing in

**"Your Google sign-in expired"**, this happens on its own, roughly once a week, while
the app's Google approval is still in its early "testing" phase; it is expected, not a
sign anything is broken. Click **Sign in again**. *(`SIGNIN_EXPIRED`)*

## The app can't finish setting up your project

**"You can use this project but not set it up"**, you are a member of this Google Cloud
project but not its Owner, so you cannot make the changes setup needs. Either ask the
project's Owner to do the setup, or create your own project instead (Step 3 in
[02-connect-google-cloud.md](02-connect-google-cloud.md)). *(`NOT_PROJECT_OWNER`)*

**"This project has no billing account"**, Google will not start any computer without a
payment method on file. Link one from the message's button, or create a new billing
account if you do not have one yet. *(`NO_BILLING`)*

**"Free Trial accounts can't use GPUs"**, a brand-new Google Cloud account defaults to a
Free Trial, which cannot rent a graphics-card computer at all. Click **Open billing** and
activate your full account; your free credit carries over. *(`FREE_TRIAL_NO_GPU`)*

**"Compute Engine isn't switched on yet"**, a Google service the app needs is off in
your project. The app turns it on for you automatically when you click through this
message; if it fails, it means you are not the project's Owner (see above).
*(`API_DISABLED`)*

**"Your organization's Google Cloud settings block this"**, your university or company
has a policy that stops this kind of app from running in their accounts. Copy the message
and send it to your IT department, or use a personal Google account instead.
*(`ORG_POLICY_BLOCK`)*

**"Your organization blocks internet access for cloud computers"**, a stricter version of
the above; some organizations do not allow rented computers to reach the internet at all,
which this app's computers need in order to download the analysis software. Same fix:
ask IT, or use a personal account. *(`NO_EXTERNAL_IP`)*

**"You are not allowed to run computers as the worker identity"**, a narrower permission
problem than being a non-Owner; ask your project's Owner for the specific role named in
the message, or use the default option the app offers instead. *(`PERMISSION_ACTAS`)*

## No graphics card is available

**"Your project isn't allowed any GPUs yet"**, this is the one-time approval described in
[02-connect-google-cloud.md](02-connect-google-cloud.md) Step 6. Click **Request 1 L4 GPU
for me**, or follow [`docs/gcp_setup_manual.md`](../gcp_setup_manual.md) by hand.
*(`NO_GPU_QUOTA`)*

**"Google won't accept quota requests from this billing account yet"**, your billing
account is too new; Google wants a little payment history first. There is no way to speed
this up. Try again in a few days, or use **Wait for a GPU** (a cheaper option that queues
for capacity instead of failing outright). *(`QUOTA_NOT_ELIGIBLE`)*

**"No GPUs free right now"**, every data center location the app tried is temporarily out
of graphics cards; this is genuinely temporary, not a problem with your account. Try
again in about 10 minutes, try a cheaper "Spot" computer (which can be interrupted but
costs much less), or try a bigger computer tier. *(`GPU_STOCKOUT`)*

## The rented computer stopped working mid-run

**"The computer was shut down before finishing"**, either a hardware issue on Google's
side, or (if you chose the cheaper "Spot" option) the computer was reclaimed, since Spot
computers can be interrupted at any time in exchange for a lower price. Whatever your
sequence had produced so far is saved; download the partial results, or retry on the
full-price option, which is not interruptible. *(`VM_DIED`, `SPOT_PREEMPTED`)*

**"The analysis program failed"**, a genuine software problem, not something you did
wrong. If your file had several records, or your batch had several files, whatever
finished before the failure is still uploaded and downloadable; only the one record or
file that was running when it failed is missing. The technical log is attached to this
error; please file an issue with it. *(`WORKER_CRASH`)*

**"The computer started but never became ready"**, the graphics card did not come up
correctly on this particular rented computer, which happens occasionally. Delete it and
retry; a fresh computer almost always works. *(`GPU_NOT_VISIBLE`)*

**"Couldn't download the analysis software onto the computer"**, a temporary network
problem between the rented computer and the software it needs. Click **Retry**.
*(`IMAGE_PULL_FAILED`)*

**"Your app and your analysis software don't match"**, this can happen right after the
app updates itself, if the cloud side has not caught up yet. It usually clears up on its
own within a few minutes; if it does not, check for an app update and install it.
*(`WORKER_VERSION_MISMATCH`)*

**"Something went wrong before your analysis could start"**, a bug on the app's side, not
something you did. Please file an issue; there is nothing to fix on your end.
*(`MANIFEST_INVALID`)*

**"The computer stopped reporting progress"**, the app has not heard from the rented
computer in a while; it may just be slow, or it may have genuinely stopped. You can wait
a little longer, or stop or delete it yourself. *(`HEARTBEAT_LOST`)*

**"Stopped at your safety time limit"**, every run has a maximum-duration safety cap (4
hours by default) so a stuck run cannot run forever and rack up cost unnoticed. If your
sequence is genuinely large enough to need longer, raise the limit in the run's Advanced
options and re-run. *(`RUN_TIME_LIMIT`)*

## The model couldn't run your sequence

**"The sequence window is too big for this GPU's memory"**, your context length setting
(Advanced options) needs more memory than the chosen computer has. Lower the context
length, or use a bigger computer tier. *(`MODEL_OOM`)*

**"This model needs a bigger, rarer type of graphics card than you picked"**, one or two
of the larger model options only run on a specific, expensive, hard-to-get computer tier.
Switch back to the default model (`evo2_7b`), which works on the standard, readily
available tier. *(`MODEL_NEEDS_HOPPER`)*

**"Something is wrong with your input file"**, the app tells you exactly what and where
(for example, an unexpected character at a specific position). Fix the file, or turn on
**Treat as RNA** if the message suggests that. This is the same check described in
[03-run.md](03-run.md). *(`INPUT_INVALID`)*

## Results are missing or won't download

**"These results were deleted after your retention period"**, cloud copies of results
are kept for a set number of days (90 by default) and then automatically removed to save
space and cost. If you still have a local copy, use it; otherwise, re-run the analysis.
*(`RETENTION_EXPIRED`)*

**"Can't reach Google Cloud"**, a network problem on your own end, or Google's. Check
your internet connection and click **Retry**; your results are safe in the cloud for your
full retention period regardless of when you manage to download them.
*(`DOWNLOAD_FAILED`)*

## The app stopped me from running something

**"This run would go over your monthly warning cap"**, you set a spending limit in
Settings and this run's estimated cost would cross it. Click **Run anyway** if you are
sure, or raise the cap in Settings. *(`SPEND_CAP`)*

**"This batch is too large"**, more files or letters than the batch ceiling allows (see
[03-run.md](03-run.md)), a cost guard rather than an arbitrary restriction. The message
gives your batch's actual numbers against the limit; split it into smaller runs, or raise
the limit yourself if you mean to run this much at once. *(`BATCH_LIMIT_EXCEEDED`)*

## Related

[06-costs-and-cleanup.md](06-costs-and-cleanup.md), for anything cost-related that is not
an outright error.
[02-connect-google-cloud.md](02-connect-google-cloud.md), for the initial setup steps
several of the messages above point back to.
[glossary.md](glossary.md) and [faq.md](faq.md) for background on any term used above.
