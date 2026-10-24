# Installed mutation effective policy

Forge compares the actual retained report-v2 execution policy with the effective
policy of the independently pinned, captured `mutate4csharp.json`. Raw configuration
SHA remains unchanged in the invocation binding. A valid sparse configuration is
not byte-identical to the producer's complete seven-field policy.

The supported defaults come from official Mutate source
`d3fa140d11e19e1ee2e15c7ed922a532a9e73bb1`:
`ExecutionPolicy.Default` and `CheckConfiguration.ParsePolicy` select one worker,
100 mutants, 600 baseline seconds, 120 mutant seconds, 1800 overall seconds,
`allowNotApplicable=false`, and one stability repetition. Explicit values take
precedence. Unknown, duplicate, null, malformed, nonintegral or incomplete report
fields fail closed. No future producer default is inferred from a report.

The supported Forge starting profile independently limits one mutation worker,
100 selected mutants and 600 seconds for each phase and overall. Consequently a
missing overall deadline defaults to the real 1800 and is refused; it is never
silently replaced with 600. The same original host/process lifetime and its
clipped native deadlines remain authoritative. Stability repetitions retain the
producer's finite maximum of 100 and cannot enlarge selected-count/time limits.

The retained actual d3fa killed run completed six of six selected mutations with
six kills, a green baseline and exit zero. Its configuration explicitly selected
the one-worker/100-mutant/600-overall policy and omitted four defaulted fields.
The old Forge raw-object comparison refused the complete producer policy; that
failure and original report stay unchanged. Semantic policy DATA tests and this
fix cannot manufacture a current binding from that saved report. Fresh calls
still require the real original invocation, current source, installed payload,
SDK, resource and native closure checks before any gate receipt is issued.
