import type { EntityPickerItem, EntityPickerSource } from "@redbamboo/ui"

/** One agent as returned by Mail's owner-only GET /agents (includes the owner's confidential agents). */
export type MailAgent = {
  id: string
  slug: string
  name: string
  avatar?: string | null
  confidential?: boolean
}

type ShellSource = Pick<EntityPickerSource, "search" | "resolve"> | null

export function agentItem(agent: MailAgent): EntityPickerItem {
  return {
    entity: { id: agent.id, typeSlug: "agent", name: agent.name },
    slug: agent.slug || undefined,
    visual: agent.avatar
      ? { src: agent.avatar, fallbackText: agent.name, shape: "circle" }
      : { icon: "ph-bold ph-user-circle", fallbackText: agent.name, shape: "circle" },
    subtitle: agent.confidential ? "Confidential agent" : undefined,
  }
}

function abortError() {
  return typeof DOMException !== "undefined" ? new DOMException("Aborted", "AbortError") : new Error("Aborted")
}

/**
 * Per-field picker source for the bound agent. The shell's entity listing hides
 * confidential agents, but Mail may be bound to one of the owner's, so:
 *  - search: shell results, merged by id with Mail's /agents (filtered by name);
 *  - resolve: Mail's /agents match first, else the shell. A null is returned only
 *    when the answer is authoritative; if Mail's list was unavailable and the shell
 *    has no match, the failure is transient and the call throws so the field retries.
 * `subscribe` is left to the shell provider. The shell is read at call time.
 */
export function createMailAgentSource(
  loadAgents: (signal: AbortSignal) => Promise<MailAgent[]>,
  shell: () => ShellSource,
): Pick<EntityPickerSource, "search" | "resolve"> {
  return {
    async search(request, signal) {
      const host = shell()
      if (request.type !== "agent") return host ? [...await host.search(request, signal)] : []
      const [fromShell, fromMail] = await Promise.allSettled([
        host ? host.search(request, signal) : Promise.resolve([] as readonly EntityPickerItem[]),
        loadAgents(signal),
      ])
      if (signal.aborted) throw abortError()
      if (fromShell.status === "rejected" && fromMail.status === "rejected") throw fromMail.reason
      const items = new Map<string, EntityPickerItem>()
      if (fromShell.status === "fulfilled") for (const item of fromShell.value) items.set(item.entity.id, item)
      if (fromMail.status === "fulfilled") {
        const query = request.query.trim().toLowerCase()
        for (const agent of fromMail.value)
          if (!items.has(agent.id) && (!query || agent.name.toLowerCase().includes(query)))
            items.set(agent.id, agentItem(agent))
      }
      return [...items.values()].slice(0, request.limit)
    },

    async resolve(request, signal) {
      const host = shell()
      if (request.type !== "agent") return host ? host.resolve(request, signal) : null
      let mine: MailAgent[] | null = null
      try { mine = await loadAgents(signal) }
      catch (error) { if (signal.aborted) throw error }
      const hit = mine?.find((agent) => agent.id === request.id || (!!agent.slug && agent.slug === request.id))
      if (hit) return agentItem(hit)
      if (host) {
        const item = await host.resolve(request, signal) // throws on transient failure
        if (item || mine) return item
      } else if (mine) {
        return null
      }
      throw new Error("Mail's agent list is unavailable")
    },
  }
}
