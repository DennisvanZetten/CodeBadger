import net from "node:net";

// No provider URL, credential, permission decision or repository ID is accepted
// from the caller. Those are bound to the authenticated pipe by CodeBadger.
export function createBrokerTransport(pipeName) {
  if (!/^(?:LOCAL\\|Sessions\\[0-9]+\\AppContainerNamedObjects\\S-1-15-2-(?:[0-9]+-)*[0-9]+\\)CodeBadger\.Broker\.[a-f0-9]{32}$/.test(pipeName)) {
    throw new Error("Invalid CodeBadger pipe name.");
  }
  let queue = Promise.resolve();
  let socket;
  let pending;
  let received = Buffer.alloc(0);
  let failed = false;
  let connecting;
  let rejectConnection;
  let failureReason = "CodeBadger provider bridge closed.";
  function fail(reason) {
    if (typeof reason === "string") failureReason = reason;
    failed = true;
    rejectConnection?.(new Error(failureReason));
    rejectConnection = undefined;
    pending?.reject(new Error(failureReason));
    pending = undefined;
    socket?.destroy();
  }
  function connect() {
    if (failed) throw new Error(failureReason);
    if (connecting) return connecting;
    connecting = new Promise((resolve, reject) => {
      rejectConnection = reject;
      socket = net.createConnection(`\\\\.\\pipe\\${pipeName}`);
      socket.once("connect", () => { rejectConnection = undefined; resolve(); });
      socket.on("error", (error) => {
        const code = /^[A-Z0-9_]+$/.test(error.code ?? "") ? error.code : "UNKNOWN";
        fail(`CodeBadger pipe connection failed (${code}). Check the host sandbox error.`);
      });
      socket.on("close", fail);
      socket.on("data", (chunk) => {
        if (!pending) return fail();
        if (received.length + chunk.length > 1024 * 1024 + 4) return fail();
        received = Buffer.concat([received, chunk]);
        if (received.length < 4) return;
        const length = received.readInt32LE(0);
        if (length < 1 || length > 1024 * 1024) return fail();
        if (received.length < length + 4) return;
        if (received.length !== length + 4) return fail();
        try {
          const reply = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(received.subarray(4)));
          const request = pending;
          pending = undefined;
          received = Buffer.alloc(0);
          if (reply.error || !reply.response) request.reject(new Error(
            typeof reply.error === "string" ? reply.error.slice(0, 512) : "CodeBadger rejected the provider request."));
          else request.resolve(reply.response);
        } catch { fail(); }
      });
    });
    return connecting;
  }
  async function complete(body, signal) {
    const work = async () => {
      signal?.throwIfAborted();
      const abort = () => fail("OpenCode canceled the provider request. Restart the sandbox to reconnect.");
      // Includes host approval and the provider's separate five-minute deadline.
      const timeout = setTimeout(() => fail("Timed out waiting for CodeBadger provider bridge (11 minutes)."), 660000);
      signal?.addEventListener("abort", abort, { once: true });
      try {
        await connect();
        signal?.throwIfAborted();
        const bytes = Buffer.from(JSON.stringify({ operation: "complete", body: { ...body, stream: false } }));
        if (bytes.length > 4 * 1024 * 1024) throw new Error("Provider request exceeds limit.");
        const header = Buffer.alloc(4);
        header.writeInt32LE(bytes.length);
        return await new Promise((resolve, reject) => {
          pending = { resolve, reject };
          socket.write(Buffer.concat([header, bytes]));
        });
      } finally {
        clearTimeout(timeout);
        signal?.removeEventListener("abort", abort);
      }
    };
    const result = queue.then(work);
    queue = result.catch(() => {});
    return result;
  }
  return { complete, close: fail };
}

export function completionResponse(completion, stream) {
  if (!Array.isArray(completion?.choices) || !completion.choices.length) throw new Error("Invalid provider completion.");
  if (!stream) return new Response(JSON.stringify(completion), { headers: { "content-type": "application/json" } });
  // The host uses bounded non-streaming HTTP; synthesize the OpenAI SSE shape
  // expected by the OpenCode runtime without adding network authority.
  const common = { id: completion.id, object: "chat.completion.chunk", created: completion.created, model: completion.model };
  const choices = completion.choices.map((choice) => ({ index: choice.index, delta: choice.message, finish_reason: null }));
  const finished = completion.choices.map((choice) => ({ index: choice.index, delta: {}, finish_reason: choice.finish_reason }));
  const chunks = [{ ...common, choices }, { ...common, choices: finished, usage: completion.usage }];
  return new Response(chunks.map((chunk) => `data: ${JSON.stringify(chunk)}\n\n`).join("") + "data: [DONE]\n\n",
    { headers: { "content-type": "text/event-stream" } });
}

export function installBrokerFetch(pipeName) {
  const transport = createBrokerTransport(pipeName);
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (input, init) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    if (url.origin !== "https://codebadger.invalid" || url.pathname !== "/v1/chat/completions" || url.search || request.method !== "POST") {
      throw new Error("Direct network access is disabled in the CodeBadger sandbox.");
    }
    const body = await request.json();
    return completionResponse(await transport.complete(body, request.signal), body.stream === true);
  };
  return () => { globalThis.fetch = originalFetch; transport.close(); };
}
