import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createBrokerTransport, completionResponse } from "./provider-transport.mjs";

const home = process.env.HOME;
const runtime = path.dirname(fileURLToPath(import.meta.url));
for (const key of ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME"]) process.env[key] = home;
const transport = createBrokerTransport(JSON.parse(fs.readFileSync(path.join(runtime, "chat.json"), "utf8")).pipeName);
const config = JSON.parse(fs.readFileSync(path.join(home, "opencode.json"), "utf8"));
const provider = createBrokerTransport(config.plugins[0].options.pipeName);
async function brokerFetch(input, init) {
  const request = new Request(input, init);
  const url = new URL(request.url);
  if (url.origin !== "https://codebadger.invalid" || url.pathname !== "/v1/chat/completions" || url.search || request.method !== "POST")
    throw new Error("Direct HTTP requests are blocked.");
  const body = await request.json();
  return completionResponse(await provider.complete(body, request.signal), body.stream === true);
}
const pending = [];
let busy = false;
let failed = false;
function emit(type, text = "") {
  if (pending.length >= 128) { failed = true; throw new Error("SDK event queue exceeded its bound."); }
  pending.push({ type, text: String(text).slice(0, 32000) });
}
const { OpenCode } = await import("@opencode/sdk");
const host = await OpenCode.create({
  config: { directory: home, project: false, claude: false, file: path.join(home, "opencode.json") },
  models: { fetch: false }, fs: { filewatcher: false, fff: false },
  fetch: brokerFetch,
  log: { level: "error", emit: () => emit("error", "OpenCode SDK reported an internal error. Stop and restart the sandbox.") }
});
try {
  if ((await host.server.info()).urls.length !== 0) throw new Error("SDK opened an unexpected listener.");
  const session = await host.sessions.create({ location: { directory: process.cwd() } });
  await host.sessions.update({ sessionID: session.id, permissions: [
    { action: "shell", resource: "*", effect: "deny" },
    { action: "edit", resource: "*", effect: "allow" }
  ] });
  emit("ready");
  const seen = new Set();
  async function run(prompt) {
    busy = true; emit("busy");
    try {
      for (const message of await host.sessions.context({ sessionID: session.id })) seen.add(message.id);
      await host.sessions.prompt({ sessionID: session.id, text: prompt });
      const deadline = Date.now() + 15 * 60 * 1000;
      let complete = false;
      while (!complete && Date.now() < deadline) {
        for (const message of await host.sessions.context({ sessionID: session.id })) {
          if (seen.has(message.id)) continue;
          if (message.type === "idle") { seen.add(message.id); complete = true; continue; }
          if (message.type !== "assistant" || !message.time.completed) continue;
          seen.add(message.id);
          for (const part of message.content ?? []) {
            if (part.type === "text") emit("text", part.text);
            if (part.type === "tool") emit("tool", `${part.name}: ${part.state.status}${part.state.error ? " — " + part.state.error.message : ""}`);
          }
        }
        if (!complete) await new Promise(resolve => setTimeout(resolve, 150));
      }
      if (!complete) { await host.sessions.interrupt({ sessionID: session.id, resume: false }); throw new Error("SDK turn exceeded 15 minutes."); }
    } catch { emit("error", "SDK request failed. Check provider permissions or stop and restart the sandbox."); }
    finally { busy = false; emit("idle"); }
  }
  while (!failed) {
    const reply = await transport.complete({ events: pending.splice(0, 32), acceptPrompt: !busy });
    if (reply.prompt !== null && reply.prompt !== undefined) {
      if (busy || typeof reply.prompt !== "string" || reply.prompt.length > 32000) throw new Error("Invalid host prompt.");
      void run(reply.prompt);
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
} finally { transport.close(); provider.close(); await host.close(); }
