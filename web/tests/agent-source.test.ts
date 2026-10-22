import { test } from "node:test"
import assert from "node:assert/strict"
import { createMailAgentSource, type MailAgent } from "../src/agentSource.ts"

const item = (id: string, name: string) => ({ entity: { id, typeSlug: "agent", name }, subtitle: "shell" })
const nova = item("a1", "Nova")
const scout = item("a2", "Scout")
const mailAgents: MailAgent[] = [
  { id: "a1", slug: "nova", name: "Nova", avatar: "/api/assets/n.png" },
  { id: "c1", slug: "diary", name: "Diary Keeper", confidential: true, avatar: null },
]

function shell(overrides: Partial<{ search: Function; resolve: Function }> = {}) {
  const calls: string[] = []
  return {
    calls,
    source: {
      search: async (req: { query: string }) => { calls.push(`search:${req.query}`); return overrides.search ? overrides.search(req) : [nova, scout] },
      resolve: async (req: { id: string }) => { calls.push(`resolve:${req.id}`); return overrides.resolve ? overrides.resolve(req) : [nova, scout].find(i => i.entity.id === req.id) ?? null },
    },
  }
}

const signal = () => new AbortController().signal

test("search merges shell results by id with Mail's confidential agents", async () => {
  const host = shell()
  const source = createMailAgentSource(async () => mailAgents, () => host.source as never)
  const items = await source.search({ type: "agent", query: "", limit: 50 }, signal())
  assert.deepEqual(items.map(i => i.entity.id), ["a1", "a2", "c1"])
  assert.equal(items[0].subtitle, "shell", "shell presentation wins for agents both list")
  assert.equal(items[2].subtitle, "Confidential agent")
})

test("search filters Mail-only agents by name and respects the limit", async () => {
  const host = shell({ search: () => [] })
  const source = createMailAgentSource(async () => mailAgents, () => host.source as never)
  assert.deepEqual((await source.search({ type: "agent", query: "diary", limit: 50 }, signal())).map(i => i.entity.id), ["c1"])
  assert.deepEqual((await source.search({ type: "agent", query: "zzz", limit: 50 }, signal())), [])
  assert.equal((await source.search({ type: "agent", query: "", limit: 1 }, signal())).length, 1)
})

test("search survives one failing side and throws when both fail", async () => {
  const failing = shell({ search: () => { throw new Error("shell down") } })
  const viaMail = createMailAgentSource(async () => mailAgents, () => failing.source as never)
  assert.deepEqual((await viaMail.search({ type: "agent", query: "", limit: 50 }, signal())).map(i => i.entity.id), ["a1", "c1"])
  const viaShell = createMailAgentSource(async () => { throw new Error("401") }, () => shell().source as never)
  assert.deepEqual((await viaShell.search({ type: "agent", query: "", limit: 50 }, signal())).map(i => i.entity.id), ["a1", "a2"])
  const both = createMailAgentSource(async () => { throw new Error("401") }, () => failing.source as never)
  await assert.rejects(both.search({ type: "agent", query: "", limit: 50 }, signal()))
})

test("resolve prefers Mail's list, so a confidential bound agent resolves", async () => {
  const host = shell()
  const source = createMailAgentSource(async () => mailAgents, () => host.source as never)
  const confidential = await source.resolve({ type: "agent", id: "c1" }, signal())
  assert.equal(confidential?.entity.name, "Diary Keeper")
  assert.equal((await source.resolve({ type: "agent", id: "nova" }, signal()))?.entity.id, "a1", "slug references match too")
  assert.deepEqual(host.calls, [], "the shell is not asked when Mail knows the agent")
})

test("resolve falls back to the shell and returns null only for an authoritative miss", async () => {
  const host = shell()
  const source = createMailAgentSource(async () => mailAgents, () => host.source as never)
  assert.equal((await source.resolve({ type: "agent", id: "a2" }, signal()))?.entity.name, "Scout")
  assert.equal(await source.resolve({ type: "agent", id: "gone" }, signal()), null)

  const mailDown = createMailAgentSource(async () => { throw new Error("503") }, () => shell().source as never)
  assert.equal((await mailDown.resolve({ type: "agent", id: "a2" }, signal()))?.entity.name, "Scout")
  await assert.rejects(mailDown.resolve({ type: "agent", id: "c1" }, signal()), /unavailable/,
    "Mail unavailable + shell miss is transient, not 'missing'")

  const shellTransient = createMailAgentSource(async () => mailAgents, () => shell({ resolve: () => { throw new Error("timeout") } }).source as never)
  await assert.rejects(shellTransient.resolve({ type: "agent", id: "a2" }, signal()), /timeout/)
})

test("non-agent types and abort pass straight through", async () => {
  const host = shell()
  const source = createMailAgentSource(async () => mailAgents, () => host.source as never)
  await source.search({ type: "flow", query: "x", limit: 5 }, signal())
  assert.deepEqual(host.calls, ["search:x"])
  const controller = new AbortController()
  const pending = source.search({ type: "agent", query: "", limit: 5 }, controller.signal)
  controller.abort()
  await assert.rejects(pending, (error: Error) => error.name === "AbortError")
})
