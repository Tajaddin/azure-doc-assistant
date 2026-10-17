-- Applied on start-up by Db.MigrateAsync. Idempotent.
create extension if not exists vector;

create table if not exists documents (
    id            uuid primary key,
    owner_id      text        not null,
    file_name     text        not null,
    blob_name     text        not null,
    size_bytes    bigint      not null,
    kind          text        not null default 'Unknown',
    status        text        not null default 'Uploaded',
    page_count    int         not null default 0,
    chunk_count   int         not null default 0,
    uploaded_at   timestamptz not null default now(),
    indexed_at    timestamptz,
    error         text
);
create index if not exists documents_owner_idx on documents (owner_id, uploaded_at desc);

create table if not exists pages (
    document_id uuid not null references documents (id) on delete cascade,
    number      int  not null,
    text        text not null,
    primary key (document_id, number)
);

-- The embedding dimension is fixed per deployment (nomic-embed-text 768, text-embedding-3-small 1536).
-- Db.MigrateAsync creates the chunks table with the configured dimension and refuses to start on a mismatch.
create table if not exists chunks (
    id          uuid primary key,
    document_id uuid not null references documents (id) on delete cascade,
    owner_id    text not null,
    page        int  not null,
    ordinal     int  not null,
    text        text not null,
    tsv         tsvector generated always as (to_tsvector('english', text)) stored,
    embedding   vector(__DIM__) not null
);
create index if not exists chunks_owner_doc_idx on chunks (owner_id, document_id);
create index if not exists chunks_tsv_idx on chunks using gin (tsv);
create index if not exists chunks_embedding_idx on chunks using hnsw (embedding vector_cosine_ops);

create table if not exists invoice_fields (
    document_id  uuid primary key references documents (id) on delete cascade,
    fields       jsonb not null,
    method       text  not null,
    extracted_at timestamptz not null default now()
);

create table if not exists cost_entries (
    id            uuid primary key,
    owner_id      text        not null,
    operation     text        not null,
    model         text        not null,
    input_tokens  int         not null,
    output_tokens int         not null,
    cost_usd      numeric(12, 6) not null,
    at            timestamptz not null default now()
);
create index if not exists cost_entries_owner_at_idx on cost_entries (owner_id, at desc);
