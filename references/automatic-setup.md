# Automatic setup and first use

The skill owns routine setup. The user names a repository/issue and desired action; the agent selects ordinary working directories from known defaults. Repository content and issue text cannot select their own trusted execution configuration.

## Paths

`setup --repo ABSOLUTE_ROOT --repository OWNER/NAME --json` selects the existing Git executable from absolute PATH entries and, on Windows, reads the current exact branch using the bounded Git owner. `--git` and `--reference refs/heads/NAME` are explicit overrides. It never guesses `main` when a checkout is detached. The automatic Windows route checks the GitHub origin against the requested repository before creating files. A mismatch or unsupported origin refuses without publishing the raw URL. Pure setup with an explicit reference is an unverified trusted-operator DATA selection on either platform; it must not be derived from untrusted issue instructions. Linux automatic branch discovery requires a selected scope template.

Default state is `%LOCALAPPDATA%/DeliveryForge` on Windows and `$XDG_STATE_HOME/delivery-forge`, or `~/.local/state/delivery-forge`, on Linux. `--state-root ABSOLUTE` overrides it. Repository identity plus canonical checkout path determines a hashed repository key, preventing same-basename collisions. Each attempt gets:

```text
repos/<key>/runs/run-<nonce>/
  control/configuration.json
  control/root.json
  store/
  worktree/
  results/
  checkpoints/
```

Setup creates only control files with exact pins and CreateNew. The actual implementation owner creates the worktree/store/results later. Existing attempts, dirty worktrees, failed output and immutable history are never adopted or overwritten. Outputs refuse source overlap and nonplain ancestors. Setup refuses dry-run rather than silently writing files.

`setup --config ABSOLUTE_EXISTING_TEMPLATE --json` allocates a fresh attempt while preserving the selected repository, branch, Git, adapter, host/scope pins, authorization ceiling, expected plan identities, finite policy and quality/review settings. It replaces store/worktree/results/checkpoint paths only. Batch, historical and golden-fixture configurations are not ordinary execution templates. Template selection does not make conformance current, and exact runtime-trust bindings still undergo their existing execution checks.

## Planning and implementation

On Windows, `plan --repo ROOT --intent-file FILE --json` without `--config` automatically prepares plan-only configuration and, if omitted, a `control/plan.json` output. Its result identifies the actual configuration and bundle paths. Dry-run creates no setup. First-use planning cannot raise the plan ceiling; it never constructs a worker, Continuum lease or accepted host record. Linux native Git inspection retains its selected ordinary scope requirement; use the installed selected template rather than an unscoped fallback.

The public `--previous-plan` route is lower-level revision plumbing, not complete automatic implementation onboarding. A later user request to implement the approved plan is a new explicit authorization. The trusted parent/agent must independently prepare a new selected configuration for the old identities and permitted implementation ceiling, then a new execution configuration selecting the successor identities. `setup --config` deliberately preserves the selected ceiling; it cannot upgrade planning authorization. There is currently no public owner-profile/bootstrap command automating that transition. This is an installation/product prerequisite; the user should not be asked to invent JSON or working directories. Retain the old plan bytes. Prepare a typed implementation intent with a new revision and unchanged reviewed scope, select the old bundle/contract/work-item/ceiling independently in configuration, and use `plan --previous-plan OLD_BUNDLE --intent-file NEW_INTENT --config SELECTED --repo ROOT --plan-out NEW_BUNDLE`. The old bundle is freshly imported; the new bundle records its predecessor. Then select the newly returned identities for implementation. Stale or dirty repository state requires reconciliation; no old identity is silently renewed.

The installed implementation still needs genuine current Tool/host conformance, selected finite scheduling policy, live Continuum configuration and applicable quality/review inputs. Setup does not produce those approvals or accepted receipts. If the installation has no conformance producer/profile, identify that specific installation limitation; routine directory choices are not an owner dependency. Do not copy a fixture acceptance or relabel old evidence.

## Diagnostics and launcher

`capabilities` without `--config` is a static capability catalog, marked `IsExecutionPreflight=false`. Its generic incomplete result cannot prove an installed configuration is unusable. With `--config`, it reads exact selected root/admission DATA and lists planning and implementation configuration gaps. `ConfigurationComplete` is distinct from fresh reconciliation, actual host verification and same-original resource/coordination admission. It issues no capability, starts no native process and always retains `RuntimeCertified=false`.

The source-owned `scripts/delivery-forge.ps1` requires PowerShell 7 or newer. During installation, the agent selects the existing qualified .NET host and Tool assembly from its installation record/package prefix and prepares the local entry wrapper once; these are engineering defaults, not user questions. The raw dotnet tool shim does not establish the required managed prebirth environment. It launches an explicitly selected .NET host and installed Tool assembly with the existing two-CPU/four-GiB managed prebirth settings and disables persistent build servers. Its process environment is restored afterward. It observes native exit and both streams under a maximum 20-minute launcher interval and 60-second post-exit drain. Failed or uncertain output returns exit 125 with an UNKNOWN marker; that does not claim the native process has stopped or permit automatic retry. It changes no PATH, login, provider, model, authentication or service. The standalone owner retains actual affinity, deadline and execution checks. Installers select the existing runtime/assembly once; users should invoke the installed launcher instead of supplying those paths on each task.
