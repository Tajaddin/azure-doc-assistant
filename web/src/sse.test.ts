import { describe, expect, it } from "vitest";
import { parseSseChunk } from "./sse";

describe("parseSseChunk", () => {
  it("parses complete blocks and keeps the partial tail", () => {
    const { events, rest } = parseSseChunk('event: token\ndata: {"text":"Hi"}\n\nevent: done\ndata: {"a":1}\n\nevent: tok');
    expect(events).toEqual([
      { event: "token", data: { text: "Hi" } },
      { event: "done", data: { a: 1 } },
    ]);
    expect(rest).toBe("event: tok");
  });

  it("accepts CRLF and non-JSON data", () => {
    const { events } = parseSseChunk("event: note\r\ndata: plain text\r\n\r\n");
    expect(events).toEqual([{ event: "note", data: "plain text" }]);
  });

  it("ignores blocks without data", () => {
    expect(parseSseChunk(": keep-alive\n\n").events).toEqual([]);
  });
});
