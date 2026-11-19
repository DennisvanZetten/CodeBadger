import { Plugin } from "@opencode/plugin";
import { createBrokerTransport, completionResponse } from "./provider-transport.mjs";

// Must be staged by CodeBadger with a host-bound pipe name. This plugin grants
// no OS capabilities. AppContainer still denies network if it is unloaded or
// if a provider bypasses the JS fetch interception.
export default Plugin.define({
  id: "codebadger.sandbox-provider",
  async setup(ctx) {
    const transport = createBrokerTransport(ctx.options.pipeName);
    await ctx.session.hook("http.request", async (event) => {
      const request = event.request;
      const url = new URL(request.url);
      if (url.origin !== "https://codebadger.invalid" || url.pathname !== "/v1/chat/completions" || request.method !== "POST") {
        throw new Error("Only CodeBadger provider requests are supported.");
      }
      const body = await request.clone().json();
      const completion = await transport.complete(body, request.signal);
      const response = completionResponse(completion, body.stream === true);
      // Experimental transport adaptation: the native HTTP runtime must prove
      // it accepts data responses in integration tests before production use.
      const bytes = Buffer.from(await response.arrayBuffer());
      event.request = new Request(`data:${response.headers.get("content-type")};base64,${bytes.toString("base64")}`);
    }, { providerID: "codebadger" });
    return () => transport.close();
  },
});
