import { useCallback, useEffect, useRef, useState, type ComponentProps, type ReactNode } from "react"
import {
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  EntityCard,
  Input,
  ScrollArea,
  SectionHeader,
  SettingRow,
  Switch,
  useEntityCardPresentation,
  type EntityCardEntity,
} from "@redbamboo/ui"
import { get, post, put } from "./api"

const BASE = "/api/extensions/mail"

type Status = {
  connected: boolean
  status: "connected" | "needs_reauth" | "disconnected"
  email?: string | null
  token_health?: string | null
  last_sync_at?: string | null
  last_error?: string | null
  granted_scope?: string | null
  inbox_unread?: number | null
  backfill_complete?: boolean
  client_configured: boolean
  owner_configured: boolean
  agent_id?: string | null
  live_approved: boolean
  live_approved_at?: string | null
  live_excluded_categories: string[]
}
type Agent = { id: string; slug: string; name: string }

const CATEGORIES: Array<{ id: string; label: string }> = [
  { id: "CATEGORY_PROMOTIONS", label: "Promotions" },
  { id: "CATEGORY_SOCIAL", label: "Social" },
  { id: "CATEGORY_FORUMS", label: "Forums" },
  { id: "CATEGORY_UPDATES", label: "Updates" },
]

const message = (cause: unknown, fallback: string) => cause instanceof Error ? cause.message : fallback

function when(value?: string | null) {
  if (!value) return "never"
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function statusLabel(status: Status | null) {
  if (!status) return "Checking..."
  if (status.status === "connected") return status.connected ? "Connected (read-only)" : "Connected, waiting for first sync"
  if (status.status === "needs_reauth") return "Needs reconnecting"
  return "Not connected"
}

export function MailSettings() {
  const [status, setStatus] = useState<Status | null>(null)
  const [agents, setAgents] = useState<Agent[]>([])
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [authUrl, setAuthUrl] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const [redirectUrl, setRedirectUrl] = useState("")
  const [agentPickerOpen, setAgentPickerOpen] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)

  const reload = useCallback(async () => {
    try {
      const next = await get<Status>(`${BASE}/status`)
      setStatus(next)
      setError(null)
      return next
    } catch (cause) {
      setError(message(cause, "Could not load Mail status"))
      return null
    }
  }, [])

  useEffect(() => {
    void reload()
    void get<{ items: Agent[] }>(`${BASE}/agents`).then((value) => setAgents(value.items)).catch(() => setAgents([]))
  }, [reload])

  // While a consent attempt is open, watch for the loopback redirect to complete it.
  useEffect(() => {
    if (!pending) return
    const started = Date.now()
    const timer = window.setInterval(async () => {
      const next = await reload()
      if (next?.status === "connected" || Date.now() - started > 10 * 60_000) {
        setPending(false)
        setAuthUrl(null)
        if (next?.status === "connected") setNote("Connected.")
      }
    }, 3000)
    return () => window.clearInterval(timer)
  }, [pending, reload])

  const run = async (action: () => Promise<unknown>, done?: string) => {
    setBusy(true)
    setError(null)
    setNote(null)
    try {
      await action()
      if (done) setNote(done)
      await reload()
    } catch (cause) {
      setError(message(cause, "Request failed"))
    } finally {
      setBusy(false)
    }
  }

  const uploadClient = async (file: File) => {
    const text = await file.text()
    await run(() => put(`${BASE}/client`, { json: text }), "OAuth client saved.")
    if (fileInput.current) fileInput.current.value = ""
  }

  const connect = async () => {
    // Open synchronously so the popup is tied to the click, then point it at Google.
    const popup = window.open("about:blank", "_blank")
    if (popup) popup.opener = null
    await run(async () => {
      const started = await post<{ authUrl: string }>(`${BASE}/oauth/start`)
      setAuthUrl(started.authUrl)
      setPending(true)
      if (popup) popup.location.href = started.authUrl
    })
  }

  const complete = () => run(async () => {
    await post(`${BASE}/oauth/complete`, { redirectUrl: redirectUrl.trim() })
    setRedirectUrl("")
    setPending(false)
    setAuthUrl(null)
  }, "Connected.")

  const disconnect = () => {
    if (!window.confirm("Disconnect Gmail? The grant is revoked at Google and the stored token is deleted.")) return
    void run(() => post(`${BASE}/disconnect`), "Disconnected.")
  }

  const saveConfig = (values: Record<string, unknown>) => run(() => put(`${BASE}/config`, values), "Saved.")

  const chooseAgent = async (agentId: string | null) => {
    setAgentPickerOpen(false)
    await saveConfig({ agent_id: agentId })
  }

  const boundAgentId = status?.agent_id ?? null
  const boundAgent = boundAgentId
    ? agents.find((agent) => agent.id === boundAgentId) ?? { id: boundAgentId, slug: "", name: "Unknown agent" }
    : null

  const toggleCategory = (id: string, excluded: boolean) => {
    const current = new Set(status?.live_excluded_categories ?? [])
    if (excluded) current.add(id)
    else current.delete(id)
    void saveConfig({ live_excluded_categories: [...current] })
  }

  return <div>
    <p aria-live="polite" className={`mb-2 text-xs ${error ? "text-accent-red" : "text-accent-teal"}`}>{error ?? note ?? ""}</p>

    <SectionHeader>Mail</SectionHeader>
    <div className="mail-status">
      <strong>{statusLabel(status)}</strong>
      {status?.email && <span>{status.email}{typeof status.inbox_unread === "number" ? ` · ${status.inbox_unread} unread in Inbox` : ""}</span>}
      <span>Last sync: {when(status?.last_sync_at)}{status?.token_health ? ` · token: ${status.token_health}` : ""}</span>
      {status?.last_error && <span className="mail-warning">{status.last_error}</span>}
    </div>
    <p className="mb-3 text-xs text-text-muted leading-relaxed">
      Mail gives you and your bound agent read-only access to one Gmail account. It requests only
      the <code>gmail.readonly</code> scope, so it cannot send, change, or delete mail. Message bodies are fetched on
      demand and never stored; only message ids are kept (30 days) to track sync.
    </p>

    <SectionHeader>1 · Google Cloud setup</SectionHeader>
    <ol className="mail-checklist">
      <li>In Google Cloud Console, create a project of your own.</li>
      <li>APIs &amp; Services → Library: enable the <strong>Gmail API</strong>.</li>
      <li>Google Auth Platform → <strong>Branding</strong>: fill in the app name, support email, <strong>home page URL</strong> and
        <strong> privacy policy URL</strong> (the repository URL and <code>…/leaf-mail#privacy-model</code> work).
        <strong> Publish app</strong> only becomes available once Branding is complete.</li>
      <li><strong>Audience</strong>: User type External, then <strong>Publish app → In production</strong>. It stays unverified, which is fine for personal use.</li>
      <li><strong>Data Access</strong>: <strong>leave it empty</strong>. Adding <code>gmail.readonly</code> there opens Google's
        verification form, which a personal install does not need. Mail requests the scope when you connect.</li>
      <li><strong>Clients</strong>: Create client → Application type <strong>Desktop app</strong>, then download the JSON.</li>
    </ol>
    <p className="mail-warning mb-3">
      If the app stays in Testing mode, Google expires its tokens after 7 days and Mail will ask you to reconnect weekly.
      During consent, Google warns once that the app is unverified: choose Advanced → continue, since it is your own project.
    </p>

    <SectionHeader>2 · OAuth client</SectionHeader>
    <SettingRow label="Desktop client JSON" hint={status?.client_configured ? "A client is configured. Upload again to replace it (disconnect first if it is a different client)." : "Upload the client_secret_….json file you downloaded. Web clients are rejected."}>
      <div className="flex items-center gap-2">
        <span className="text-xs text-text-muted">{status?.client_configured ? "Configured" : "Not configured"}</span>
        <input ref={fileInput} type="file" accept="application/json,.json" className="hidden"
          onChange={(event) => { const file = event.target.files?.[0]; if (file) void uploadClient(file) }} />
        <Button variant="outline" size="sm" disabled={busy} onClick={() => fileInput.current?.click()}>Upload JSON</Button>
      </div>
    </SettingRow>

    <SectionHeader>3 · Agent</SectionHeader>
    <p className="mb-2 text-xs text-text-muted leading-relaxed">
      Only this agent can read mail (from your private discussions) and receive Mail context and LIVE notices.
    </p>
    <div className="mb-3">
      {boundAgent
        ? <AgentCard agent={boundAgent} subtitle="Bound agent"
            actions={<Button variant="outline" size="sm" disabled={busy || !status} onClick={() => setAgentPickerOpen(true)}>Change</Button>} />
        : <Button variant="outline" className="w-full" disabled={busy || !status} onClick={() => setAgentPickerOpen(true)}>Choose agent</Button>}
    </div>
    <AgentPicker open={agentPickerOpen} agents={agents} selectedId={boundAgentId ?? undefined} busy={busy}
      onOpenChange={setAgentPickerOpen} onSelect={(agent) => void chooseAgent(agent.id)}
      onUnbind={boundAgentId ? () => void chooseAgent(null) : undefined} />

    <SectionHeader>4 · Connect</SectionHeader>
    <SettingRow label="Gmail account" hint="Opens Google's consent page in a new window. Keep this tab open until it reports Connected.">
      <div className="flex items-center gap-2">
        {status?.status !== "disconnected" && status &&
          <Button variant="outline" size="sm" disabled={busy} onClick={disconnect}>Disconnect</Button>}
        <Button size="sm" disabled={busy || !status?.client_configured} onClick={() => void connect()}>
          {status?.status === "connected" ? "Reconnect" : "Connect"}
        </Button>
      </div>
    </SettingRow>
    {authUrl && <div className="mail-card">
      <p className="mail-muted mb-2">
        Waiting for Google... If the window did not open, <a href={authUrl} target="_blank" rel="noopener noreferrer" className="underline">open the consent page</a>.
        The request expires in 10 minutes.
      </p>
      <p className="mail-muted mb-1">
        Finished consent on another device? Its browser ends on a page at <code>http://127.0.0.1:…</code> that fails to
        load. Copy that full address and paste it here:
      </p>
      <div className="flex items-center gap-2">
        <Input value={redirectUrl} placeholder="http://127.0.0.1:…/?state=…&code=…" onChange={(event) => setRedirectUrl(event.target.value)} />
        <Button variant="outline" size="sm" disabled={busy || !redirectUrl.trim()} onClick={() => void complete()}>Complete</Button>
      </div>
    </div>}

    <SectionHeader>LIVE notices</SectionHeader>
    <SettingRow label="Show new mail in LIVE (sender and subject only)"
      hint="Off by default. When on, new Inbox mail appears in your LIVE timeline as the sender name and subject, quoted as external content (more than three at once are combined). Your agent sees LIVE in conversations and heartbeats. Message bodies are never shown there. Only you can change this, from this page.">
      <Switch checked={status?.live_approved === true} disabled={busy || !status}
        onCheckedChange={(value) => void saveConfig({ live_approved: value })} />
    </SettingRow>
    {CATEGORIES.map((category) =>
      <SettingRow key={category.id} label={`Skip ${category.label}`}>
        <Switch checked={status?.live_excluded_categories.includes(category.id) === true} disabled={busy || !status || !status.live_approved}
          onCheckedChange={(value) => toggleCategory(category.id, value)} />
      </SettingRow>)}
  </div>
}

/** One agent as an entity card; the host presentation bridge supplies the avatar when available. */
function AgentCard({ agent, subtitle, selected, action, trailing, actions }: {
  agent: Agent
  subtitle: string
  selected?: boolean
  action?: ComponentProps<typeof EntityCard>["action"]
  trailing?: ReactNode
  actions?: ReactNode
}) {
  const fallback: EntityCardEntity = { id: agent.id, typeSlug: "agent", name: agent.name }
  const presentation = useEntityCardPresentation(fallback)
  const entity = presentation?.entity ?? fallback
  return <EntityCard
    entity={entity}
    visual={presentation?.visual ?? { icon: "ph-bold ph-user-circle", fallbackText: entity.name, shape: "circle" }}
    subtitle={subtitle}
    variant="outlined"
    selected={selected}
    action={action}
    trailing={trailing}
    actions={actions}
  />
}

function AgentPicker({ open, agents, selectedId, busy, onOpenChange, onSelect, onUnbind }: {
  open: boolean
  agents: Agent[]
  selectedId?: string
  busy: boolean
  onOpenChange: (open: boolean) => void
  onSelect: (agent: Agent) => void
  onUnbind?: () => void
}) {
  return <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Choose an Agent</DialogTitle>
        <DialogDescription>Mail will be readable only by this Agent, from your private discussions.</DialogDescription>
      </DialogHeader>
      <ScrollArea className="max-h-96">
        <div className="space-y-2 pr-2">
          {agents.length === 0 && <p className="text-xs text-text-muted">No agents are available.</p>}
          {agents.map((agent) => <AgentCard
            key={agent.id}
            agent={agent}
            subtitle="Leaf Agent"
            selected={agent.id === selectedId}
            action={{ kind: "button", ariaLabel: `Select ${agent.name}`, onActivate: () => { if (!busy) onSelect(agent) } }}
            trailing={agent.id === selectedId ? <Badge variant="secondary">Current</Badge> : undefined}
          />)}
        </div>
      </ScrollArea>
      {onUnbind && <div className="flex justify-end">
        <Button variant="ghost" size="sm" disabled={busy} onClick={onUnbind}>Unbind agent</Button>
      </div>}
    </DialogContent>
  </Dialog>
}
