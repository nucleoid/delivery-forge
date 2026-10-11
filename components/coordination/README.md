# Coordination prototype compatibility

The coordination component is a fixture-scoped prototype. `CreateProductionAsync` remains
unconditionally unavailable: there is no runtime consumer, configured endpoint, persistent
checkpoint-store implementation, deployed lease, or data migration to roll out.

`LeaseOwnership.CleanupComplete` is an additive neutral cleanup summary. Existing enum values keep
their numeric values; `CleanupComplete` is value `5`. `Released` now means every known lease identity
has durable release proof. Cleanup completed through validated absence or replacement proof reports
`CleanupComplete`, including mixed released/not-owned results. Per-lease identities, fences, and
terminal states remain available in `ReleaseResult.Leases`.

Because the component has no production consumer or persisted store, this source-level API addition
requires no mixed-version migration. A future trusted host must version its own public serialization,
checkpoint persistence, and rollout before enabling production coordination. Fixture writer epochs
and takeover proof strings are conformance evidence only and do not provide production exclusivity.
