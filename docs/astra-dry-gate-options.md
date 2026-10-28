# Pluggable DRY gate: options and recommendation

Research date: 2026-10-28.

Status: design options, not an implemented gate or an approved production rollout.
This document records the investigation of [Delivery Forge PR #52][pr52] and
[Engram issue #238][issue238]. The recommendation is to combine deterministic
clone detection with rules preserving completed refactors, then evaluate models
for ambiguous cases.

## Objective and cost priorities

Inspect proposed code additions and updates, detect avoidable duplication, and
block delivery until the applicable DRY findings are resolved. Prefer:

1. Fully deterministic checks with no inference cost.
2. A configurable decision model, such as Jev.
3. A cheap generative model, such as Luna or Haiku, with Terra or Sonnet available
   as configurable alternatives or escalation targets.

Engram queries may support any option. Cheap research subagents should handle
work that does not require Astra-level reasoning; both research subagents used
Luna for this investigation.

The proposed gate contract is:

> This patch introduces or worsens no unapproved duplication within the declared
> analysis scope.

This avoids making unrelated legacy cleanup a prerequisite for every patch.
DRY concerns repeated knowledge and responsibilities, not just repeated text.
Similar code can require separate implementations; different-looking code can
duplicate the same business rule. A passing check establishes compliance with
its configured policy, not a universal proof that no equivalent code exists.

## What already exists

### Delivery Forge: PR #52

PR #52, merged on 2026-10-22, provides a trusted extension mechanism for
deterministic evaluations. The inspected local merge commit was `572cdc5`.

| Existing capability | Relevance to a DRY gate |
| --- | --- |
| `IDeterministicEvaluationPlugin` | Stable ID/version/tool identity, input detection, validation, pre-execution snapshot, normalization, and bound artifacts. |
| `TrustedEvaluationPluginRegistry` | Host code registers implementations and gate/tool/version bindings. Repository policy can select registered plugins, but cannot provide executable implementations or commands. |
| `SelectApplicable` | Filters caller-supplied candidate paths. It does not itself provide a generic changed-code diff. |
| `GateRequest` | Carries repository base/head/tree identity and invocation details. |
| `GateRunner` | Runs the tool, normalizes results, retains artifacts and hashes, checks repository identity, and preserves the raw plugin outcome separately from the final outcome. |
| Explicit enabled state | Disabling a required check does not satisfy the gate; it remains `INCOMPLETE`. |
| Outcome vocabulary | `PASS`, `FAIL`, `INCOMPLETE`, `ERROR`, and `NOT_APPLICABLE`. Missing or incomplete evidence must not become `PASS`. |

The anti-slop integration demonstrates frozen Git input materialization,
protected-base policy, pinned tool identity, and receipt sidecars. Its specialized
Git reader also obtains changed paths, but that functionality is not yet a
general-purpose diff input contract for arbitrary plugins.

**Production limitation:** the supported anti-slop composition is advisory.
External plugins can execute and produce evidence, but registration alone does
not confer production authority. `AuthoritativeEvaluationHost` is unavailable,
future-v2 authority is disabled, and there is no supported production launcher
automatically invoking anti-slop. A new authoritative DRY gate requires the
separate executor/signer boundary and producer/consumer integration described in
[Quality gates and evidence receipts](quality-gates.md). Simply adding a required
gate or trusting a raw plugin `PASS` is insufficient.

Source entry points:

- [Plugin contract and selection](../components/evidence/DeliveryForge.Evidence/DeterministicEvaluationPlugins.cs)
- [Trusted registration](../components/evidence/DeliveryForge.Evidence/TrustedEvaluationPluginRegistry.cs)
- [Request and evidence models](../components/evidence/DeliveryForge.Evidence/EvidenceModels.cs)
- [Gate execution](../components/evidence/DeliveryForge.Evidence/GateRunner.cs)
- [Frozen repository input reader](../components/evidence/DeliveryForge.Evidence/GitRepositoryIdentityReader.cs)
- [Advisory composition](../components/evidence/DeliveryForge.Evidence/AdvisoryAntiSlopComposition.cs)
- [PR #52 acceptance and limitations](issues-48-51-acceptance.md)

### Engram: issue #238 and the existing scan

Issue #238 is a recurring DRY audit. Its historical examples include:

| Pattern | Why it matters |
| --- | --- |
| Identical commit-store wrappers across provider indexers | Strong candidate for deterministic clone detection and shared helper reuse. |
| Similar Git/TFVC contract extraction | Fetch closures and log strings differ; sharing may be appropriate after preserving those differences. |
| Repeated indexing phase skeletons | Ordering, call-site extraction, and provider behavior can differ materially. Similarity alone cannot justify consolidation. |
| Repeated AST navigation helpers | Language-agnostic helpers can be shared; language-specific node and string semantics may need separation. |
| Private Store DTOs, migrations, and performance specializations | Examples where forced abstraction can be inappropriate. |

These are examples from the evolving issue, not a claim that every historical
hotspot remains unresolved. Its latest generated candidate section observed
during research was dated 2026-10-24.

Engram already has a [weekly workflow][dryworkflow] that runs `jscpd@4.2.5` over
`src/`, with `--min-lines 10`, `--min-tokens 70`, and JSON reporting. It then uses
`tools/Engram.DryUpScan` for thresholds, deduplication, likely-resolved detection,
and reporting. Related self-audit tooling renders issue drafts. This is an audit
and reporting pipeline, not the proposed patch-blocking Delivery Forge gate.

The detector, report parser, and fingerprinting approach are worth assessing for
reuse. Issue maintenance and issue creation are separate responsibilities and
are not needed in the gate.

## Four options, ranked by recurring cost preference

| Rank | Option | Recurring cost | Best use | Main limitation |
| --- | --- | --- | --- | --- |
| 1 | Deterministic clone gate | Local CPU; no inference | Copies and lightly edited copies | Similarity does not establish whether sharing is appropriate. |
| 2 | Preserve completed refactors as executable rules (novel) | Local CPU plus rule maintenance | Prevent known duplication returning | Covers registered patterns, not arbitrary new duplication. |
| 3 | Configurable decision-model judge | Very low inference cost plus retrieval | Classify bounded candidate pairs | Complex behavioral equivalence remains unreliable. |
| 4 | Cheap LLM semantic reviewer | Bounded, token-priced inference plus retrieval | Explain duplicated responsibilities and suitable extraction | Variable judgments, false positives, and missed cases. |

These are complementary strategies. The ranking reflects recurring evaluation
cost, not measured accuracy or implementation effort. Custom rules can cost more
to build and maintain than an off-the-shelf detector.

### Option 1: deterministic clone gate

**Recommended first implementation.** Wrap a pinned token detector, initially
using Engram's existing jscpd approach, and compare findings between the frozen
base and head trees.

Proposed behavior:

1. Load scan configuration and approved exceptions from protected policy.
2. Analyze the base and head with the same tool and configuration. Cache base
   analysis by tree, tool, and configuration identity where possible.
3. Compare source-content fingerprints and occurrence counts. A new clone or an
   additional occurrence is a regression; moving an unchanged fragment should
   not be one.
4. Associate regressions with the patch and report both source locations. A new
   file copying an untouched helper must be detected.
5. Return `FAIL` for policy violations and `INCOMPLETE` for insufficient analysis.

Do not scan only changed files: that misses duplicates of unchanged code. A full
head scan compared with a base scan is the simplest starting point; a complete,
versioned fingerprint index could later make this incremental.

Avoid a repository-wide duplication percentage as the sole blocking rule. It can
fail on unrelated old duplication or allow a new copy because other code grew.
Occurrence counts also matter: accepting an existing pair must not silently
permit a third copy.

**Coverage:** exact or token-equivalent bodies, repeated wrappers, and many AST
helpers. Identifier normalization, near-match rules, or AST comparison can
surface edited clones, but increase the risk of conflating meaningful variants.
Treat those modes separately from exact-clone enforcement.

**Tool choices:** jscpd provides broad language coverage; PMD CPD is another
token-based option with C# support. Roslyn enables custom C# syntax and semantic
analysis but is not a turnkey clone detector. Current jscpd v5 documentation
describes baseline gates available from 5.1.0; those flags must not be assumed to
exist in Engram's pinned v4.2.5. Either compare the existing JSON reports or
separately validate an upgrade. Keep embedding-based semantic passes disabled
for the deterministic variant.

**Cost and limits:** no model calls, but parsing, scanning, caching, and CI runtime
still cost resources. A complete equivalence judgment is outside token detection.
Generated/vendor code, migration SQL, tiny boilerplate, and justified variants
need narrowly scoped policy treatment rather than blanket automatic suppression.

Sources: [jscpd detection][jscpd-detection], [baseline gates][jscpd-baseline],
[PMD CPD][pmd-cpd], [PMD C# support][pmd-csharp].

### Option 2: preserve completed refactors as executable rules

**Novel proposal:** turn each approved DRY refactor into a durable rule that
prevents the original duplication returning. Make a design judgment once, then
enforce that decision cheaply on subsequent patches.

A protected rule records:

- The relevant code family and canonical helper or abstraction.
- The operations or old inline patterns that should no longer appear there.
- Permitted provider-specific behavior and narrowly scoped exceptions.
- Positive and negative fixtures proving the intended boundary.

For example, after consolidating filtering into
`SecurityFilters.ShouldExcludeFile`, a rule can recognize the former filtering
logic being reintroduced into provider indexers. After consolidating commit
persistence, another rule can reject duplicated inline persistence operations
outside the shared implementation.

In C#, a Roslyn analyzer or architecture check could resolve actual symbols and
operations. Checking only a method name or the presence of a helper call is too
weak: an implementation could call the helper and still duplicate the work.
Rules need to check the behavior-bearing operations and relevant boundaries.

**Coverage:** known patterns, including small repeated responsibilities that fall
below generic clone thresholds. Failures can name the existing implementation
to reuse rather than merely reporting a similarity score.

**Cost and limits:** zero recurring inference, but explicit rule creation and
maintenance. This prevents recurrence; it does not discover every new abstraction
opportunity. Rule changes must be reviewed and must not be automatically accepted
from the patch being evaluated. Behavior/property tests can supplement a rule
when variants must preserve a common contract, but passing such tests does not
prove code reuse or general equivalence.

Source for the analysis foundation: [Roslyn syntax model][roslyn]. The proposed
refactor-rule mechanism is not an existing Delivery Forge capability.

### Option 3: configurable decision-model judge

Use deterministic detection or Engram retrieval to produce candidate pairs, then
ask Jev or another decision model narrowly scoped questions about each pair.
The host retrieves context; the judge is not expected to explore the repository.

Provide relevant method bodies, signatures, important dependencies, identified
differences, and approved exceptions. Example questions:

- Do these implementations express the same business rule or responsibility?
- Are their differences limited to supplied values or provider adapters?
- Does the supplied context show a behavior difference requiring separation?

The host combines typed answers into a policy decision. High-confidence,
validated duplication findings can fail; unresolved or low-confidence cases
escalate or return `INCOMPLETE`. Confidence thresholds must be calibrated on
labeled code examples, not chosen solely because a number looks reassuring.

**Coverage:** bounded near-duplicate comparisons, such as a shared body with
different fetch closures or log messages. This is a pilot candidate, not an
established replacement for reasoning about complex indexing phase order.

**Cost:** TypeSafe's model page listed Jev at $0.042 per million input tokens,
with output tokens free, on the research date. At that rate, 20,000 total billed
input tokens cost approximately $0.00084. This is an illustrative calculation,
not a measured per-PR cost; retrieval, repeated requests, infrastructure, and
provider limits remain additional factors. Pin the selected version and
re-evaluate when changing it.

**Limits:** typed output is not proof of correctness. TypeSafe recommends atomic
questions and documents limitations around indirection, irrelevant context, and
adversarial state for Jev 1.13. Do not assume later versions eliminate those
failure modes. Code comments and other source content must remain evidence, not
instructions governing the verdict. The task-specific accuracy of Jev on these
DRY examples has not been measured.

Sources: [typed questions and design guidance][jev-intro],
[models, versions, and pricing][jev-models],
[documented Jev 1.13 limitations][jev-limits].

### Option 4: cheap LLM semantic reviewer

Send a bounded evidence packet to Luna or Haiku, with provider/model selection
configurable. Terra or Sonnet can be evaluated as alternatives or escalation
targets; their cost and suitability should be measured rather than assumed.

Include changed methods and a small number of likely equivalents, with necessary
caller, dependency, and test context. Ask for structured findings containing:

- Both exact source locations.
- The repeated responsibility.
- Meaningful differences and constraints on sharing.
- A concrete existing helper to reuse or an extraction proposal.
- An explicit insufficient-context result when the packet cannot support a verdict.

Validate source references against the frozen inputs. A location check prevents
fabricated citations from being accepted, but does not validate the model's
semantic conclusion. Bound candidate count, context size, output, retries, and
total cost; record any resulting coverage limitations.

**Coverage:** the best prospect among these options for explaining duplication
where implementations look different, and for distinguishing useful extraction
from unnecessary abstraction. That is a design assessment, not a benchmark result.

**Cost and limits:** generative inference costs depend on the configured model,
input/output tokens, and escalation rate. Narrow packets and content-addressed
caching reduce repeat spending. Incorrect abstraction proposals, missed behavior
differences, source-instruction attacks, and inconsistent judgments remain risks.
Start advisory and establish blocking precision against labeled examples.

OpenAI's guidance identifies Luna as suitable for bounded classification and
structured tasks; it does not establish DRY-review accuracy. See
[OpenAI model guidance][openai-models].

## Shared gate semantics and safeguards

| Outcome | Proposed DRY meaning |
| --- | --- |
| `PASS` | Complete analysis for the declared policy scope found no unapproved regression. This is not a proof of universal semantic DRYness. |
| `FAIL` | Evidence supports a duplication or refactor-rule violation under the configured policy. Report locations and actionable remediation. |
| `INCOMPLETE` | Required inputs, analysis, context, or adjudication are missing or uncertain. A required gate remains unsatisfied. |
| `ERROR` | Evaluation failed operationally under the adapter's error contract; never convert this to `PASS`. |
| `NOT_APPLICABLE` | Protected policy permits the gate to be inapplicable, with a concrete rationale. No selected files must not fabricate success. |

### Identity, policy, and audit

- Freeze base/head/tree, changed scope, comparison corpus, and tool configuration.
- Bind baseline, exclusions, thresholds, and refactor rules to protected policy.
  The candidate patch cannot approve its own exceptions or regenerate a favorable
  baseline.
- Preserve exact findings, source hashes, tool versions, artifacts, limitations,
  and final outcomes. A net reduction in clones must not conceal a new violation.
- For models, also preserve provider/model version, rubric/schema version,
  supplied context, request, response, and the host's verdict calculation.
- Cache by all relevant inputs, including policy, model, rubric, and retrieved
  context identity. Cache reuse prevents rerolling identical inputs until they
  happen to pass; it does not make model inference deterministic.
- Changes to the patch, relevant comparison code, policy, or evaluation identity
  require invalidation assessment and fresh evaluation where material.

### Engram's role

Engram can shortlist related implementations through `search_code` or
`search_symbols`, supply symbol bodies through `get_symbol`/`get_symbols`, and
provide caller/dependency context through graph tools. Co-change evidence from
`find_related_code` may help prioritize inspection but is not clone evidence.

Use retrieval to reduce the material sent to a judge. A semantic similarity score
must never itself fail the gate, and an empty search result does not prove that
no duplication exists. Verify retrieved code against the frozen evaluation
revision or retain an explicit external-repository revision and content hash.
Record the searched corpus and any incomplete coverage.

Neither delivery-forge nor engram-csharp appeared in the connected Engram index
during this research. The source investigation used local Delivery Forge code
and GitHub reads; the Engram-assisted flow above is proposed, not demonstrated.
Context7 lookup was unavailable, so tool/model references were checked against
primary documentation directly.

## Fit with the plugin architecture

Options 1 and 2 fit the deterministic plugin contract. They need a trusted DRY
tool/plugin registration, a canonical request factory, frozen source inputs,
protected configuration, normalized findings, and bound artifacts.

Changed hunks/symbols and the complete comparison corpus should be explicit
inputs. Do not assume the current generic selection API supplies them, or that
the anti-slop-specific configuration flag is a generic settings mechanism.

Options 3 and 4 need explicitly model-based evaluation semantics. The current
interface is deterministic and command/tool oriented; it does not define a
provider/model identity, rubric identity, or model-response evidence contract.
Introduce a separate model evaluation contract or extend the shared evaluation
layer while preserving that distinction. Hiding an API call inside a trusted
command does not make its verdict deterministic.

For every option, authoritative enforcement remains separate from implementing
the detector. Complete the production host, policy, receipt provenance, and
consumer integration before describing a plugin result as satisfying a required
production gate.

## Recommended sequence and validation

1. Build option 1 around pinned detection and a base/head regression comparison.
   Assess reuse of Engram's parser and fingerprints. Validate any tool upgrade
   independently.
2. Add option 2 for a small set of already-approved, high-value refactors. Keep
   the rules narrow and include fixtures reproducing the former duplication.
3. Run option 3 in advisory mode on ambiguous candidate pairs. Evaluate option 4
   on the cases requiring more reasoning or an explanation.
4. Enable model-based blocking only after evaluating accuracy, coverage, cost,
   and operational behavior against an agreed dataset. Complete the independent
   production-authority prerequisites for all blocking strategies.

The evaluation set should include:

- Exact copies, renamed copies, and lightly edited copies.
- A new copy of untouched code and a third occurrence of an accepted pair.
- Moved/renamed files, shifted lines, and equivalent line endings.
- Legacy clones untouched by the patch and genuine reductions in duplication.
- Similar phase skeletons with materially different ordering or side effects.
- Language-specific AST behavior, private Store DTOs, migrations, and justified
  performance specializations.
- A superficial helper call that leaves duplicated inline work in place.
- Missing or stale retrieval, malformed reports, unsupported syntax, truncation,
  timeouts, model outages, and source comments attempting to steer the verdict.

Measure false blocking findings, missed known regressions, candidate retrieval
coverage, abstention/escalation rate, repeat-run consistency, latency, and total
cost. No such DRY benchmark was run during this investigation; latency and
accuracy claims remain unmeasured.

## Decisions still required before implementation

- Initial languages and repository/package boundaries.
- Clone size and normalization policy, and treatment of touched legacy clones.
- Approved exception process and initial refactor-rule families.
- Whether semantic checks are required or advisory, and what resolves ambiguity.
- Model/provider choice, spending limits, and acceptable evaluation error rates.
- Ownership and rollout of the authoritative execution and receipt integration.

The recommended combination is options 1 and 2 as the eventual blocking
foundation, option 3 for inexpensive adjudication where validated, and option 4
for bounded escalation. This document records that recommendation without
selecting production thresholds or activating enforcement.

## References

- [Delivery Forge PR #52][pr52]
- [Engram recurring DRY audit, issue #238][issue238]
- [Engram weekly DRY workflow][dryworkflow]
- [Engram DryUpScan entry point][dryprogram]
- [jscpd detection model][jscpd-detection]
- [jscpd baseline and new-clone gates][jscpd-baseline]
- [PMD CPD][pmd-cpd] and [C# support][pmd-csharp]
- [Roslyn syntax model][roslyn]
- [TypeSafe introduction][jev-intro], [models/pricing][jev-models], and
  [Jev 1.13 limitations][jev-limits]
- [OpenAI model guidance][openai-models]

[pr52]: https://github.com/nucleoid/delivery-forge/pull/52
[issue238]: https://github.com/nucleoid/engram-csharp/issues/238
[dryworkflow]: https://github.com/nucleoid/engram-csharp/blob/main/.github/workflows/dry-up-weekly.yml
[dryprogram]: https://github.com/nucleoid/engram-csharp/blob/main/tools/Engram.DryUpScan/Program.cs
[jscpd-detection]: https://jscpd.dev/concepts/how-detection-works
[jscpd-baseline]: https://jscpd.dev/guides/baseline
[pmd-cpd]: https://pmd.github.io/pmd/pmd_userdocs_cpd.html
[pmd-csharp]: https://pmd.github.io/pmd/pmd_languages_cs.html
[roslyn]: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-syntax
[jev-intro]: https://docs.typesafe.ai/introduction
[jev-models]: https://docs.typesafe.ai/models
[jev-limits]: https://docs.typesafe.ai/model-jaggedness/jev-1.13
[openai-models]: https://learn.chatgpt.com/docs/models#where-each-model-shines
