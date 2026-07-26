#!/usr/bin/env node
import http from "node:http";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const host = process.env.CODEX_BRIDGE_HOST || "127.0.0.1";
const port = Number.parseInt(process.env.CODEX_BRIDGE_PORT || "8787", 10);
const modelName = process.env.CODEX_BRIDGE_MODEL || "codex-local";
const bridgeToken = process.env.CODEX_BRIDGE_TOKEN || "";
const codexBin = process.env.CODEX_BRIDGE_CODEX_BIN || detectDefaultCodexBinary();
const codexReasoningEffort = process.env.CODEX_BRIDGE_REASONING_EFFORT || "low";
const codexWebSearch = process.env.CODEX_BRIDGE_WEB_SEARCH || "enabled";
const codexWorkdir = process.env.CODEX_BRIDGE_WORKDIR
  || path.join(tmpdir(), "ai-arena-codex-bridge-workspace");
const maxBodyBytes = Number.parseInt(process.env.CODEX_BRIDGE_MAX_BODY_BYTES || "1048576", 10);
const requestTimeoutMs = Number.parseInt(process.env.CODEX_BRIDGE_TIMEOUT_MS || "600000", 10);

mkdirSync(codexWorkdir, { recursive: true });

function detectDefaultCodexBinary() {
  if (process.platform !== "win32") {
    return "codex";
  }

  const windowsApps = "C:\\Program Files\\WindowsApps";
  try {
    const matches = readdirSync(windowsApps)
      .filter((entry) => entry.startsWith("OpenAI.Codex_"))
      .sort()
      .reverse();
    for (const match of matches) {
      const candidate = path.join(windowsApps, match, "app", "resources", "codex.exe");
      if (existsSync(candidate)) {
        return candidate;
      }
    }
  } catch {
    // Fall back to PATH resolution below.
  }

  return "codex";
}

function sendJson(res, statusCode, value) {
  const body = JSON.stringify(value);
  res.writeHead(statusCode, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(body),
    "cache-control": "no-store",
  });
  res.end(body);
}

function sendError(res, statusCode, message) {
  sendJson(res, statusCode, {
    error: {
      message,
      type: "codex_bridge_error",
      code: String(statusCode),
    },
  });
}

function authorized(req) {
  if (!bridgeToken) {
    return true;
  }

  const auth = req.headers.authorization || "";
  return auth === `Bearer ${bridgeToken}`;
}

async function readBody(req) {
  let body = "";
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maxBodyBytes) {
      throw new Error(`Request body exceeded ${maxBodyBytes} bytes.`);
    }

    body += chunk;
  }

  return body;
}

function normalizeMessages(messages) {
  if (!Array.isArray(messages)) {
    return [];
  }

  return messages.map((message) => {
    const role = typeof message?.role === "string" ? message.role : "user";
    const content = normalizeContent(message?.content);
    return { role, content };
  });
}

function normalizeContent(content) {
  if (typeof content === "string") {
    return content;
  }

  if (Array.isArray(content)) {
    return content.map((part) => {
      if (typeof part === "string") {
        return part;
      }

      if (typeof part?.text === "string") {
        return part.text;
      }

      return JSON.stringify(part);
    }).join("\n");
  }

  if (content == null) {
    return "";
  }

  return JSON.stringify(content);
}

function buildPrompt(body) {
  const messages = normalizeMessages(body.messages);
  const systemText = messages
    .filter((message) => message.role === "system" || message.role === "developer")
    .map((message) => message.content)
    .join("\n\n");
  const conversation = messages
    .filter((message) => message.role !== "system" && message.role !== "developer")
    .map((message) => `${message.role.toUpperCase()}:\n${message.content}`)
    .join("\n\n");

  const temperature = typeof body.temperature === "number" ? body.temperature : undefined;
  const maxTokens = typeof body.max_tokens === "number" ? body.max_tokens : body.max_completion_tokens;

  return [
    "You are acting as a text-only chat completion model behind an OpenAI-compatible local bridge.",
    "Return only the assistant message content. Do not include markdown fences, tool calls, shell commands, plans, hidden reasoning, or bridge metadata.",
    "Do not inspect, edit, or execute local files or commands. You may use web search when it helps answer with recent public knowledge.",
    temperature == null ? "" : `Requested temperature: ${temperature}`,
    maxTokens == null ? "" : `Requested max output tokens: ${maxTokens}`,
    systemText ? `System/developer instructions:\n${systemText}` : "",
    conversation ? `Conversation:\n${conversation}` : "",
    "Assistant response:",
  ].filter(Boolean).join("\n\n");
}

function completionEnvelope({ id, model, text, usage }) {
  return {
    id,
    object: "chat.completion",
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [
      {
        index: 0,
        message: {
          role: "assistant",
          content: text,
        },
        finish_reason: "stop",
      },
    ],
    usage: usage || null,
  };
}

function nativeCompletionEnvelope({ id, model, text, usage }) {
  return {
    id,
    object: "response",
    model,
    output: [
      {
        type: "message",
        role: "assistant",
        content: [
          {
            type: "output_text",
            text,
          },
        ],
      },
    ],
    usage: usage
      ? {
          input_tokens: usage.prompt_tokens,
          output_tokens: usage.completion_tokens,
          total_tokens: usage.total_tokens,
        }
      : null,
  };
}

function streamChunk(res, id, model, content) {
  res.write(`data: ${JSON.stringify({
    id,
    object: "chat.completion.chunk",
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [
      {
        index: 0,
        delta: { content },
        finish_reason: null,
      },
    ],
  })}\n\n`);
}

function streamDone(res, id, model) {
  res.write(`data: ${JSON.stringify({
    id,
    object: "chat.completion.chunk",
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [
      {
        index: 0,
        delta: {},
        finish_reason: "stop",
      },
    ],
  })}\n\n`);
  res.write("data: [DONE]\n\n");
  res.end();
}

function nativeStreamChunk(res, content) {
  res.write(`data: ${JSON.stringify({
    type: "message.delta",
    content,
  })}\n\n`);
}

function nativeStreamDone(res, id, model, text, usage) {
  res.write(`data: ${JSON.stringify({
    type: "chat.end",
    result: nativeCompletionEnvelope({ id, model, text, usage }),
  })}\n\n`);
  res.end();
}

async function streamSyntheticText(res, id, model, text) {
  const pieces = text.match(/.{1,96}(\s|$)|.{1,96}/gs) || [];
  for (const piece of pieces) {
    streamChunk(res, id, model, piece);
    await new Promise((resolve) => setTimeout(resolve, 12));
  }
}

function runCodex(prompt, { onDelta, signal }) {
  return new Promise((resolve, reject) => {
    const args = [
      "exec",
      "--json",
      "--sandbox",
      "read-only",
      "--skip-git-repo-check",
      "--ignore-rules",
      "-c",
      `model_reasoning_effort="${codexReasoningEffort}"`,
      "-c",
      'model_reasoning_summary="none"',
      "-c",
      `web_search="${codexWebSearch}"`,
      "--cd",
      codexWorkdir,
      "-",
    ];
    const child = spawn(codexBin, args, {
      cwd: codexWorkdir,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
      shell: process.platform === "win32" && !codexBin.toLowerCase().endsWith(".exe"),
    });

    let stdout = "";
    let stderr = "";
    let pending = "";
    let assistantText = "";
    let streamedText = "";
    let usage = null;
    let settled = false;

    const kill = () => {
      if (!child.killed) {
        child.kill();
      }
    };

    const abort = () => {
      kill();
      reject(new Error("Request was aborted."));
    };

    if (signal?.aborted) {
      abort();
      return;
    }

    signal?.addEventListener("abort", abort, { once: true });

    child.stdin.end(prompt);

    const handleLine = (line) => {
      const trimmed = line.trim();
      if (!trimmed.startsWith("{")) {
        return;
      }

      let event;
      try {
        event = JSON.parse(trimmed);
      } catch {
        return;
      }

      const delta = extractDelta(event);
      if (delta) {
        streamedText += delta;
        assistantText += delta;
        onDelta?.(delta);
      }

      const completed = extractCompletedMessage(event);
      if (completed) {
        assistantText = completed;
      }

      if (event.type === "turn.completed" && event.usage) {
        usage = {
          prompt_tokens: event.usage.input_tokens ?? 0,
          completion_tokens: event.usage.output_tokens ?? 0,
          total_tokens: (event.usage.input_tokens ?? 0) + (event.usage.output_tokens ?? 0),
        };
      }
    };

    child.stdout.on("data", (chunk) => {
      const text = chunk.toString("utf8");
      stdout += text;
      pending += text;
      let newlineIndex;
      while ((newlineIndex = pending.indexOf("\n")) >= 0) {
        const line = pending.slice(0, newlineIndex);
        pending = pending.slice(newlineIndex + 1);
        handleLine(line);
      }
    });

    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString("utf8");
    });

    child.on("error", (error) => {
      if (settled) {
        return;
      }

      settled = true;
      signal?.removeEventListener("abort", abort);
      reject(error);
    });

    child.on("close", (code) => {
      if (settled) {
        return;
      }

      settled = true;
      signal?.removeEventListener("abort", abort);
      if (pending.trim()) {
        handleLine(pending);
      }

      if (code !== 0) {
        reject(new Error(`codex exited with code ${code}: ${stderr.trim() || stdout.trim()}`));
        return;
      }

      resolve({
        text: assistantText.trim(),
        streamed: streamedText.length > 0,
        streamedText,
        usage,
      });
    });
  });
}

function extractDelta(event) {
  if (typeof event.delta === "string") {
    return event.delta;
  }

  if (typeof event.text_delta === "string") {
    return event.text_delta;
  }

  if (event.item && typeof event.item.delta === "string") {
    return event.item.delta;
  }

  if (event.item && typeof event.item.text_delta === "string") {
    return event.item.text_delta;
  }

  if (event.type?.includes("delta") && event.item && typeof event.item.text === "string") {
    return event.item.text;
  }

  return "";
}

function extractCompletedMessage(event) {
  if (event.type !== "item.completed" || event.item?.type !== "agent_message") {
    return "";
  }

  return typeof event.item.text === "string" ? event.item.text : "";
}

function nativeBodyToOpenAiBody(body) {
  const messages = [];
  if (typeof body.system_prompt === "string" && body.system_prompt.trim()) {
    messages.push({ role: "system", content: body.system_prompt });
  }

  if (Array.isArray(body.messages)) {
    messages.push(...normalizeMessages(body.messages));
  } else if (typeof body.input === "string") {
    messages.push({ role: "user", content: body.input });
  } else if (Array.isArray(body.input)) {
    messages.push(...normalizeMessages(body.input));
  }

  return {
    ...body,
    messages,
    max_tokens: body.max_output_tokens ?? body.max_tokens,
  };
}

async function handleCompletion(req, res) {
  if (!authorized(req)) {
    sendError(res, 401, "Unauthorized.");
    return;
  }

  let body;
  try {
    body = JSON.parse(await readBody(req) || "{}");
  } catch (error) {
    sendError(res, 400, error.message);
    return;
  }

  const id = `chatcmpl-codex-${randomUUID()}`;
  const model = typeof body.model === "string" && body.model.trim() ? body.model : modelName;
  const prompt = buildPrompt(body);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), requestTimeoutMs);
  req.on("close", () => {
    if (!res.writableEnded) {
      controller.abort();
    }
  });

  if (body.stream) {
    res.writeHead(200, {
      "content-type": "text/event-stream; charset=utf-8",
      "cache-control": "no-cache, no-transform",
      "connection": "keep-alive",
      "x-accel-buffering": "no",
    });
    res.write(": codex bridge connected\n\n");

    let hadNativeDelta = false;
    try {
      const result = await runCodex(prompt, {
        signal: controller.signal,
        onDelta: (delta) => {
          hadNativeDelta = true;
          streamChunk(res, id, model, delta);
        },
      });

      if (hadNativeDelta && result.text.startsWith(result.streamedText)) {
        const remainder = result.text.slice(result.streamedText.length);
        if (remainder) {
          streamChunk(res, id, model, remainder);
        }
      } else if (!hadNativeDelta && result.text) {
        await streamSyntheticText(res, id, model, result.text);
      }

      streamDone(res, id, model);
    } catch (error) {
      streamChunk(res, id, model, `Codex bridge error: ${error.message}`);
      streamDone(res, id, model);
    } finally {
      clearTimeout(timeout);
    }

    return;
  }

  try {
    const result = await runCodex(prompt, { signal: controller.signal });
    sendJson(res, 200, completionEnvelope({ id, model, text: result.text, usage: result.usage }));
  } catch (error) {
    sendError(res, 502, error.message);
  } finally {
    clearTimeout(timeout);
  }
}

async function handleNativeCompletion(req, res) {
  if (!authorized(req)) {
    sendError(res, 401, "Unauthorized.");
    return;
  }

  let body;
  try {
    body = JSON.parse(await readBody(req) || "{}");
  } catch (error) {
    sendError(res, 400, error.message);
    return;
  }

  const id = `resp_codex_${randomUUID()}`;
  const model = typeof body.model === "string" && body.model.trim() ? body.model : modelName;
  const prompt = buildPrompt(nativeBodyToOpenAiBody(body));
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), requestTimeoutMs);
  req.on("close", () => {
    if (!res.writableEnded) {
      controller.abort();
    }
  });

  if (body.stream) {
    res.writeHead(200, {
      "content-type": "text/event-stream; charset=utf-8",
      "cache-control": "no-cache, no-transform",
      "connection": "keep-alive",
      "x-accel-buffering": "no",
    });

    let fullText = "";
    let hadNativeDelta = false;
    try {
      const result = await runCodex(prompt, {
        signal: controller.signal,
        onDelta: (delta) => {
          hadNativeDelta = true;
          fullText += delta;
          nativeStreamChunk(res, delta);
        },
      });

      if (hadNativeDelta && result.text.startsWith(result.streamedText)) {
        const remainder = result.text.slice(result.streamedText.length);
        if (remainder) {
          fullText += remainder;
          nativeStreamChunk(res, remainder);
        }
      } else if (!hadNativeDelta && result.text) {
        fullText = result.text;
        const pieces = result.text.match(/.{1,96}(\s|$)|.{1,96}/gs) || [];
        for (const piece of pieces) {
          nativeStreamChunk(res, piece);
          await new Promise((resolve) => setTimeout(resolve, 12));
        }
      }

      nativeStreamDone(res, id, model, fullText || result.text, result.usage);
    } catch (error) {
      const message = `Codex bridge error: ${error.message}`;
      nativeStreamChunk(res, message);
      nativeStreamDone(res, id, model, message, null);
    } finally {
      clearTimeout(timeout);
    }

    return;
  }

  try {
    const result = await runCodex(prompt, { signal: controller.signal });
    sendJson(res, 200, nativeCompletionEnvelope({ id, model, text: result.text, usage: result.usage }));
  } catch (error) {
    sendError(res, 502, error.message);
  } finally {
    clearTimeout(timeout);
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url || "/", `http://${req.headers.host || `${host}:${port}`}`);

  if (req.method === "GET" && (url.pathname === "/v1/models" || url.pathname === "/models" || url.pathname === "/api/v1/models")) {
    if (!authorized(req)) {
      sendError(res, 401, "Unauthorized.");
      return;
    }

    sendJson(res, 200, {
      object: "list",
      data: [
        {
          id: modelName,
          object: "model",
          created: 0,
          owned_by: "local-codex",
        },
      ],
    });
    return;
  }

  if (req.method === "GET" && url.pathname === "/health") {
    sendJson(res, 200, {
      ok: true,
      model: modelName,
      codexBin,
      codexReasoningEffort,
      codexWebSearch,
      codexWorkdir,
    });
    return;
  }

  if (req.method === "POST" && (url.pathname === "/v1/chat/completions" || url.pathname === "/chat/completions")) {
    await handleCompletion(req, res);
    return;
  }

  if (req.method === "POST" && url.pathname === "/api/v1/chat") {
    await handleNativeCompletion(req, res);
    return;
  }

  sendError(res, 404, `No route for ${req.method} ${url.pathname}`);
});

server.listen(port, host, () => {
  console.log(`Codex OpenAI bridge listening at http://${host}:${port}/v1`);
  console.log(`Model: ${modelName}`);
  console.log(`Reasoning effort: ${codexReasoningEffort}`);
  console.log(`Web search: ${codexWebSearch}`);
  console.log(`Codex workdir: ${codexWorkdir}`);
  if (bridgeToken) {
    console.log("Bridge bearer token: required");
  }
});
