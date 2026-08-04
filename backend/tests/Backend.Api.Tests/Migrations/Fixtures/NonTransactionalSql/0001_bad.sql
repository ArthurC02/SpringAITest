-- 交易外操作:整批回滾的保證會靜默消失,manifest 必須在建構期就拒絕。
CREATE INDEX CONCURRENTLY ix_fx_bad ON fx_widget (tenant_id);
