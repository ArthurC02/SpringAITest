-- bundle 之後的普通 migration,刻意在執行期失敗(主鍵重複)。
-- 它自己那一個交易必須回滾,先前已寫入的 bundle 不受影響。
INSERT INTO fx_widget_item (id, widget_id) VALUES (1, 1), (1, 1);
