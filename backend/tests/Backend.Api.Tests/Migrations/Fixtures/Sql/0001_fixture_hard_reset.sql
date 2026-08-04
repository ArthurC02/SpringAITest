-- P2 test-only fixture migration。**不是**生產 0001_architecture_hard_reset.sql:
-- 生產 bundle 在 P3 才與其消費端一起出貨(01-plan §5、02-spec §6.1)。
-- 這裡只證明 runner 機制:空庫分支、legacy 清理分支、稽核只存計數。
--
-- 空庫時整個迴圈是無動作;legacy 時逐表記下清理前列數後 drop。
-- 稽核列的 execution UUID 由 runner 以 GUC 傳入,SQL 原文永不被替換。
DO $$
DECLARE
    legacy_table text;
    row_count bigint;
BEGIN
    FOREACH legacy_table IN ARRAY ARRAY['fx_legacy_a', 'fx_legacy_b'] LOOP
        IF to_regclass('public.' || quote_ident(legacy_table)) IS NOT NULL THEN
            EXECUTE format('SELECT count(*) FROM public.%I', legacy_table) INTO row_count;
            INSERT INTO springaitest_meta.migration_cleanup_audit
                (execution_id, migration_version, table_name, deleted_row_count)
            VALUES (
                current_setting('springaitest.migration_execution_id')::uuid,
                1,
                legacy_table,
                row_count);
            EXECUTE format('DROP TABLE public.%I CASCADE', legacy_table);
        END IF;
    END LOOP;
END $$;
