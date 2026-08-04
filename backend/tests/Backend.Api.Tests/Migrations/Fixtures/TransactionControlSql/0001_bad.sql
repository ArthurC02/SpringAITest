-- 裸交易控制語句:在 runner 的交易邊界內另開邊界,「整批回滾」會靜默失效,
-- manifest 必須在建構期就拒絕。
CREATE TABLE fx_widget (id integer PRIMARY KEY);
COMMIT;
