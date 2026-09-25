import {
  isValidUnicode,
  LIMITS,
  parseInbound,
  parseOutbound,
  ProtocolError,
  type Inbound,
  type Outbound,
} from "./protocol.js";

const MAX_DEPTH = 16;
const utf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
const encoder = new TextEncoder();

function invalid(): never {
  throw new ProtocolError("InvalidFrame", "Invalid protocol frame.");
}

// Scan before JSON.parse: revivers cannot detect duplicate properties, and
// JSON.parse alone accepts unpaired UTF-16 surrogates.
class JsonScanner {
  private offset = 0;

  constructor(private readonly input: string) {}

  validate(): void {
    this.whitespace();
    if (this.input[this.offset] !== "{") invalid();
    this.value(0);
    this.whitespace();
    if (this.offset !== this.input.length) invalid();
  }

  private whitespace(): void {
    while (this.input[this.offset] === " " || this.input[this.offset] === "\t") {
      this.offset++;
    }
  }

  private string(): string {
    if (this.input[this.offset++] !== '"') return invalid();
    const start = this.offset - 1;
    while (this.offset < this.input.length) {
      const character = this.input[this.offset++];
      if (character === '"') {
        const value: unknown = JSON.parse(this.input.slice(start, this.offset));
        if (typeof value !== "string" || !isValidUnicode(value)) invalid();
        return value;
      }
      if (character === "\\") this.offset++;
    }
    return invalid();
  }

  private value(depth: number): void {
    this.whitespace();
    const character = this.input[this.offset];
    if (character === "{" || character === "[") {
      if (depth >= MAX_DEPTH) invalid();
      this.container(character, depth + 1);
      return;
    }
    if (character === '"') {
      this.string();
      return;
    }
    const remaining = this.input.slice(this.offset);
    const literal = /^(?:true|false|null|0|[1-9][0-9]*)/.exec(remaining);
    if (literal === null) invalid();
    if (/^[0-9]/.test(literal[0]) && !Number.isSafeInteger(Number(literal[0]))) {
      invalid();
    }
    this.offset += literal[0].length;
  }

  private container(open: "{" | "[", depth: number): void {
    const close = open === "{" ? "}" : "]";
    const keys = new Set<string>();
    this.offset++;
    this.whitespace();
    if (this.input[this.offset] === close) {
      this.offset++;
      return;
    }
    for (;;) {
      this.whitespace();
      if (open === "{") {
        const key = this.string();
        if (keys.has(key)) invalid();
        keys.add(key);
        this.whitespace();
        if (this.input[this.offset++] !== ":") invalid();
      }
      this.value(depth);
      this.whitespace();
      const separator = this.input[this.offset++];
      if (separator === close) return;
      if (separator !== ",") invalid();
    }
  }
}

export function decodeFrame(bytes: Uint8Array): unknown {
  if (
    bytes.length === 0 ||
    bytes.length > LIMITS.maxFrameBytes ||
    bytes.includes(10) ||
    bytes.includes(13)
  ) {
    return invalid();
  }
  try {
    const input = utf8.decode(bytes);
    new JsonScanner(input).validate();
    return JSON.parse(input) as unknown;
  } catch {
    return invalid();
  }
}

export function encodeFrame(value: Inbound | Outbound): Uint8Array {
  switch (value.tag) {
    case "hello":
    case "request":
    case "cancel":
    case "shutdown":
      parseInbound(value);
      break;
    default:
      parseOutbound(value);
  }
  const bytes = encoder.encode(JSON.stringify(value));
  decodeFrame(bytes);
  const frame = new Uint8Array(bytes.length + 1);
  frame.set(bytes);
  frame[bytes.length] = 10;
  return frame;
}

export class FrameDecoder {
  private readonly pending = new Uint8Array(LIMITS.maxFrameBytes);
  private length = 0;
  private ended = false;
  private failed = false;

  push(chunk: Uint8Array): unknown[] {
    if (this.ended || this.failed) return invalid();
    const frames: unknown[] = [];
    try {
      let offset = 0;
      while (offset < chunk.length) {
        const newline = chunk.indexOf(10, offset);
        const end = newline === -1 ? chunk.length : newline;
        const part = chunk.subarray(offset, end);
        if (this.length + part.length > LIMITS.maxFrameBytes || part.includes(13)) {
          invalid();
        }
        if (newline !== -1 && this.length === 0) {
          frames.push(decodeFrame(part));
        } else {
          this.pending.set(part, this.length);
          this.length += part.length;
          if (newline !== -1) {
            frames.push(decodeFrame(this.pending.subarray(0, this.length)));
            this.length = 0;
          }
        }
        offset = end + 1;
      }
      return frames;
    } catch {
      this.failed = true;
      return invalid();
    }
  }

  end(): void {
    this.ended = true;
    if (this.failed || this.length !== 0) {
      this.failed = true;
      invalid();
    }
  }
}
