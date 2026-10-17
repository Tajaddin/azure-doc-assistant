# 0002. Semantic Kernel with a manual, bounded function-calling loop and numbered-source citations

Date: 2026-10-17. Status: accepted.

## Context

The assistant must answer from the user's documents with citations and be able to call functions (the
posting's "RAG or function calling to connect LLMs to custom data sources"). Semantic Kernel can invoke
functions automatically (`FunctionChoiceBehavior.Auto()`), which hides the loop inside the connector: the
number of model calls, their token usage and any runaway tool chatter are invisible to the caller.

## Decision

`AskService` uses Semantic Kernel for what it is good at (plugins from annotated methods, structured
chat history, provider-neutral chat and embedding services, streaming) and runs the tool loop itself with
`FunctionChoiceBehavior.Auto(autoInvoke: false)`:

1. Hybrid retrieval puts the top 8 passages in the system prompt as numbered sources `[n]`.
2. Each round streams the model's reply; `FunctionCallContentBuilder` assembles any tool calls.
3. Tool calls are executed through the kernel (`search_documents`, `lookup_invoice`, `list_documents`),
   their results appended to the history, and the source list extended so new passages are citable.
4. At most four rounds; the answer's `[n]` markers are parsed into `Citation` objects pointing at document,
   page and chunk; usage is read from every round's metadata and priced, or estimated and flagged when a
   provider reports none.

Plugins are instantiated per request with the caller's owner id, so a tool can never reach another
user's documents. The kernel is cloned per request to hold them.

## Consequences

- Every answer carries its own model-call count, tokens, cost, tool list and citations; each round is a
  span. Cost per answer is a number, not a guess, when the provider reports usage.
- The loop is capped, so a model that keeps calling tools returns a bounded, labelled answer
  (`tool_limit`) instead of spinning.
- Citation correctness is measurable: the eval scores whether the cited document is the one the fact came
  from.
- Streaming and tool calling together cost more code than auto-invoke (about 80 lines). Structured
  outputs for extraction use `ResponseFormat = "json_object"`, which both Azure OpenAI and Ollama's
  OpenAI-compatible endpoint accept; typed JSON schema response formats are OpenAI-only and were avoided
  so the same code path runs locally.
