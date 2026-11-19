import assert from "node:assert/strict";
import net from "node:net";
import { randomUUID } from "node:crypto";
import { createBrokerTransport, completionResponse } from "../Resources/Raw/sandbox/provider-transport.mjs";

assert.throws(() => createBrokerTransport("invalid"));
const pipeName = `LOCAL\\CodeBadger.Broker.${randomUUID().replaceAll("-", "")}`;
const server = net.createServer((socket) => {
  let bytes = Buffer.alloc(0);
  socket.on("data", (data) => {
    bytes = Buffer.concat([bytes, data]);
    if (bytes.length < 4) return;
    const length = bytes.readInt32LE(0);
    if (bytes.length < length + 4) return;
    const request = JSON.parse(bytes.subarray(4, length + 4));
    bytes = bytes.subarray(length + 4);
    assert.equal(request.operation, "complete");
    assert.equal(request.body.stream, false);
    const body = Buffer.from(JSON.stringify({ response: { id: "test", model: "test-model", choices: [{ index: 0, message: { role: "assistant", content: "test response" }, finish_reason: "stop" }] } }));
    const header = Buffer.alloc(4);
    header.writeInt32LE(body.length);
    // Fragmented frames exercise the client's receive buffering.
    socket.write(header.subarray(0, 2));
    socket.write(Buffer.concat([header.subarray(2), body]));
  });
});
await new Promise((resolve) => server.listen(`\\\\.\\pipe\\${pipeName}`, resolve));
const transport = createBrokerTransport(pipeName);
try {
  const [first, second] = await Promise.all([
    transport.complete({ model: "test-model", messages: [], stream: true }),
    transport.complete({ model: "test-model", messages: [] }),
  ]);
  assert.equal(first.choices[0].message.content, "test response");
  assert.equal(second.model, "test-model");
  assert.match(await completionResponse(first, true).text(), /data: \[DONE\]/);
  assert.equal((await completionResponse(first, false).json()).id, "test");
  console.log("Provider pipe transport and SSE adaptation tests passed.");
} finally {
  transport.close();
  await new Promise((resolve) => server.close(resolve));
}
