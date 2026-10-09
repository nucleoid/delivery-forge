using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Planning;

public static class PlanFreezer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static FrozenPlan Freeze(
        PlanDraft draft,
        string revision,
        DateTimeOffset createdAt,
        FrozenPlan? predecessor = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            return FreezeCore(draft, revision, createdAt, predecessor);
        }
        catch (PlanningException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ContractJsonException or ContractValidationException or JsonException or InvalidOperationException)
        {
            throw new PlanningException($"Plan could not be frozen as a validated portable contract: {exception.Message}");
        }
    }

    private static FrozenPlan FreezeCore(PlanDraft draft, string revision, DateTimeOffset createdAt, FrozenPlan? predecessor)
    {
        if (predecessor is not null &&
            (!string.Equals(predecessor.Repository, draft.Request.Repository, StringComparison.Ordinal) ||
             !string.Equals(predecessor.WorkItem, draft.Request.WorkItem, StringComparison.Ordinal)))
        {
            throw new PlanningException("A predecessor must share the same repository and work-item lineage.");
        }
        var failures = Validate(draft, revision, createdAt);
        if (failures.Count > 0)
        {
            throw new PlanningException($"Plan is not ready: {string.Join("; ", failures)}");
        }

        var request = draft.Request;
        var included = Sorted(request.Included);
        var excluded = Sorted(request.Excluded);
        var criteria = Sorted(request.AcceptanceCriteria);
        var limitations = draft.Repository.Limitations
            .Concat(draft.Intake.Limitations)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var material = new
        {
            repository = request.Repository,
            workItem = request.WorkItem,
            mode = request.Mode,
            intakeDepth = request.Depth.ToString().ToLowerInvariant(),
            intent = request.Outcome,
            requestedCeiling = request.RequestedCeiling,
            baseCommit = draft.Repository.Commit,
            baseTree = draft.Repository.Tree,
            scope = new { included, excluded },
            acceptanceCriteria = criteria,
            repositoryObservations = new
            {
                requestedRef = draft.Repository.RequestedRef,
                draft.Repository.ExactBranchReferenceVerified,
                draft.Repository.HeadCommit,
                draft.Repository.HeadTree,
                draft.Repository.DetachedHead,
                draft.Repository.Dirty,
                draft.Repository.Shallow,
                submodules = Sorted(draft.Repository.Submodules)
            },
            intake = new
            {
                draft.Intake.Ready,
                depth = draft.Intake.Depth.ToString().ToLowerInvariant(),
                importedContextRequirement = draft.Intake.ImportedContextRequirement.ToString().ToLowerInvariant(),
                draft.Intake.ImportedContextAvailable,
                limitations = Sorted(draft.Intake.Limitations),
                advisoryCaveats = draft.Intake.AdvisoryCaveats
                    .Distinct()
                    .Order()
                    .Select(code => new { code = CaveatWireValue(code), message = IntakePlanner.CaveatText(code) })
                    .ToArray(),
                advisoryMeanings = draft.Intake.AdvisoryMeanings
                    .Distinct()
                    .Order()
                    .Select(code => new { code = MeaningWireValue(code), message = IntakePlanner.MeaningText(code) })
                    .ToArray(),
                advisoryNotices = draft.Intake.AdvisoryNotices
                    .Distinct()
                    .Order()
                    .Select(code => new { code = NoticeWireValue(code), message = IntakePlanner.NoticeText(code) })
                    .ToArray()
            },
            provenance = draft.Provenance
                .Where(item => item.ProducerKind != EvidenceProducerKind.PrivateAdvisory)
                .OrderBy(item => item.SourceKind)
                .ThenBy(item => item.Locator, StringComparer.Ordinal)
                .Select(item => new
                {
                    sourceKind = item.SourceKind.ToString().ToLowerInvariant(),
                    producerKind = ProducerWireValue(item.ProducerKind),
                    locatorKind = item.LocatorKind.ToString().ToLowerInvariant(),
                    locator = item.Locator,
                    item.Digest,
                    observedAt = FormatUtc(item.ObservedAt),
                    caveats = item.Caveats.Order().Select(IntakePlanner.CaveatText).ToArray(),
                    item.Supersedes,
                    item.IsComplete,
                    requirement = item.Requirement.ToString().ToLowerInvariant(),
                    repositorySafety = item.SourceKind == EvidenceSourceKind.Repository
                        ? new
                        {
                            item.RepositoryCommit,
                            item.RepositoryTree,
                            item.RepositoryIsSymlink,
                            symlinkResolution = item.RepositorySymlinkResolution?.ToString().ToLowerInvariant(),
                            item.RepositoryGenerationClassification
                        }
                        : null
                }).ToArray(),
            changeMap = draft.ChangeMap
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Symbol, StringComparer.Ordinal)
                .Select(item => new { item.Path, item.Symbol, item.Effect }).ToArray(),
            dependencyDag = draft.Dependencies
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new { item.Id, dependsOn = Sorted(item.DependsOn), item.IntegrationCondition }).ToArray(),
            gates = draft.Gates
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.Id,
                    command = new
                    {
                        item.Command.Executable,
                        arguments = item.Command.Arguments.Select(argument => new
                        {
                            kind = ArgumentWireValue(argument.Kind),
                            argument.Value
                        }).ToArray()
                    },
                    displayCommand = item.Command.Render(),
                    item.ExpectedOutcome
                }).ToArray(),
            rollout = new
            {
                draft.Rollout.Compatibility,
                draft.Rollout.Configuration,
                draft.Rollout.Secrets,
                draft.Rollout.Migration,
                draft.Rollout.Reauthentication,
                draft.Rollout.Backfill,
                draft.Rollout.Observability,
                draft.Rollout.Rollback,
                operatorActions = Sorted(draft.Rollout.OperatorActions)
            },
            unknowns = draft.Unknowns
                .OrderBy(item => item.Description, StringComparer.Ordinal)
                .ThenBy(item => item.Owner, StringComparer.Ordinal)
                .Select(item => new { item.Description, item.Owner, item.BlocksReadiness }).ToArray(),
            limitations
        };

        var materialBytes = JsonSerializer.SerializeToUtf8Bytes(material, JsonOptions);
        var identityMaterial = JsonNode.Parse(materialBytes)?.AsObject()
            ?? throw new PlanningException("Plan material could not be projected for identity.");
        identityMaterial.Remove("repositoryObservations");
        identityMaterial["limitations"] = JsonSerializer.SerializeToNode(Sorted(draft.Intake.Limitations), JsonOptions);
        var contentDigest = CanonicalJson.ComputeIdentity(JsonSerializer.SerializeToUtf8Bytes(identityMaterial, JsonOptions));
        if (predecessor is not null &&
            string.Equals(predecessor.Revision, revision, StringComparison.Ordinal) &&
            !string.Equals(predecessor.ContentDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new PlanningException("A declared revision cannot be reused for materially different plan content.");
        }

        var planRevision = $"{revision}@{contentDigest}";
        var created = FormatUtc(createdAt);
        var contract = BuildAndValidateContract(request, planRevision, draft.Repository.Commit, created, included, excluded, criteria);
        var supersedes = predecessor is not null && !string.Equals(predecessor.ContractIdentity, contract.Identity, StringComparison.Ordinal)
            ? predecessor.ContractIdentity
            : predecessor?.Supersedes;
        var portable = new
        {
            schemaVersion = "1.0.0",
            kind = "planning-bundle",
            identity = "sha256:" + new string('0', 64),
            contractIdentity = contract.Identity,
            declaredRevision = revision,
            planRevision,
            contentDigest,
            supersedes,
            createdAt = created,
            plan = material
        };

        var provisional = JsonSerializer.SerializeToUtf8Bytes(portable, JsonOptions);
        var identity = CanonicalJson.ComputeIdentity(provisional);
        using var document = JsonDocument.Parse(provisional);
        var finalBytes = ReplaceIdentity(document.RootElement, identity);
        var canonical = CanonicalJson.Canonicalize(finalBytes);
        if (!string.Equals(identity, CanonicalJson.ComputeIdentity(canonical), StringComparison.Ordinal))
        {
            throw new PlanningException("Frozen plan identity failed its post-serialization check.");
        }

        return new FrozenPlan(
            identity,
            contract.Identity,
            revision,
            planRevision,
            contentDigest,
            supersedes,
            request.Repository,
            request.WorkItem,
            draft.Repository.RequestedRef,
            draft.Repository.ExactBranchReferenceVerified,
            draft.Repository.DetachedHead,
            draft.Repository.Commit,
            draft.Repository.Tree,
            canonical,
            contract.CanonicalBytes.ToArray(),
            downstreamReady: true,
            limitations);
    }

    private static ValidatedContract BuildAndValidateContract(
        PlanningRequest request,
        string planRevision,
        string baseCommit,
        string createdAt,
        string[] included,
        string[] excluded,
        string[] criteria)
    {
        object Contract(string identity) => new
        {
            schemaVersion = "1.0.0",
            kind = "plan",
            identity,
            repository = request.Repository,
            workItem = request.WorkItem,
            mode = request.Mode,
            planRevision,
            baseCommit,
            createdAt,
            scope = new { outcome = request.Outcome, included, excluded },
            acceptanceCriteria = criteria
        };

        var provisional = JsonSerializer.SerializeToUtf8Bytes(Contract("sha256:" + new string('0', 64)), JsonOptions);
        var identity = CanonicalJson.ComputeIdentity(provisional);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Contract(identity), JsonOptions);
        return ContractValidator.ParseAndValidate(bytes);
    }

    private static byte[] ReplaceIdentity(JsonElement root, string identity)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("identity")) writer.WriteString("identity", identity);
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static List<string> Validate(PlanDraft draft, string revision, DateTimeOffset createdAt)
    {
        var failures = new List<string>();
        var request = draft.Request;
        Required(request.Repository, "repository", failures);
        Required(request.WorkItem, "work item", failures);
        Required(request.Mode, "mode", failures);
        Required(request.Outcome, "intent", failures);
        Required(request.RequestedCeiling, "requested ceiling", failures);
        Required(revision, "revision", failures);
        if (createdAt.Offset != TimeSpan.Zero) failures.Add("createdAt must be UTC");
        if (request.Mode is not ("plan" or "implement" or "pr" or "merge" or "resume")) failures.Add("mode is not recognized");
        if (request.Included.Count == 0) failures.Add("scope.included is required");
        ValidateRequiredItems(request.Included, "scope.included", failures);
        ValidateRequiredItems(request.Excluded, "scope.excluded", failures);
        if (request.AcceptanceCriteria.Count == 0) failures.Add("acceptance criteria are required");
        ValidateRequiredItems(request.AcceptanceCriteria, "acceptance criteria", failures);
        if (!string.IsNullOrWhiteSpace(request.UserOwnedDecision)) failures.Add("user-owned decision remains unresolved");
        if (string.IsNullOrWhiteSpace(request.UserOwnedDecision) != string.IsNullOrWhiteSpace(request.RecommendedOption))
            failures.Add("a user-owned decision and concrete recommended option must be supplied together");
        if (!draft.Intake.Ready) failures.Add("intake readiness evidence is not ready");
        if (draft.Intake.Depth != request.Depth) failures.Add("intake depth does not match the planning request");
        if (!IntakePlanner.IsBoundTo(draft.Intake, request, draft.Provenance))
            failures.Add("intake assessment is not bound to this planning request, evidence, and imported-context caveats");
        if (draft.Intake.ImportedContextRequirement == EvidenceRequirement.Required && !draft.Intake.ImportedContextAvailable)
            failures.Add("required imported context is unavailable");
        if (!draft.Provenance.Any(item => item.ProducerKind != EvidenceProducerKind.PrivateAdvisory))
            failures.Add("portable public provenance is required");
        if (draft.Provenance.Any(item => item.Requirement == EvidenceRequirement.Required && !item.IsComplete))
            failures.Add("required provenance is incomplete");
        if (draft.Provenance.Any(item => !item.HasConsistentSourceLocator()))
            failures.Add("provenance source kind and locator scheme must be consistent");
        if (draft.Provenance.Any(item =>
                item.ProducerKind != EvidenceProducerKind.PrivateAdvisory &&
                item.SourceKind == EvidenceSourceKind.Repository &&
                !item.IsReaderBoundRepositoryEvidence()))
            failures.Add("repository provenance is not bound to exact reader-issued file/commit/tree and safety metadata");
        if (draft.Provenance.Any(item =>
                item.ProducerKind != EvidenceProducerKind.PrivateAdvisory &&
                item.SourceKind == EvidenceSourceKind.Repository &&
                (!string.Equals(item.RepositoryCommit, draft.Repository.Commit, StringComparison.Ordinal) ||
                 !string.Equals(item.RepositoryTree, draft.Repository.Tree, StringComparison.Ordinal))))
            failures.Add("repository provenance commit/tree differs from the draft base commit/tree");
        if (draft.Intake.VerifiedRepositoryIdentities.Any(identity =>
                !string.Equals(identity.Commit, draft.Repository.Commit, StringComparison.Ordinal) ||
                !string.Equals(identity.Tree, draft.Repository.Tree, StringComparison.Ordinal)))
            failures.Add("verified imported context repository identity differs from the draft base commit/tree");
        if (draft.ChangeMap.Count == 0) failures.Add("change map is required");
        if (draft.Dependencies.Count == 0) failures.Add("dependency DAG is required (use an explicit root node when empty)");
        if (draft.Gates.Count == 0) failures.Add("gates/tests are required");
        if (draft.Unknowns.Any(item => item.BlocksReadiness)) failures.Add("blocking unknown remains unresolved");
        ValidateDag(draft.Dependencies, failures);
        ValidateUnique(draft.Provenance, item => $"{item.SourceKind}\0{item.Locator}", "provenance source/locator keys", failures);
        ValidateUnique(draft.ChangeMap, item => $"{item.Path}\0{item.Symbol}", "change-map path/symbol keys", failures);
        ValidateUnique(draft.Gates, item => item.Id, "gate IDs", failures);
        ValidateUnique(draft.Unknowns, item => item.Description, "unknown descriptions", failures);
        ValidateGitObject(draft.Repository.Commit, "base commit", failures);
        ValidateGitObject(draft.Repository.Tree, "base tree", failures);
        if (!draft.Repository.IsReaderIssued()) failures.Add("repository context was not issued by the exact Git reader or its bound observations changed");
        if (request.RequestedCeiling is not ("plan" or "implement" or "pr" or "merge"))
            failures.Add("requested ceiling is not recognized");
        foreach (var evidence in draft.Provenance)
        {
            Required(evidence.Locator, "provenance locator", failures);
            if (evidence.ObservedAt.Offset != TimeSpan.Zero) failures.Add($"provenance at '{evidence.Locator}' must use a UTC observed time");
            if (evidence.Digest is not null && !IsSha256(evidence.Digest))
                failures.Add($"provenance digest at '{evidence.Locator}' must be lowercase sha256");
        }
        foreach (var target in draft.ChangeMap)
        {
            Required(target.Path, "change-map path", failures);
            Required(target.Symbol, "change-map symbol", failures);
            Required(target.Effect, "change-map effect", failures);
        }
        foreach (var dependency in draft.Dependencies)
        {
            Required(dependency.Id, "dependency ID", failures);
            Required(dependency.IntegrationCondition, "dependency integration condition", failures);
            if (dependency.DependsOn.Count != dependency.DependsOn.Distinct(StringComparer.Ordinal).Count())
                failures.Add($"dependency DAG node '{dependency.Id}' contains duplicate dependencies");
        }
        foreach (var gate in draft.Gates)
        {
            Required(gate.Id, "gate ID", failures);
            Required(gate.ExpectedOutcome, "gate expected outcome", failures);
            try { CommandRenderer.Validate(gate.Command); }
            catch (PlanningException exception) { failures.Add(exception.Message); }
        }
        Required(draft.Rollout.Compatibility, "rollout compatibility", failures);
        Required(draft.Rollout.Configuration, "rollout configuration", failures);
        Required(draft.Rollout.Secrets, "rollout secrets", failures);
        Required(draft.Rollout.Migration, "rollout migration", failures);
        Required(draft.Rollout.Reauthentication, "rollout reauthentication", failures);
        Required(draft.Rollout.Backfill, "rollout backfill", failures);
        Required(draft.Rollout.Observability, "rollout observability", failures);
        Required(draft.Rollout.Rollback, "rollout rollback", failures);
        foreach (var unknown in draft.Unknowns)
        {
            Required(unknown.Description, "unknown description", failures);
            Required(unknown.Owner, "unknown owner", failures);
        }
        ValidatePortablePlan(draft, revision, failures);
        return failures;
    }

    private static void ValidateDag(IReadOnlyList<DependencyNode> nodes, List<string> failures)
    {
        var byId = nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (byId.Count != nodes.Count) failures.Add("dependency DAG IDs must be unique");
        foreach (var node in nodes)
        {
            foreach (var dependency in node.DependsOn)
            {
                if (!byId.ContainsKey(dependency)) failures.Add($"dependency DAG node '{node.Id}' references missing '{dependency}'");
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (!visiting.Add(id)) return false;
            if (visited.Contains(id)) { visiting.Remove(id); return true; }
            foreach (var dependency in byId[id].DependsOn.Where(byId.ContainsKey))
            {
                if (!Visit(dependency)) return false;
            }
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        if (byId.Keys.Any(id => !visited.Contains(id) && !Visit(id))) failures.Add("dependency DAG contains a cycle");
    }

    private static void ValidatePortablePlan(PlanDraft draft, string revision, List<string> failures)
    {
        foreach (var target in draft.ChangeMap)
        {
            var normalized = target.Path.Replace('\\', '/');
            if (target.Path.Contains('\\') || PortableMaterial.IsAbsolutePath(target.Path) ||
                normalized.Split('/').Any(segment => segment == ".."))
                failures.Add($"change-map path '{target.Path}' is not repository-relative");
        }

        var portableText = new[]
        {
            revision, draft.Request.Repository, draft.Request.WorkItem, draft.Request.Mode, draft.Request.Outcome,
            draft.Request.RequestedCeiling, draft.Request.UserOwnedDecision ?? string.Empty,
            draft.Request.RecommendedOption ?? string.Empty,
            draft.Repository.RequestedRef,
            draft.Rollout.Compatibility, draft.Rollout.Configuration, draft.Rollout.Secrets,
            draft.Rollout.Migration, draft.Rollout.Reauthentication, draft.Rollout.Backfill,
            draft.Rollout.Observability, draft.Rollout.Rollback
        }.Concat(draft.Request.Included)
         .Concat(draft.Request.Excluded)
         .Concat(draft.Request.AcceptanceCriteria)
         .Concat(draft.Repository.Submodules)
         .Concat(draft.Repository.Limitations)
         .Concat(draft.Intake.Limitations)
         .Concat(draft.Provenance.Where(item => item.ProducerKind != EvidenceProducerKind.PrivateAdvisory)
             .SelectMany(item => new[] { item.Locator, item.Supersedes ?? string.Empty }))
         .Concat(draft.ChangeMap.SelectMany(item => new[] { item.Path, item.Symbol, item.Effect }))
         .Concat(draft.Dependencies.SelectMany(item => new[] { item.Id, item.IntegrationCondition }.Concat(item.DependsOn)))
         .Concat(draft.Gates.SelectMany(item => new[] { item.Id, item.Command.Executable, item.Command.Render(), item.ExpectedOutcome }))
         .Concat(draft.Rollout.OperatorActions)
         .Concat(draft.Unknowns.SelectMany(item => new[] { item.Description, item.Owner }));
        if (portableText.Any(PortableMaterial.ContainsPrivateMaterial))
            failures.Add("plan contains a host path or credential-like private material");
    }

    private static void ValidateUnique<T>(IReadOnlyList<T> values, Func<T, string> key, string name, List<string> failures)
    {
        if (values.Select(key).Distinct(StringComparer.Ordinal).Count() != values.Count)
            failures.Add($"{name} must be unique");
    }

    private static void ValidateRequiredItems(IEnumerable<string> values, string name, List<string> failures)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) failures.Add($"{name} cannot contain blank values");
    }

    private static bool IsSha256(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateGitObject(string value, string name, List<string> failures)
    {
        if ((value.Length is not 40 and not 64) || value.Any(character => character is < '0' or > '9' && character is < 'a' or > 'f'))
            failures.Add($"{name} must be a lowercase Git object ID");
    }

    private static void Required(string value, string name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)) failures.Add($"{name} is required");
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string[] Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string ProducerWireValue(EvidenceProducerKind value) => value switch
    {
        EvidenceProducerKind.ExplicitUserPublic => "explicit-user-public",
        EvidenceProducerKind.RepositoryAtBase => "repository-at-base",
        EvidenceProducerKind.DeterministicGenerated => "deterministic-generated",
        EvidenceProducerKind.PrivateAdvisory => "private-advisory",
        _ => throw new PlanningException("Evidence producer kind is not recognized.")
    };

    private static string CaveatWireValue(EvidenceCaveatCode value) => value switch
    {
        EvidenceCaveatCode.Stale => "stale",
        EvidenceCaveatCode.Truncated => "truncated",
        EvidenceCaveatCode.Heuristic => "heuristic",
        EvidenceCaveatCode.Incomplete => "incomplete",
        EvidenceCaveatCode.UnverifiedCheckout => "unverified-checkout",
        EvidenceCaveatCode.CheckoutConflict => "checkout-conflict",
        EvidenceCaveatCode.UnsafeSymlink => "unsafe-symlink",
        EvidenceCaveatCode.GeneratedFile => "generated-file",
        EvidenceCaveatCode.OptionalEvidenceUnavailable => "optional-evidence-unavailable",
        EvidenceCaveatCode.RequiredMeaningNotDistillable => "required-meaning-not-distillable",
        _ => throw new PlanningException("Evidence caveat code is not recognized.")
    };

    private static string MeaningWireValue(ImportedMeaningCode value) => value switch
    {
        ImportedMeaningCode.AdditionalRepositoryEvidence => "additional-repository-evidence",
        ImportedMeaningCode.RepositoryConflict => "repository-conflict",
        ImportedMeaningCode.PolicyConstraint => "policy-constraint",
        ImportedMeaningCode.CallerRelationship => "caller-relationship",
        _ => throw new PlanningException("Imported meaning code is not recognized.")
    };

    private static string NoticeWireValue(PlanningNoticeCode value) => value switch
    {
        PlanningNoticeCode.PrivateAdvisoryConflictReported => "private-advisory-conflict-reported",
        PlanningNoticeCode.PrivateAdvisoryLimitationReported => "private-advisory-limitation-reported",
        PlanningNoticeCode.OptionalPrivateMeaningNotExported => "optional-private-meaning-not-exported",
        _ => throw new PlanningException("Planning notice code is not recognized.")
    };

    private static string ArgumentWireValue(CommandArgumentKind value) => value switch
    {
        CommandArgumentKind.Literal => "literal",
        CommandArgumentKind.Placeholder => "placeholder",
        CommandArgumentKind.SecretReference => "secret-reference",
        _ => throw new PlanningException("Command argument kind is not recognized.")
    };
}
