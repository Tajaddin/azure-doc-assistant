# 0004. A synthetic corpus whose facts are known by construction, scored deterministically

Date: 2026-10-17. Status: accepted.

## Context

Real invoices and contracts contain personal and commercial data that cannot be committed. Public
document-QA datasets are either scanned (needing OCR the local analyzer does not do) or unlicensed for
redistribution. An eval still needs ground truth.

## Decision

`tools/DocAssistant.SynthData` generates 20 invoices, 5 master services agreements and 3 product manuals
(31 pages) from a seeded random generator and renders them with QuestPDF; every amount, date, party, term
and specification is known. The same generator emits `evals/questions.json`: 44 answerable questions over
those facts (totals, vendors, due dates, tax, customers, termination notice, monthly fee, governing law,
SLA, power, temperature range, error codes, filter interval) and 2 unanswerable ones. `truth.json` holds
the invoice fields for the extraction score.

`tools/DocAssistant.Evals` runs against a live API: it uploads the corpus for a fresh user, times
ingestion to pages per minute, asks each question, and scores:

- **answer accuracy**: expected numbers within 0.5% (or 0.01), dates in any common rendering, otherwise a
  normalised substring; unanswerable questions pass when the answer declines;
- **citation rate**: at least one citation points at the document the fact came from;
- **extraction field accuracy**: eight fields per invoice compared with the truth.

Nothing asks a model to grade a model. Results are written per model label.

## Consequences

- The corpus is small and clean (born-digital, one layout family per type), so the numbers are an upper
  bound on messy real documents, and the README says so.
- Because the questions mention identifiers ("invoice INV-1007"), keyword retrieval alone finds the
  right document most of the time; the hybrid index's vector half matters more for paraphrased questions
  than this set exercises.
- Extending the set is editing the generator, which keeps truth and PDFs in step; hand-written questions
  can be added to the JSON without touching the generator.
