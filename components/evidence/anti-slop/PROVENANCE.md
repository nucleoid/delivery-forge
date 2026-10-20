# Vendored anti-slop provenance

- Upstream: <https://github.com/dmmulroy/anti-slop>
- Pinned commit: `13909c9e07590f5a85d93c62712259fe14a883f7`
- Upstream package version at the pin: `0.1.2` (`private: true`; there is no official npm package)
- License: MIT; the upstream `LICENSE` is retained verbatim as `vendor/anti-slop/LICENSE`.
- Runtime: `oxlint` and `@oxlint/plugins` are both pinned exactly to `1.78.0`; the reviewed upstream development toolchain is pinned to `tsx` `4.23.12` and TypeScript `7.0.2`.

`vendor/anti-slop/` contains the reviewed runtime rule source required by the generic plugin and the separately registered Effect plugin. Upstream tests, skills, scripts, repository configuration, and package-manager metadata are intentionally excluded. The nested ESLint Stylistic license and provenance used by `require-readable-spacing` are retained with that source.

The generic rules are enabled by `oxlint.config.ts`. Effect-specific rules are disabled by default and exist only in `oxlint.effect.config.ts`, which must be selected explicitly by trusted plugin configuration. Delivery Forge never invokes Oxlint autofix and does not rewrite a target repository's lint configuration.

At execution, Delivery Forge derives every tracked JavaScript-family file from the exact frozen Git tree. Ignore files and tracked `node_modules/` or `vendor/anti-slop/` paths cannot remove target inputs. Caller inputs must match that set exactly; partial, empty, unsupported, duplicate, escaping, option-like, or invocation-mismatched sets cannot pass. The input identity hashes each frozen path and Git blob id and is bound with the exact head in the v2 authority receipt. New or changed inline ESLint/Oxlint suppression and rule-configuration directives are rejected by comparing exact frozen-head blobs to the protected base.

The host registers the installed bundle root and absolute Node executable in trusted code. Gate execution clears the inherited environment, permits only the small platform allow-list, invokes the package-local Oxlint launcher through that absolute Node runtime, and binds/rechecks the full config, vendored-rule, lockfile, Oxlint JS/native/plugin, runtime, and environment closure.

`npm run test:fixtures` invokes only the package-local pinned Oxlint binary with ignores and nested target configuration disabled. It proves clean, policy-violation, malformed-syntax, and inline-suppression behavior against real JavaScript-plugin execution without mocks, autofix, target configuration changes, or global installation. The suppression fixture intentionally demonstrates why frozen-blob directive enforcement is required before Oxlint runs.
