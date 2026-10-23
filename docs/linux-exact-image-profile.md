# Linux exact native image profile

The optional exact-image mode binds the selected main native ELF image to the
creation-owned Linux scope. It does not enable protected Linux review. The reviewer also refuses a missing
selected Linux scope or a nonmatching configured capture executor/original
budget before evidence preparation; this admission check launches nothing. The
managed birth gate, its runtime, the ELF interpreter and dynamically loaded
libraries remain a separate trust boundary.

The child copies the selected executable into a sealed executable memfd. The
original parent independently verifies the same descriptor, its kernel identity,
complete bytes and seals before RELEASE. The child executes that descriptor with
`execveat`; there is no script or pathname fallback.

The parent charges the image plus EOF to its existing 16 MiB cumulative kernel
observation quota. WORK can debit only the first 14 MiB, preserving at least
2 MiB for cleanup inside the same original ceiling. Exact images are limited
to 11 MiB inclusive, leaving approximately 3 MiB of the WORK partition for
metadata after image binding; prior observations and EOF still consume it.
The shared public size constant is profile DATA, not native authority. An
image must also fit the actual remaining WORK credit; the gate's larger
standalone extent ceiling grants no additional allowance. Tool callers also
reserve both complete image reads against the same existing file credit. A
large real CLI can be unsupported under this profile even when its Windows
counterpart works. The actual Linux CLI's size and compatibility must be checked.

The hash loop keeps one bounded read record with a finite syscall count and
actual return facts for each read. A short-read plan refusal, deadline or failed
hash does not create a successful image proof. Original open, identity, seal,
first-close and cleanup records remain independently required. Once exact-image
binding has entered, WORK admissions stop at 8,192 retained syscall rows; cleanup
can use the remainder of the SAME 16,384-row ceiling. The exact-image hash frame
uses that WORK admission limit as well. Ordinary scopes that never enter exact-image
binding retain the original 16,384-row WORK ceiling; cleanup shares the remaining
rows of that same counter. No row counter is reset when exact-image binding enters.
Long exact-image work can still exceed its finite partition and must refuse.
These partitions preserve cleanup headroom, not a guarantee that an unknown native
effect will settle or that cleanup is permitted after its original clock expires.
An exact-image descriptor whose first-close WORK admission expires can use SAME
original cleanup before close entry; once close has entered, it is never retried.
Admission, close and post failures remain retained. No rows, bytes or time are
refunded or added.

All memfd pages, source page cache, loader/runtime pages and descendants consume
the same scope `MemoryMax=4294967296` and `MemorySwapMax=0`. This is a total scope
cap; it does not promise 4 GiB of target heap in addition to the sealed image.
No memory, time, observation, file or cleanup credit is increased.

A new ordinary Linux gate build and package are required. The selected build
audit must enumerate every actual compiler source, including `ExactNativeImage.cs`,
and join its PDB source checksums to the five selected gate outputs. Old receipts
must remain preserved and cannot certify rebuilt bytes.

Genuine guest controls must establish descriptor execution, shared memory
charging, successful retirement and precise hash/extent/ELF refusal. Kernel seal
controls alone do not establish the process-owner binding. Replacing a pathname
after RELEASE does not establish a pre-RELEASE race witness. Mid-copy cancellation,
pre-RELEASE replacement and forged descriptor/READY controls remain separate
acceptance requirements before broader protected-runtime certification.
