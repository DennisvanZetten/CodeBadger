// Listenerless synthetic smoke test; no provider credentials are loaded or used.
import fs from "node:fs";
import path from "node:path";
const home = process.env.CODEBADGER_SDK_PROBE_HOME ?? (process.argv.includes("--sandbox") ? process.env.HOME : undefined);
if (!home || !path.isAbsolute(home)) throw new Error("A private synthetic home is required.");
for (const variable of ["HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME"])
  process.env[variable] = home;
process.env.OPENCODE_CONFIG = path.join(home, "opencode.json");
process.env.OPENCODE_CONFIG_PROJECT_DISABLE = "1";
process.env.OPENCODE_DISABLE_MODELS_FETCH = "1";
fs.mkdirSync(home, { recursive: true });
if (!fs.existsSync(process.env.OPENCODE_CONFIG))
  fs.writeFileSync(process.env.OPENCODE_CONFIG, JSON.stringify({ snapshots: false, mcp: { servers: {} } }));
try {
  if (process.argv.includes("--security")) {
    for (const filename of ["unselected.txt", ".env", "../outside.txt"]) {
      try {
        fs.readFileSync(path.resolve(process.cwd(), filename));
        throw new Error(`Unreviewed test file was readable: ${filename}`);
      } catch (error) {
        if (!["EACCES", "EPERM"].includes(error.code)) throw error;
      }
    }
    console.log("SDK_FILES_DENIED");
  }
  const { OpenCode } = await import("@opencode/sdk");
  const host = await OpenCode.create({
    config: { directory: home, project: false, claude: false, file: process.env.OPENCODE_CONFIG },
    models: { fetch: false }, fs: { filewatcher: false, fff: false },
    log: { level: "error", emit: entry => console.error("SDK_LOG", JSON.stringify(entry)) }
  });
  try {
    const info = await host.server.info();
    if (info.urls.length !== 0) throw new Error("Embedded SDK unexpectedly opened a listener.");
    console.log("SDK_READY", JSON.stringify(info));
    if (process.argv.includes("--prompt")) {
      const session = await host.sessions.create({ location: { directory: process.cwd() } });
      console.log("SDK_SESSION", session.id);
      const result = await host.sessions.prompt({ sessionID: session.id, text: "Reply OK without using tools" });
      console.log("SDK_ENQUEUED", result.id);
      const until = Date.now() + 30000;
      let answer;
      while (Date.now() < until) {
        const messages = await host.sessions.context({ sessionID: session.id });
        answer = messages.find(message => JSON.stringify(message).includes("SDK_PROVIDER_OK"));
        if (answer) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      if (!answer) throw new Error("No fake provider response within 30 seconds.");
      console.log("SDK_ANSWER", JSON.stringify(answer));
      for (const message of await host.sessions.context({ sessionID: session.id }))
        for (const item of message.content ?? [])
          if (item.type === "tool") console.log("SDK_TOOL", item.name, item.state.status, item.state.error?.message ?? "");
    }
  } finally { await host.close(); }
} catch (error) {
  console.error(error);
  process.exitCode = 1;
}
