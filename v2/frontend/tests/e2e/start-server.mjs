import { spawn } from "node:child_process";
import { mkdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const v2Root = resolve(currentDirectory, "../../..");
const runtimeRoot = resolve(v2Root, "artifacts/e2e/runtime");
const dataRoot = resolve(runtimeRoot, "data");
const configRoot = resolve(runtimeRoot, "config");
const serverDll = resolve(
  v2Root,
  "src/CipherVault.Server/bin/Release/net10.0/CipherVault.Server.dll",
);

rmSync(runtimeRoot, { recursive: true, force: true });
mkdirSync(dataRoot, { recursive: true });
mkdirSync(configRoot, { recursive: true });

const child = spawn("dotnet", [serverDll], {
  cwd: v2Root,
  stdio: "inherit",
  env: {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: "Development",
    ASPNETCORE_URLS: "http://127.0.0.1:43199",
    CIPHERVAULT_DATA_DIR: dataRoot,
    CIPHERVAULT_CONFIG_DIR: configRoot,
    CIPHERVAULT_IDLE_TIMEOUT_MINUTES: "10",
  },
});

const stop = () => {
  child.kill();
};

process.on("SIGINT", stop);
process.on("SIGTERM", stop);
child.on("exit", (code) => {
  process.exit(code ?? 0);
});
