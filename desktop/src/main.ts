if (process.argv.includes("--integration-test") || process.argv.includes("--fixture")) {
  await import("./fake-main.js");
} else {
  await import("./workspace-main.js");
}
