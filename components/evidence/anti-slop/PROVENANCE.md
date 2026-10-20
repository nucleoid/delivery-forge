# Vendored anti-slop provenance

- Upstream: <https://github.com/dmmulroy/anti-slop>
- Pinned commit: `13909c9e07590f5a85d93c62712259fe14a883f7`
- Upstream package version at the pin: `0.1.2` (`private: true`; there is no official npm package)
- License: MIT; the upstream `LICENSE` is retained verbatim as `vendor/anti-slop/LICENSE`.
- Runtime: `oxlint` and `@oxlint/plugins` are both pinned exactly to `1.78.0`; the reviewed upstream development toolchain is pinned to `tsx` `4.23.12` and TypeScript `7.0.2`.

`vendor/anti-slop/` contains the reviewed runtime rule source required by the generic plugin and the separately registered Effect plugin. Upstream tests, skills, scripts, repository configuration, and package-manager metadata are intentionally excluded. The nested ESLint Stylistic license and provenance used by `require-readable-spacing` are retained with that source.

The generic rules are enabled by `oxlint.config.ts`. Effect-specific rules are disabled by default and exist only in `oxlint.effect.config.ts`, which must be selected explicitly by trusted plugin configuration. Delivery Forge never invokes Oxlint autofix and does not rewrite a target repository's lint configuration.

At execution, Delivery Forge derives every tracked JavaScript-family entry from the exact frozen Git tree. Only exact paths in protected-base `.delivery-forge/anti-slop-ignore` may be excluded; patch-head ignore files have no authority. The input identity hashes each path, blob id, mode and include/exclude decision. Non-regular entries fail closed. Changed files containing live ESLint/Oxlint suppression directives in either base or head are rejected, including inherited or moved disable/enable and next-line directives.

The host registers the installed bundle root and absolute Node executable in trusted code. Direct argv is capped at 24,000 bytes; bundled `run-oxlint-batches.mjs` safely batches larger manifests with argument arrays, no shell, bounded output, and aggregate completeness checks. The config, runner, manifest, vendored rules, lockfile, Oxlint JS/native/plugin, runtime, and environment closure is bound and rechecked.

`npm run test:fixtures` invokes only the package-local pinned Oxlint binary with ignores and nested target configuration disabled. `npm run test:batch` exercises the code-owned manifest batch host against the same pin. These prove clean, policy-violation, malformed-syntax, inline-suppression, and batch behavior against real plugin execution without global installation.
