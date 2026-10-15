import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["tests/**/*.test.ts"],
    fileParallelism: false,
    setupFiles: ["tests/setup/isolate-local-app-data.ts"],
  },
});
