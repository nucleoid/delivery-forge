# Vendored anti-slop provenance

- Upstream: <https://github.com/dmmulroy/anti-slop>
- Pinned commit: `13909c9e07590f5a85d93c62712259fe14a883f7`
- Upstream package version at the pin: `0.1.2` (`private: true`; there is no official npm package)
- License: MIT; the upstream `LICENSE` is retained verbatim as `vendor/anti-slop/LICENSE`.
- Runtime: `oxlint` and `@oxlint/plugins` are both pinned exactly to `1.78.0`; the reviewed upstream development toolchain is pinned to `tsx` `4.23.12` and TypeScript `7.0.2`.

`vendor/anti-slop/` contains the reviewed runtime rule source required by the generic plugin and the separately registered Effect plugin. Upstream tests, skills, scripts, repository configuration, and package-manager metadata are intentionally excluded. The nested ESLint Stylistic license and provenance used by `require-readable-spacing` are retained with that source.

The generic rules are enabled by `oxlint.config.ts`. Effect-specific rules are disabled by default and exist only in `oxlint.effect.config.ts`, which must be selected explicitly by trusted plugin configuration. Delivery Forge never invokes Oxlint autofix and does not rewrite a target repository's lint configuration.

At execution, Delivery Forge derives the complete tracked JavaScript-family candidate set from the exact frozen Git tree and applies frozen `.gitignore`, root `.oxlintignore`, root `.eslintignore`, and bundled config ignores. Caller inputs must match that derived set exactly; partial, empty, unsupported, duplicate, escaping, option-like, or invocation-mismatched sets cannot pass. The normalized set hash remains bound inside the v1 receipt command envelope.

The host registers the installed bundle root and absolute Node executable in trusted code. Gate execution clears the inherited environment, permits only the small platform allow-list, invokes the package-local Oxlint launcher through that absolute Node runtime, and binds/rechecks the full config, vendored-rule, lockfile, Oxlint JS/native/plugin, runtime, and environment closure.

`npm run test:fixtures` invokes only the package-local pinned Oxlint binary. It proves clean, policy-violation, and malformed-syntax behavior against real JavaScript-plugin execution without mocks, autofix, target configuration changes, or global installation.
