import { createHash } from "node:crypto"
import { existsSync, readFileSync, statSync, writeFileSync } from "node:fs"
import { resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { fileURLToPath } from "node:url"

const INPUT_KEYS = ["backendProject", "classification", "component", "dependencies", "frontendDirectory", "inputType", "leafSdk", "redbamboo", "redleafReleaseToolCommit", "runtimeRequirements", "schemaVersion", "targetPlatform", "testProject", "toolchain"]
const SHARED = [
  ["redbamboo-ui", "@redbamboo/ui", "packages/ui"],
  ["redbamboo-utility", "@redbamboo/utility", "packages/utility"],
]
const PERMISSIONS = [
  { kind: "net", target: "gmail.googleapis.com", reason: "Gmail API (scope gmail.readonly only)" },
  { kind: "net", target: "oauth2.googleapis.com", reason: "OAuth token exchange, refresh, and revocation" },
  { kind: "net", target: "accounts.google.com", reason: "Owner consent page opened in the owner's browser" },
  { kind: "net", target: "127.0.0.1", reason: "Ephemeral loopback listener receiving the OAuth redirect during a consent attempt" },
  { kind: "process", target: "Leaf.Plugins.Mail.Extractor.exe", reason: "Bundled attachment text extractor (backend/extractor/, never PATH), started once per PDF, HTML, calendar or email attachment read, inside a Windows job object (512 MB, 15 s, one process, no UI): bytes in on stdin, text out on stdout, no network, nothing written to disk" },
]
const SLOTS = [
  { surface: "backend", role: "contributes", id: "nova-mail-context", target: "nova", slot: "context-contributor", contract: "leaf.context-fragment/1" },
  { surface: "backend", role: "contributes", id: "nova-mail-live", target: "nova", slot: "live-event", contract: "leaf.live-event/1" },
]
const LOCKS = [
  { id: "backend-nuget-lock", ecosystem: "nuget", path: "src/Leaf.Plugins.Mail/packages.lock.json" },
  { id: "extractor-nuget-lock", ecosystem: "nuget", path: "src/Leaf.Plugins.Mail.Extractor/packages.lock.json" },
  { id: "tests-nuget-lock", ecosystem: "nuget", path: "tests/Leaf.Plugins.Mail.Tests/packages.lock.json" },
  { id: "web-pnpm-lock", ecosystem: "npm", path: "web/pnpm-lock.yaml" },
]
// Users bring their own Google desktop client; nothing credential-shaped may be a package input.
// tests/ is never packaged and holds only obvious fakes, so it is not scanned for markers.
const SECRET_PATH = /(^|\/)(client_secret[^/]*|credentials?[^/]*|[^/]*tokens?)\.json$|\.(pem|pfx|p12|eml|mbox)$/i
const SECRET_MARKERS = [/GOCSPX-[A-Za-z0-9_-]{8,}/, /ya29\.[A-Za-z0-9_-]{8,}/, /\b1\/\/0[A-Za-z0-9_-]{16,}/, /"(client_secret|refresh_token|access_token)"\s*:\s*"[^"]+"/]
const PACKAGE_SOURCES = /^(plugin\.json|seeds\/|src\/|web\/|build\/)/

function fail(message) { throw new Error(message) }
function json(path) { return JSON.parse(readFileSync(path, "utf8")) }
function keys(value) { return Object.keys(value).sort() }
function exactKeys(value, expected, label) {
  if (!value || typeof value !== "object" || Array.isArray(value) || JSON.stringify(keys(value)) !== JSON.stringify([...expected].sort())) fail(`${label} has unexpected or missing fields.`)
}
function canonical(value) {
  const sort = (item) => Array.isArray(item) ? item.map(sort) : item && typeof item === "object"
    ? Object.fromEntries(keys(item).map((key) => [key, sort(item[key])])) : item
  return `${JSON.stringify(sort(value), null, 2)}\n`
}
function same(actual, expected, label) {
  if (canonical(actual) !== canonical(expected)) fail(`${label} drifted.`)
}
function run(command, args, cwd) {
  const shell = process.platform === "win32" && command.toLowerCase().endsWith(".cmd")
  const result = spawnSync(command, args, { cwd, encoding: "utf8", windowsHide: true, shell })
  if (result.error || result.status !== 0) fail((result.stderr || result.stdout || result.error?.message || `${command} failed`).trim())
  return result.stdout.trimEnd()
}
function git(root, ...args) { return run("git", ["-C", root, ...args]) }
function hashFile(path) { return createHash("sha256").update(readFileSync(path)).digest("hex") }
function facts(path) { return { sizeBytes: statSync(path).size, sha256: hashFile(path) } }
function ordinal(a, b) { return a < b ? -1 : a > b ? 1 : 0 }
function cleanGit(root, label) {
  if (git(root, "status", "--porcelain=v1", "--untracked-files=all")) fail(`${label} checkout must be clean.`)
}
export function validateExactVersionOverride(head, current, expectedVersion) {
  const headVersion = head.manifest.version
  if (head.input.component.version !== headVersion || head.packageJson.version !== headVersion) fail("Committed Mail version fields are inconsistent.")
  if (expectedVersion === headVersion) fail("An ephemeral release override must change the committed version.")
  if (current.manifest.version !== expectedVersion || current.input.component.version !== expectedVersion || current.packageJson.version !== expectedVersion) fail("Ephemeral Mail version fields must match the requested version.")
  const restored = structuredClone(current)
  restored.manifest.version = headVersion
  restored.input.component.version = headVersion
  restored.packageJson.version = headVersion
  if (canonical(restored.manifest) !== canonical(head.manifest)
      || canonical(restored.input) !== canonical(head.input)
      || canonical(restored.packageJson) !== canonical(head.packageJson)) fail("Mail checkout contains changes beyond the exact release version fields.")
}
function cleanMailGit(root, input, manifest, packageJson) {
  const status = git(root, "status", "--porcelain=v1", "--untracked-files=all")
  if (!status) return
  const expectedPaths = ["plugin.json", "release/producer-input.v1.json", "web/package.json"]
  const entries = status.split(/\r?\n/)
  if (entries.length !== expectedPaths.length
      || entries.some((entry) => !entry.startsWith(" M "))
      || entries.map((entry) => entry.slice(3)).sort(ordinal).some((path, index) => path !== expectedPaths[index])) fail("Mail checkout must be clean except for the exact workflow-owned version fields.")
  const atHead = (path) => JSON.parse(git(root, "show", `HEAD:${path}`))
  validateExactVersionOverride({
    manifest: atHead("plugin.json"),
    input: atHead("release/producer-input.v1.json"),
    packageJson: atHead("web/package.json"),
  }, { manifest, input, packageJson }, input.component.version)
}
export function validateNoSecrets(files, read) {
  const named = files.filter((path) => SECRET_PATH.test(path))
  if (named.length) fail(`Credential-shaped files must never be committed: ${named.join(", ")}.`)
  for (const path of files.filter((item) => PACKAGE_SOURCES.test(item))) {
    const text = read(path)
    if (text !== null && SECRET_MARKERS.some((marker) => marker.test(text))) fail(`Package input '${path}' contains an OAuth client secret or token.`)
  }
}
function exactCommit(value, label) {
  if (!/^[a-f0-9]{40}$/.test(value)) fail(`${label} must be one full lowercase commit SHA.`)
}
function addOptional(target, key, value) { if (value !== undefined && value !== null) target[key] = value }

export function validateInput(input, manifest, packageJson) {
  exactKeys(input, INPUT_KEYS, "producer input")
  exactKeys(input.component, ["id", "kind", "version"], "component")
  exactKeys(input.toolchain, ["dotnetSdk", "msbuild", "node", "pnpm"], "toolchain")
  exactKeys(input.targetPlatform, ["architecture", "operatingSystem"], "targetPlatform")
  exactKeys(input.leafSdk, ["commit", "repository", "sourcePath"], "Leaf.Sdk")
  exactKeys(input.redbamboo, ["commit", "inputs", "lockPath", "repository"], "RedBamboo")
  if (input.schemaVersion !== 1 || input.inputType !== "mail-extension-release-producer-input") fail("Unsupported producer input.")
  if (input.classification !== "optional") fail("Mail must be classified optional.")
  exactCommit(input.redleafReleaseToolCommit, "RedLeaf release-tool pin")
  exactCommit(input.leafSdk.commit, "Leaf.Sdk pin")
  exactCommit(input.redbamboo.commit, "RedBamboo pin")
  if (input.leafSdk.repository !== "RedBamboo-Interactive/redleaf" || input.leafSdk.sourcePath !== "src/Leaf.Sdk") fail("Leaf.Sdk repository or source path is invalid.")
  if (input.redbamboo.repository !== "RedBamboo-Interactive/redbamboo-packages" || input.redbamboo.lockPath !== "pnpm-lock.yaml") fail("RedBamboo repository or lock path is invalid.")
  if (input.targetPlatform.operatingSystem !== "windows" || input.targetPlatform.architecture !== "x64") fail("Mail Release output must target windows/x64.")
  if (input.backendProject !== "src/Leaf.Plugins.Mail/Leaf.Plugins.Mail.csproj" || input.testProject !== "tests/Leaf.Plugins.Mail.Tests/Leaf.Plugins.Mail.Tests.csproj" || input.frontendDirectory !== "web") fail("Mail build or test paths are invalid.")
  if (input.component.id !== "mail" || input.component.kind !== "extension" || input.component.version !== manifest.version) fail("Producer component identity must match plugin.json.")
  if (manifest.id !== "mail" || manifest.kernelApi !== "^0.1.2" || manifest.computeApi !== undefined || manifest.productVersion !== undefined) fail("Mail compatibility drifted.")
  if (manifest.backend?.assembly !== "Leaf.Plugins.Mail" || manifest.backend?.apiNamespace !== "extensions" || !manifest.frontend) fail("plugin.json must describe the Mail backend-plus-frontend extension.")
  if (manifest.frontend.package !== packageJson.name || packageJson.name !== "@redbamboo/plugin-mail" || packageJson.version !== manifest.version) fail("Mail frontend identity does not match plugin.json.")
  if (manifest.build !== undefined) fail("Source plugin.json must not contain Release build evidence.")
  same(manifest.requires, ["entities", "records"], "kernel capability requirements")
  same(manifest.permissions, PERMISSIONS, "privacy-sensitive permissions")
  same(manifest.backend.extensions?.map(({ id, target, slot, contract }) => ({ id, target, slot, contract })), SLOTS.map(({ id, target, slot, contract }) => ({ id, target, slot, contract })), "backend extension contributions")
  if (manifest.frontend.shell !== undefined) fail("Mail declares no shell services.")
  same(manifest.frontend.settings, {}, "settings panel")
  if (packageJson.private !== true || packageJson.packageManager !== `pnpm@${input.toolchain.pnpm}` || packageJson.engines?.node !== input.toolchain.node) fail("package.json Node/pnpm pins do not match producer input.")
  same(input.redbamboo.inputs.map((item) => [item.id, item.name, item.sourcePath]), SHARED, "RedBamboo fan-in")
  for (const [index, item] of input.redbamboo.inputs.entries()) exactKeys(item, ["id", "name", "sourcePath"], `redbamboo.inputs[${index}]`)
  const declaredShared = Object.entries(packageJson.dependencies ?? {}).filter(([name]) => name.startsWith("@redbamboo/"))
  same(declaredShared.map(([name]) => name).sort(), SHARED.map(([, name]) => name).sort(), "frontend shared dependencies")
  for (const [, name, sourcePath] of SHARED) if (packageJson.dependencies[name] !== `link:../../redbamboo-packages/${sourcePath}`) fail(`${name} must use its declared local source link.`)
  if (!Array.isArray(input.dependencies) || input.dependencies.length !== 0 || !Array.isArray(input.runtimeRequirements) || input.runtimeRequirements.length !== 0) fail("Mail has no extension component dependencies or packaged runtime requirements.")
}

export function buildMetadata(input, manifest, source) {
  const compatibility = { kernelApi: manifest.kernelApi }
  addOptional(compatibility, "computeApi", manifest.computeApi)
  addOptional(compatibility, "productVersion", manifest.productVersion)
  return {
    schemaVersion: 1,
    metadataType: "redleaf-extension-release-metadata",
    classification: input.classification,
    compatibility,
    targetPlatform: input.targetPlatform,
    buildId: `source-${source.mailCommit}`,
    builtAt: source.builtAt,
    repository: { id: input.component.id, repositoryUrl: source.mailUrl, commit: source.mailCommit },
    toolchain: { ...input.toolchain, releaseTool: input.redleafReleaseToolCommit },
    build: { backendProject: input.backendProject, frontendDirectory: input.frontendDirectory },
    buildInputs: [
      { id: "leaf-sdk", repositoryUrl: source.leafSdkUrl, commit: input.leafSdk.commit, sourcePath: input.leafSdk.sourcePath },
      ...input.redbamboo.inputs.map((item) => ({ id: item.id, repositoryUrl: source.redbambooUrl, commit: input.redbamboo.commit, sourcePath: item.sourcePath })),
    ].sort((a, b) => ordinal(a.id, b.id)),
    dependencyLocks: [
      ...LOCKS,
      { id: "redbamboo-pnpm-lock", ecosystem: "npm", path: "redbamboo-packages/pnpm-lock.yaml" },
    ].sort((a, b) => ordinal(a.path, b.path)),
    sboms: [],
    dependencies: input.dependencies,
    runtimeRequirements: input.runtimeRequirements,
  }
}

export function validateDescriptor(descriptor, metadata, artifactPath, lockFiles, manifest) {
  const artifact = facts(artifactPath)
  if (descriptor.componentId !== "mail" || descriptor.version !== manifest.version || descriptor.classification !== "optional" || descriptor.componentKind !== "extension") fail("Descriptor Mail identity/classification is invalid.")
  same(descriptor.compatibility, metadata.compatibility, "descriptor compatibility")
  same(descriptor.evidence.buildInputs, metadata.buildInputs, "descriptor build inputs")
  if (descriptor.evidence.repository.commit !== metadata.repository.commit || descriptor.evidence.build.backendProject !== metadata.build.backendProject || descriptor.evidence.build.frontendDirectory !== metadata.build.frontendDirectory) fail("Descriptor source or build layout is invalid.")
  if (descriptor.artifact.sha256 !== artifact.sha256 || descriptor.artifact.sizeBytes !== artifact.sizeBytes) fail("Descriptor artifact facts do not match actual bytes.")
  const expectedLocks = metadata.dependencyLocks.map((item) => ({ ...item, ...facts(lockFiles[item.id]) })).sort((a, b) => ordinal(a.path, b.path))
  same(descriptor.evidence.dependencyLocks, expectedLocks, "descriptor dependency locks")
  same(descriptor.permissions, PERMISSIONS.map((item) => `${item.kind}:${item.target}`).sort(), "descriptor permissions")
  same(descriptor.dependencies, metadata.dependencies, "descriptor component dependencies")
  same(descriptor.provisioning, { present: false, payloadPaths: [], runtimeRequirements: [] }, "descriptor provisioning")
  same(descriptor.slots, SLOTS, "descriptor slots")
  if (descriptor.sboms.length !== 0) fail("Mail does not require an SBOM output.")
}

function args(values) {
  const parsed = new Map()
  for (let i = 0; i < values.length; i += 2) {
    if (!values[i]?.startsWith("--") || values[i + 1] === undefined) fail("Arguments must use --name value.")
    parsed.set(values[i].slice(2), values[i + 1])
  }
  return (name) => parsed.get(name) ?? fail(`--${name} is required.`)
}

function collect(get) {
  const repository = resolve(get("repository"))
  const redbamboo = resolve(get("redbamboo"))
  const leafSdk = resolve(get("leaf-sdk"))
  const redleaf = resolve(get("redleaf"))
  const input = json(get("input"))
  const manifest = json(resolve(repository, "plugin.json"))
  const packageJson = json(resolve(repository, "web/package.json"))
  validateInput(input, manifest, packageJson)
  if (existsSync(resolve(repository, "provision.json")) || existsSync(resolve(repository, "payload"))) fail("Mail must not acquire synthetic provisioning or payload inputs.")
  cleanMailGit(repository, input, manifest, packageJson)
  validateNoSecrets(git(repository, "ls-files", "-z").split("\0").filter(Boolean), (path) => {
    const bytes = readFileSync(resolve(repository, path))
    return bytes.includes(0) ? null : bytes.toString("utf8")
  })
  cleanGit(redbamboo, "RedBamboo")
  cleanGit(leafSdk, "Leaf.Sdk")
  cleanGit(redleaf, "RedLeaf release tool")
  const mailCommit = git(repository, "rev-parse", "HEAD")
  const expectedMailCommit = get("source-commit")
  exactCommit(expectedMailCommit, "Mail source commit")
  if (mailCommit !== expectedMailCommit) fail("Mail checkout does not match the called workflow commit.")
  if (git(redbamboo, "rev-parse", "HEAD") !== input.redbamboo.commit || git(leafSdk, "rev-parse", "HEAD") !== input.leafSdk.commit || git(redleaf, "rev-parse", "HEAD") !== input.redleafReleaseToolCommit) fail("A checkout does not match its immutable producer pin.")
  git(leafSdk, "cat-file", "-e", `HEAD:${input.leafSdk.sourcePath}`)
  for (const item of input.redbamboo.inputs) git(redbamboo, "cat-file", "-e", `HEAD:${item.sourcePath}`)
  const sourceEpoch = get("source-date-epoch")
  if (!/^\d+$/.test(sourceEpoch) || sourceEpoch !== git(repository, "show", "-s", "--format=%ct", "HEAD")) fail("SOURCE_DATE_EPOCH must equal the exact Mail commit timestamp.")
  const builtAt = new Date(Number(sourceEpoch) * 1000).toISOString()
  const corepack = process.platform === "win32" ? "corepack.cmd" : "corepack"
  const actual = { node: process.version.slice(1), pnpm: run(corepack, ["pnpm", "--version"]), dotnetSdk: run("dotnet", ["--version"]), msbuild: run("dotnet", ["msbuild", "-version", "-nologo"]).split(/\r?\n/).at(-1) }
  for (const [name, value] of Object.entries(input.toolchain)) if (actual[name] !== value) fail(`Expected ${name} ${value}, got ${actual[name]}.`)
  const metadata = buildMetadata(input, manifest, {
    mailCommit,
    mailUrl: git(repository, "config", "--get", "remote.origin.url"),
    leafSdkUrl: git(leafSdk, "config", "--get", "remote.origin.url"),
    redbambooUrl: git(redbamboo, "config", "--get", "remote.origin.url"),
    builtAt,
  })
  const locks = Object.fromEntries(LOCKS.map((item) => [item.id, resolve(repository, item.path)]))
  locks["redbamboo-pnpm-lock"] = resolve(redbamboo, input.redbamboo.lockPath)
  for (const [id, path] of Object.entries(locks)) if (!statSync(path).isFile()) fail(`Lock '${id}' is missing.`)
  return { manifest, metadata, locks }
}

function main() {
  const mode = process.argv[2]
  const get = args(process.argv.slice(3))
  const state = collect(get)
  if (mode === "generate") {
    writeFileSync(get("output"), canonical(state.metadata), { encoding: "utf8", flag: "wx" })
  } else if (mode === "validate") {
    const metadata = json(get("metadata"))
    same(metadata, state.metadata, "generated metadata")
    validateDescriptor(json(get("descriptor")), metadata, get("artifact"), state.locks, state.manifest)
  } else fail("Mode must be generate or validate.")
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  try { main() } catch (error) { process.stderr.write(`release metadata: ${error.message}\n`); process.exitCode = 1 }
}

export { canonical, facts, hashFile, LOCKS, PERMISSIONS, SHARED, SLOTS }
