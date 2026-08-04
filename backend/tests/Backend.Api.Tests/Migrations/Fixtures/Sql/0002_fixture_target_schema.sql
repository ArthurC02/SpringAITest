-- P2 test-only fixture:合成的「目標 schema」。identity 欄位刻意保留,
-- 用來驗證分類會把「表所擁有的 sequence」當成擁有者的實作細節排除。
CREATE TABLE fx_widget (
    id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    name text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE fx_widget_item (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    widget_id uuid NOT NULL,
    label text NOT NULL
);
