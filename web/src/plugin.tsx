import type { LeafAppPlugin } from "@redbamboo/utility"
import { MailSettings } from "./Settings"

export const plugin: LeafAppPlugin = {
  id: "mail",
  settingsPanel: { Component: MailSettings },
}
