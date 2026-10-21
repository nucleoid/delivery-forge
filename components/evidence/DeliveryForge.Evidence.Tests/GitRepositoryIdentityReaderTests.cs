namespace DeliveryForge.Evidence.Tests;

public sealed class GitRepositoryIdentityReaderTests
{
    [Fact]
    public async Task Frozen_materialization_is_reverified_by_expected_git_identity_after_tool_execution()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "app.ts");
        var original = System.Text.Encoding.UTF8.GetBytes("export const frozen = 1;\n");
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {original.Length}\0");
        var objectId = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
            header.Concat(original).ToArray()));
        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { ["app.ts"] = objectId };

        GitRepositoryIdentityReader.VerifyMaterializedInputs(
            temp.Path, expected, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "export const swapped = 2;\n", TestContext.Current.CancellationToken);

        Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.VerifyMaterializedInputs(
                temp.Path, expected, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("--skip-worktree")]
    [InlineData("--assume-unchanged")]
    public async Task Production_scope_ignores_status_hidden_worktree_bytes_and_materializes_commit(string flag)
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        var source = System.IO.Path.Combine(temp.Path, "app.ts");
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "export const head = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await Git(temp.Path, "update-index", flag, "app.ts");
        await File.WriteAllTextAsync(source, "export const hidden = 3;\n", TestContext.Current.CancellationToken);
        var reader = new GitRepositoryIdentityReader(new ProcessCommandExecutor(), temp.Path, baseCommit);

        var scope = await reader.ReadEvaluationScopeAsync(
            headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal("export const head = 2;\n", await File.ReadAllTextAsync(
                System.IO.Path.Combine(scope.MaterializedRoot!, "app.ts"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            GitRepositoryIdentityReader.DeleteMaterialization(scope.MaterializedRoot);
        }
    }

    [Fact]
    public async Task Production_scope_materializes_exact_frozen_objects_outside_the_repository()
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        var source = System.IO.Path.Combine(temp.Path, "src", "app.ts");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "src/app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "export const frozen = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        var reader = new GitRepositoryIdentityReader(new ProcessCommandExecutor(), temp.Path, baseCommit);

        var scope = await reader.ReadEvaluationScopeAsync(
            headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            Assert.NotNull(scope.MaterializedRoot);
            Assert.DoesNotContain(temp.Path, scope.MaterializedRoot, StringComparison.Ordinal);
            var frozen = System.IO.Path.Combine(scope.MaterializedRoot, "src", "app.ts");
            Assert.Equal("export const frozen = 2;\n", await File.ReadAllTextAsync(
                frozen, TestContext.Current.CancellationToken));
            Assert.True(File.GetAttributes(frozen).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            GitRepositoryIdentityReader.DeleteMaterialization(scope.MaterializedRoot);
        }
    }

    [Fact]
    public async Task Identity_reads_ignore_repository_replace_objects_and_unsafe_config()
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        var source = System.IO.Path.Combine(temp.Path, "app.ts");
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "export const head = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        var actualTree = (await Git(temp.Path, "--no-replace-objects", "rev-parse", "HEAD^{tree}")).Trim();
        await Git(temp.Path, "replace", headCommit, baseCommit);

        var identity = await new GitRepositoryIdentityReader(
            new ProcessCommandExecutor(), temp.Path, baseCommit)
            .ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(headCommit, identity.HeadCommit);
        Assert.Equal(actualTree, identity.TreeId);
        Assert.True(identity.IsClean);
    }

    [Fact]
    public async Task Production_scope_batches_many_objects_and_preserves_weird_paths()
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "src"));
        var weirdPaths = OperatingSystem.IsWindows()
            ? new[] { "src/space name.ts", "src/ünicode.ts" }
            : ["src/space name.ts", "src/tab\tname.ts", "src/line\nname.ts", "src/ünicode.ts"];
        var paths = Enumerable.Range(0, 40).Select(index => $"src/file-{index:D2}.ts")
            .Concat(weirdPaths).ToArray();
        foreach (var path in paths)
            await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, path),
                $"export const value{paths.ToList().IndexOf(path)} = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "--", ".");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.AppendAllTextAsync(System.IO.Path.Combine(temp.Path, paths[0]),
            "export const changed = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        var reader = new GitRepositoryIdentityReader(new ProcessCommandExecutor(), temp.Path, baseCommit);

        var scope = await reader.ReadEvaluationScopeAsync(
            headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(paths.Length, scope.TrackedFiles.Count);
            var materialized = Directory.EnumerateFiles(scope.MaterializedRoot!, "*", SearchOption.AllDirectories)
                .Select(path => System.IO.Path.GetRelativePath(scope.MaterializedRoot!, path)).ToArray();
            Assert.All(paths, path => Assert.True(
                File.Exists(System.IO.Path.Combine(scope.MaterializedRoot!, path)),
                $"Missing {path}; materialized: {string.Join(" | ", materialized)}"));
            Assert.InRange(reader.ObjectBatchInvocationCount, 2, 3);
        }
        finally
        {
            GitRepositoryIdentityReader.DeleteMaterialization(scope.MaterializedRoot);
        }
    }

    [Fact]
    public async Task Production_scope_reports_missing_batch_object_as_incomplete_evidence()
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        var source = System.IO.Path.Combine(temp.Path, "app.ts");
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "export const head = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        var objectId = (await Git(temp.Path, "rev-parse", "HEAD:app.ts")).Trim();
        var objectPath = System.IO.Path.Combine(temp.Path, ".git", "objects", objectId[..2], objectId[2..]);
        Assert.True(File.Exists(objectPath));
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) & ~FileAttributes.ReadOnly);
        File.Delete(objectPath);

        var error = await Assert.ThrowsAsync<EvidenceRepositoryException>(() =>
            new GitRepositoryIdentityReader(new ProcessCommandExecutor(), temp.Path, baseCommit)
                .ReadEvaluationScopeAsync(headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Contains("batch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suppression_scan_caps_directive_bodies_without_suffix_copies()
    {
        var source = "/* eslint-disable " + new string('x',
            JavaScriptSuppressionScanner.MaximumDirectiveBodyBytes + 1) + " */";

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("ceiling", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_parser_rejects_malformed_or_truncated_object_frames()
    {
        var objectId = TestEvidence.Commit('a');
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal) { [objectId] = 4 };
        var malformed = System.Text.Encoding.ASCII.GetBytes($"{objectId} blob 4\nabc\n");

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ParseBatchObjects(
                malformed, [objectId], sizes, TestContext.Current.CancellationToken));

        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_parser_honors_cancellation_before_materializing_objects()
    {
        var objectId = TestEvidence.Commit('a');
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal) { [objectId] = 0 };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GitRepositoryIdentityReader.ParseBatchObjects(
                System.Text.Encoding.ASCII.GetBytes($"{objectId} blob 0\n\n"),
                [objectId], sizes, cancellation.Token));
    }

    [Fact]
    public void Frozen_input_count_and_byte_caps_fail_closed()
    {
        Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ValidateFrozenInputLimits(
                GitRepositoryIdentityReader.MaximumFrozenInputCount + 1, 0));
        Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ValidateFrozenInputLimits(
                1, GitRepositoryIdentityReader.MaximumFrozenInputBytes + 1));
    }

    [Fact]
    public async Task Production_scope_honors_cancellation_before_git_batching()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new GitRepositoryIdentityReader(
            new ProcessCommandExecutor(), temp.Path, TestEvidence.Commit('a'));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadEvaluationScopeAsync(
                TestEvidence.Commit('b'), TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Theory]
    [InlineData("if (ok) /'/.test(value);\n", false)]
    [InlineData("if (ok) /'/.test(value);\n// eslint-disable-next-line x\nrun();\n", true)]
    [InlineData("const text = `// oxlint-disable`;\n", false)]
    [InlineData("const text = '/* eslint-disable */';\n", false)]
    [InlineData("/* oxlint-disable */\n", true)]
    public void Suppression_lexer_handles_jsx_regex_and_literal_edges(string source, bool expected)
    {
        Assert.Equal(expected, GitRepositoryIdentityReader.ContainsSuppressionDirective(source));
    }

    [Theory]
    [InlineData("if (ready) {}\n/'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("const view = <div>{x}'</div>; /* ' */ // eslint-disable-line anti-slop/no-chained-type-assertions\n")]
    [InlineData("await /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("for (const match of /x'/.exec(input)) { // eslint-disable-next-line anti-slop/no-chained-type-assertions -- '\nconst hidden = [] as unknown as string[];\n}\n")]
    [InlineData("check(input)\nreturn /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\n  && ([] as unknown as string[]).length > 0;\n")]
    [InlineData("check(input)\nthrow /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\n  && ([] as unknown as string[]).length > 0;\n")]
    [InlineData("check(input)\nyield /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\n  && ([] as unknown as string[]).length > 0;\n")]
    [InlineData("check(input)\nawait /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\n  && ([] as unknown as string[]).length > 0;\n")]
    [InlineData("check(input)\n/* line terminator remains significant */ return /'/.test(input) /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\n  && ([] as unknown as string[]).length > 0;\n")]
    [InlineData("for await (const match of /x'/.exec(input)) { // eslint-disable-next-line anti-slop/no-chained-type-assertions -- '\nconst hidden = [] as unknown as string[];\n}\n")]
    [InlineData("#!/usr/bin/env node `\nexport const hidden = [] as unknown as string[]; // eslint-disable-line anti-slop/no-chained-type-assertions\n// `\n")]
    [InlineData("\uFEFF#!/usr/bin/env node `\nexport const hidden = [] as unknown as string[]; // eslint-disable-line anti-slop/no-chained-type-assertions\n// `\n")]
    [InlineData("const ok = a</'/.source.length /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("export default /'/.source /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("export class Child extends /'/.constructor { static hidden = [] as unknown as string[]; } /* ' */ // eslint-disable-line anti-slop/no-chained-type-assertions\n")]
    [InlineData("const half = metrics.\n  return / 2 // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("class Counter { #delete = 4; value() { return this.#delete / 2 // eslint-disable-next-line anti-slop/no-chained-type-assertions\n;([] as unknown as string[]).length; } }\n")]
    [InlineData("const value = ++/'/.lastIndex /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("const value = --/'/.lastIndex /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = [] as unknown as string[];\n")]
    [InlineData("import { x } from'pkg' /* ' */ // eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = x as unknown as string[];\n")]
    [InlineData("export const View = () => (\n  <p>\n    use ` here\n  </p>\n);\n// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = x as unknown as string[];\n// `\n")]
    [InlineData("export const View = () => (\n  <p>\n    {name} use ` here\n  </p>\n);\n// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = x as unknown as string[];\n// `\n")]
    [InlineData("export const View = () => <p>{name} x 'y</p>; // eslint-disable-line anti-slop/no-chained-type-assertions -- '\nexport const hidden = x as unknown as string[];\n")]
    public void Statement_regex_and_jsx_boundaries_cannot_hide_directives(string source)
    {
        try
        {
            Assert.True(GitRepositoryIdentityReader.ContainsSuppressionDirective(source));
        }
        catch (EvidenceRepositoryException error)
        {
            Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("export const View = () => (\n  <p>\n    {format(\"}\")} use ` here\n  </p>\n);\n// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const hidden = x as unknown as string[];\n// `\n")]
    [InlineData("export const View = () => <p>{format(\"}\")} x 'y</p>; // eslint-disable-line anti-slop/no-chained-type-assertions -- '\nexport const hidden = x as unknown as string[];\n")]
    public void Jsx_expression_string_braces_cannot_hide_directives(string source)
    {
        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("JSX text", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jsx_expression_comment_braces_cannot_hide_directives()
    {
        const string source =
            "export const View = () => (\n  <p>\n    {/* } */ format(value)} use ` here\n  </p>\n);\n" +
            "// eslint-disable-next-line anti-slop/no-chained-type-assertions\n" +
            "export const hidden = x as unknown as string[];\n// `\n";

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("JSX text", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("const of = 4; x = of / 2 // eslint-disable-line\n")]
    [InlineData("x = object.in / 2 // eslint-disable-line\n")]
    [InlineData("x = object.delete / 2 // eslint-disable-line\n")]
    [InlineData("x = object.return / 2 // eslint-disable-line\n")]
    [InlineData("x = object.\nreturn / 2 // eslint-disable-line\n")]
    [InlineData("class C { #delete = 2; value() { return this.#delete / 2 // eslint-disable-line\n} }\n")]
    [InlineData("for (of / 2; of < 3; of++) {} // eslint-disable-line\n")]
    [InlineData("for (let value = object.of / 2; value < 3; value++) {} // eslint-disable-line\n")]
    public void Identifier_and_property_division_cannot_hide_directives(string source)
    {
        Assert.True(GitRepositoryIdentityReader.ContainsSuppressionDirective(source));
    }

    [Fact]
    public void Jsx_guess_ambiguity_fails_closed()
    {
        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(
                "n > '/'; // eslint-disable-line\nconst value = factory<Type>();\n"));
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jsx_text_slash_cannot_hide_the_reviewed_suppression_attack()
    {
        const string source =
            "export function f(input: { x: number }) { return <p>/[</p>; } // eslint-disable-line anti-slop/no-object-parameters ]/";

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_expression_regex_cannot_hide_the_reviewed_suppression_attack()
    {
        const string source = """
            if (globalThis) {
              const s = `${/"/.source}`; function h(input: { x: number }) { return input.x; } // eslint-disable-line anti-slop/no-object-parameters -- "
            }
            // `
            """;

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("template expression", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule_specific_enable_does_not_end_blanket_disable_and_prefixed_alias_is_dangerous()
    {
        var blanket = JavaScriptSuppressionScanner.Scan(System.Text.Encoding.UTF8.GetBytes(
            "/* eslint-disable */\n/* eslint-enable no-console */\nexport function f(input: { x: number }) { return input.x; }\n"),
            TestContext.Current.CancellationToken);
        var alias = JavaScriptSuppressionScanner.Scan(System.Text.Encoding.UTF8.GetBytes(
            "// eslint-disable-next-line other/no-object-parameters\nexport function f(input: { x: number }) { return input.x; }\n"),
            TestContext.Current.CancellationToken);
        var eslintAlias = JavaScriptSuppressionScanner.Scan(System.Text.Encoding.UTF8.GetBytes(
            "// eslint-disable-next-line eslint/no-accumulating-spread\nexport const copied = values.reduce((acc, value) => [...acc, value], []);\n"),
            TestContext.Current.CancellationToken);

        Assert.True(JavaScriptSuppressionScanner.IntroducesDangerousCoverage(blanket, null));
        Assert.True(JavaScriptSuppressionScanner.IntroducesDangerousCoverage(alias, null));
        Assert.True(JavaScriptSuppressionScanner.IntroducesDangerousCoverage(eslintAlias, null));
    }

    [Fact]
    public void Line_dense_suppression_input_fails_before_allocating_unbounded_line_hashes()
    {
        var source = System.Text.Encoding.UTF8.GetBytes(
            new string('\n', JavaScriptSuppressionScanner.MaximumLinesPerInput));

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            JavaScriptSuppressionScanner.Scan(source, TestContext.Current.CancellationToken));

        Assert.Contains("line", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Regex_after_closing_brace_is_ambiguous_and_fails_closed()
    {
        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective("}\n/'/.test(value);\n"));
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reads_exact_head_tree_and_clean_status_from_git()
    {
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(TestEvidence.Commit('b') + Environment.NewLine),
                Result(TestEvidence.Commit('a') + Environment.NewLine),
                Result(string.Empty),
                Result(TestEvidence.Commit('c') + Environment.NewLine),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TestEvidence.Repository(), identity);
    }

    [Theory]
    [InlineData("filter.evil.clean", "touch-should-never-run")]
    [InlineData("include.path", "/tmp/patch-controlled-git-include")]
    [InlineData("core.attributesFile", "/tmp/patch-controlled-gitattributes")]
    public async Task Production_identity_rejects_unsafe_repository_local_configuration(
        string key, string value)
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "app.ts"),
            "export const value = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", ".");
        await Git(temp.Path, "commit", "-m", "base");
        var commit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await Git(temp.Path, "config", "--local", key, value);

        var error = await Assert.ThrowsAsync<EvidenceRepositoryException>(() =>
            new GitRepositoryIdentityReader(new ProcessCommandExecutor(), temp.Path, commit)
                .ReadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("forbidden", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Untracked_or_modified_paths_make_the_snapshot_unclean()
    {
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(TestEvidence.Commit('b') + Environment.NewLine),
                Result(TestEvidence.Commit('a') + Environment.NewLine),
                Result(string.Empty),
                Result(TestEvidence.Commit('c') + Environment.NewLine),
                Result("?? generated.txt" + Environment.NewLine)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var identity = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.False(identity.IsClean);
    }

    [Fact]
    public async Task Evaluation_scope_includes_every_tracked_javascript_file_even_when_ignore_files_name_it()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\t.gitignore\0100644\t{TestEvidence.Commit('2')}\t.oxlintignore\0100644\t{TestEvidence.Commit('3')}\tgenerated/out.ts\0100644\t{TestEvidence.Commit('4')}\tsrc/app.ts\0100644\t{TestEvidence.Commit('5')}\tsrc/git-ignored.ts\0100644\t{TestEvidence.Commit('6')}\tsrc/tool-ignored.ts\0100644\t{TestEvidence.Commit('7')}\tsrc/app.cs\0"),
                Result(string.Empty, 128),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(
            [".gitignore", ".oxlintignore", "generated/out.ts", "src/app.cs", "src/app.ts", "src/git-ignored.ts", "src/tool-ignored.ts"],
            scope.TrackedFiles);
        Assert.Empty(scope.IgnoredFiles);
        Assert.StartsWith("sha256:", scope.InputSetIdentity);
    }

    [Fact]
    public async Task New_inline_suppression_in_a_changed_frozen_blob_is_reported()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result("/* eslint-disable */\nexport const value = 1;\n"),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const value = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
    }

    [Fact]
    public async Task Moved_disable_next_line_with_unchanged_anchor_is_inherited()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result("export const unsafe = value as unknown as string;\n// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const old = 1;\n"),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("// eslint-disable-next-line anti-slop/no-chained-type-assertions\nexport const old = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.SuppressionViolations!);
    }

    [Fact]
    public async Task Inherited_blanket_suppression_covering_changed_code_is_rejected()
    {
        var head = TestEvidence.Commit('b');
        const string content = "/* oxlint-disable */\nexport const changed = 2;\n";
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result(content),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("/* oxlint-disable */\nexport const changed = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
    }

    [Fact]
    public async Task Ordinary_eslint_or_oxlint_prose_is_not_a_directive()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result("// run oxlint before commit\nexport const eslintName = 'eslint config';\n"),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const eslintName = 'old';\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.SuppressionViolations!);
    }

    [Fact]
    public async Task Narrow_unrelated_directive_is_legitimate_and_protected_ignore_skips_scanning()
    {
        var head = TestEvidence.Commit('b');
        var unrelated = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result("// eslint-disable-next-line no-console\nconsole.log('ok');\n"),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const old = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(), TestEvidence.Commit('a'));
        var ignored = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result("src/app.ts\n"),
                Result("src/app.ts\0")),
            System.IO.Path.GetTempPath(), TestEvidence.Commit('a'));

        var unrelatedScope = await unrelated.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        var ignoredScope = await ignored.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(unrelatedScope.SuppressionViolations!);
        Assert.Empty(ignoredScope.SuppressionViolations!);
        Assert.Contains("src/app.ts", ignoredScope.IgnoredFiles);
    }

    [Fact]
    public async Task Directive_spellings_inside_literals_or_non_directive_prose_are_ignored()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result("const url = \"http://eslint-disable\";\nconst text = '/* oxlint-disable */';\nconst template = `// eslint-disable-next-line`;\n// Documentation mentions eslint-disable but does not activate it.\n/* Use oxlint-disable only after approval. */\n"),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const old = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.SuppressionViolations!);
    }

    [Theory]
    [InlineData("const a = b\n  / c /* oxlint-disable */ / d;\n")]
    [InlineData("x = p.catch(e) / 2 /* oxlint-disable */ / 3;\n")]
    [InlineData("[typeof'x' /* oxlint-disable */, 'z'in globalThis];\n")]
    [InlineData("x = {} / 1 /* oxlint-disable */ / 2;\n")]
    [InlineData("a++ / 1 /* oxlint-disable */ / 2;\n")]
    [InlineData("const view = <div><p>\"</p>{/* oxlint-disable */}<p>\"</p></div>;\n")]
    [InlineData("const view = <div><p>`</p>{/* oxlint-disable */}<p>`</p></div>;\n")]
    [InlineData("// note\u2028/* oxlint-disable */\n")]
    public async Task Conservative_scan_blocks_known_lexer_bypass_shapes(string content)
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result(content),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const old = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        try
        {
            var scope = await reader.ReadEvaluationScopeAsync(
                head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(["src/app.ts"], scope.SuppressionViolations);
        }
        catch (EvidenceRepositoryException error)
        {
            Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Deep_template_nesting_fails_closed_without_recursive_stack_growth()
    {
        var source = string.Concat(Enumerable.Repeat("`${", JavaScriptSuppressionScanner.MaximumNestingDepth + 1)) +
            "0" + string.Concat(Enumerable.Repeat("}`", JavaScriptSuppressionScanner.MaximumNestingDepth + 1));

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("nesting", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_expression_jsx_cannot_hide_the_exact_reviewed_directive()
    {
        const string source = """
            if (globalThis) {
              const s = `${<a>it's</a>}`; const f = (input: object) => input; // eslint-disable-line anti-slop/no-object-parameters -- '
            }
            // `
            """;

        var error = Assert.Throws<EvidenceRepositoryException>(() =>
            GitRepositoryIdentityReader.ContainsSuppressionDirective(source));

        Assert.Contains("JSX-shaped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deleting_enable_extends_blanket_coverage_over_changed_code()
    {
        var @base = JavaScriptSuppressionScanner.Scan(System.Text.Encoding.UTF8.GetBytes(
            "/* oxlint-disable */\nexport const old = 1;\n/* oxlint-enable */\nexport const tail = 1;\n"), TestContext.Current.CancellationToken);
        var head = JavaScriptSuppressionScanner.Scan(System.Text.Encoding.UTF8.GetBytes(
            "/* oxlint-disable */\nexport const old = 1;\nexport const tail = 2;\n"), TestContext.Current.CancellationToken);

        Assert.True(JavaScriptSuppressionScanner.IntroducesDangerousCoverage(head, @base));
    }

    [Theory]
    [InlineData("const value = 1; // eslint-disable-line anti-slop/no-chained-type-assertions\n", true)]
    [InlineData("// oxlint-disable-next-line anti-slop/no-chained-type-assertions\nconst value = 1;\n", true)]
    [InlineData("/* eslint-disable anti-slop/no-chained-type-assertions */\nconst value = 1;\n", true)]
    [InlineData("/* oxlint-enable anti-slop/no-chained-type-assertions */\nconst value = 1;\n", false)]
    [InlineData("// eslint-disable-next-line no-console */, anti-slop/no-object-parameters\nexport function value(input: { x: number }) { return input.x; }\n", true)]
    [InlineData("/* eslint-disable no-console,\n anti-slop/no-object-parameters */\nexport function value(input: { x: number }) { return input.x; }\n", true)]
    public async Task Actual_supported_directive_comment_forms_have_semantic_coverage(
        string content,
        bool expectedViolation)
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\tsrc/app.ts\0"),
                Result(string.Empty, 128),
                Result("src/app.ts\0"),
                Result(content),
                Result(TestEvidence.Commit('2') + Environment.NewLine),
                Result("export const old = 1;\n"),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(expectedViolation ? ["src/app.ts"] : [], scope.SuppressionViolations);
    }

    [Theory]
    [InlineData("// eslint-disable-next-line no-console */, anti-slop/no-object-parameters\nexport function value(input: { x: number }) { return input.x; }\n")]
    [InlineData("/* eslint-disable no-console,\n anti-slop/no-object-parameters */\nexport function value(input: { x: number }) { return input.x; }\n")]
    public async Task Production_scope_detects_full_comment_body_suppression_attacks(string attack)
    {
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        var source = System.IO.Path.Combine(temp.Path, "app.ts");
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", "app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, attack, TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();

        var scope = await new GitRepositoryIdentityReader(
            new ProcessCommandExecutor(), temp.Path, baseCommit).ReadEvaluationScopeAsync(
            headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(["app.ts"], scope.SuppressionViolations);
        }
        finally
        {
            GitRepositoryIdentityReader.DeleteMaterialization(scope.MaterializedRoot);
        }
    }

    [Fact]
    public async Task Production_scope_lints_frozen_git_bytes_despite_checkout_eol_conversion()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temp = new TempDirectory();
        await Git(temp.Path, "init");
        await Git(temp.Path, "config", "user.name", "Delivery Forge Test");
        await Git(temp.Path, "config", "user.email", "forge@example.invalid");
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".gitattributes"),
            "*.ts text eol=crlf\n", TestContext.Current.CancellationToken);
        var source = System.IO.Path.Combine(temp.Path, "app.ts");
        await File.WriteAllTextAsync(source, "export const before = 1;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "add", ".gitattributes", "app.ts");
        await Git(temp.Path, "commit", "-m", "base");
        var baseCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(source, "export const head = 2;\n", TestContext.Current.CancellationToken);
        await Git(temp.Path, "commit", "-am", "head");
        var headCommit = (await Git(temp.Path, "rev-parse", "HEAD")).Trim();
        File.Delete(source);
        await Git(temp.Path, "checkout", "--", "app.ts");
        Assert.Contains("\r\n", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));

        var scope = await new GitRepositoryIdentityReader(
            new ProcessCommandExecutor(), temp.Path, baseCommit).ReadEvaluationScopeAsync(
            headCommit, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal("export const head = 2;\n", await File.ReadAllTextAsync(
                System.IO.Path.Combine(scope.MaterializedRoot!, "app.ts"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            GitRepositoryIdentityReader.DeleteMaterialization(scope.MaterializedRoot);
        }
    }

    [Fact]
    public async Task Javascript_symlink_is_rejected_and_bound_into_scope_identity()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"120000\t{TestEvidence.Commit('1')}\tsrc/link.ts\0"),
                Result(string.Empty, 128),
                Result(string.Empty)),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["src/link.ts"], scope.InvalidInputs);
        Assert.StartsWith("sha256:", scope.InputSetIdentity);
    }

    [Fact]
    public async Task Visual_studio_ignore_patterns_and_root_vendor_names_cannot_hide_tracked_inputs()
    {
        var head = TestEvidence.Commit('b');
        var reader = new GitRepositoryIdentityReader(
            new QueueCommandExecutor(
                Result(head + Environment.NewLine),
                Result($"100644\t{TestEvidence.Commit('1')}\t.gitignore\0100644\t{TestEvidence.Commit('2')}\tnode_modules/owned.ts\0100644\t{TestEvidence.Commit('3')}\tvendor/anti-slop/owned.ts\0"),
                Result(string.Empty, 128),
                Result(".gitignore\0")),
            System.IO.Path.GetTempPath(),
            TestEvidence.Commit('a'));

        var scope = await reader.ReadEvaluationScopeAsync(
            head, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Empty(scope.IgnoredFiles);
        Assert.Contains("node_modules/owned.ts", scope.TrackedFiles);
        Assert.Contains("vendor/anti-slop/owned.ts", scope.TrackedFiles);
    }

    private static CommandResult Result(string stdout, int exitCode = 0) =>
        new(exitCode, stdout, string.Empty, false, false, TestEvidence.Time(0), TestEvidence.Time(1));

    private static async Task<string> Git(string root, params string[] arguments)
    {
        var result = await new ProcessCommandExecutor().ExecuteAsync(
            new CommandInvocation("git", arguments, root,
                new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" }),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput;
    }
}
