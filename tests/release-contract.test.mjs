import assert from "node:assert/strict"
import { readFileSync } from "node:fs"
import { join, resolve } from "node:path"
import test from "node:test"
import { buildMetadata, canonical, checkDependencyCommits, validateInput } from "../scripts/release/metadata.mjs"

const root = resolve(import.meta.dirname, "..")
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"))
const producer = readJson("release/producer-input.v1.json")
const manifest = readJson("plugin.json")
const packageJson = readJson("web/package.json")
const redleaf = "1".repeat(40)
const redbamboo = "2".repeat(40)

test("release input names Leaf.Sdk and the RedBamboo inputs, never their commits", () => {
  assert.doesNotThrow(() => validateInput(producer, manifest, packageJson))
  assert.equal("redleafReleaseToolCommit" in producer, false)
  assert.deepEqual(producer.leafSdk, { repository: "RedBamboo-Interactive/redleaf", sourcePath: "src/Leaf.Sdk" })
  assert.deepEqual(Object.keys(producer.redbamboo).sort(), ["inputs", "lockPath", "repository"])
})

test("a committed dependency commit fails closed before release output", () => {
  for (const input of [
    { ...structuredClone(producer), redleafReleaseToolCommit: redleaf },
    { ...structuredClone(producer), leafSdk: { ...producer.leafSdk, commit: redleaf } },
    { ...structuredClone(producer), redbamboo: { ...producer.redbamboo, commit: redbamboo } },
  ]) assert.throws(() => validateInput(input, manifest, packageJson), /builder inputs/)
})

test("the builder-supplied commits must be exact and name the checkouts that were built", () => {
  const checkouts = { leafSdk: redleaf, redleaf, redbamboo }
  assert.doesNotThrow(() => checkDependencyCommits({ redleaf, redbamboo }, checkouts))
  assert.throws(() => checkDependencyCommits({ redleaf: "1".repeat(7), redbamboo }, checkouts), /--redleaf-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo: "A".repeat(40) }, checkouts), /--redbamboo-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, leafSdk: "3".repeat(40) }), /Leaf\.Sdk checkout is at 3{40}, but the builder supplied 1{40}/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redleaf: "3".repeat(40) }), /RedLeaf release tool checkout/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redbamboo: "3".repeat(40) }), /RedBamboo checkout/)
})

test("metadata records the builder-supplied commits where the signed record reads them", () => {
  const metadata = JSON.parse(canonical(buildMetadata(producer, manifest, {
    mailCommit: "0".repeat(40),
    mailUrl: "https://github.com/RedBamboo-Interactive/leaf-mail",
    leafSdkUrl: "https://github.com/RedBamboo-Interactive/redleaf",
    redbambooUrl: "https://github.com/RedBamboo-Interactive/redbamboo-packages",
    redleafCommit: redleaf,
    redbambooCommit: redbamboo,
    builtAt: "2026-08-08T09:00:00.000Z",
  })))
  assert.equal(metadata.toolchain.releaseTool, redleaf)
  assert.deepEqual(Object.fromEntries(metadata.buildInputs.map((item) => [item.id, item.commit])), {
    "leaf-sdk": redleaf,
    ...Object.fromEntries(producer.redbamboo.inputs.map((item) => [item.id, redbamboo])),
  })
})
