import generic from "./oxlint.config.ts";
import { defineConfig } from "oxlint";

export default defineConfig({
  ...generic,
  jsPlugins: [
    { name: "anti-slop", specifier: "./vendor/anti-slop/index.ts" },
    { name: "anti-slop-effect", specifier: "./vendor/anti-slop/effect/index.ts" }
  ],
  rules: {
    ...generic.rules,
    "anti-slop-effect/no-manual-effect-error-tag": "error",
    "anti-slop-effect/no-manual-tag-comparison": "error",
    "anti-slop-effect/no-manual-tagged-construction": "error",
    "anti-slop-effect/no-service-constructor-imports": "error",
    "anti-slop-effect/prefer-effect-match": "error"
  }
});
