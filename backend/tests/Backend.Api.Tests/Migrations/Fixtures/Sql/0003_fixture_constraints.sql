-- P2 test-only fixture:約束與索引。FK action 會進 schema fingerprint 的 constraint 類別。
ALTER TABLE fx_widget
    ADD CONSTRAINT uq_fx_widget_tenant_name UNIQUE (tenant_id, name);

ALTER TABLE fx_widget
    ADD CONSTRAINT ck_fx_widget_name CHECK (length(name) > 0);

ALTER TABLE fx_widget_item
    ADD CONSTRAINT fk_fx_widget_item_widget FOREIGN KEY (widget_id)
        REFERENCES fx_widget (id) ON DELETE CASCADE ON UPDATE RESTRICT;

CREATE INDEX ix_fx_widget_item_widget ON fx_widget_item (widget_id);
