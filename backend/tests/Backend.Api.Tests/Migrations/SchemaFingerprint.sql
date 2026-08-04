-- =============================================================================
-- Schema fingerprint —— 版本化的驗收資產(03-design §1.3 末段)。
--
-- 這支腳本「本身」就是 fingerprint 的權威定義:改它等於改驗收標準,必須被 review。
-- 產出是排序後的 catalog projection,四欄 (object_schema, object_type, object_name, detail),
-- 排序鍵 (schema, object_type, name) 外加 detail 作為決定性 tiebreaker,全部走 C-locale byte order。
--
-- 涵蓋:schema、extension(名稱+版本+安裝 schema)、table(含 partitioning)、
--       column(name / logical type / nullability / identity / generation / collation / normalized default)、
--       constraint(含 FK action)、index、view + materialized view 定義、sequence(定義與 ownership)、
--       function/procedure 簽名與定義、trigger、custom type、domain、enum、RLS 狀態與 policy。
--
-- 刻意排除(且僅排除)這幾類:catalog OID、owner、ACL 順序、實體 row/index 排列順序、
--       統計資訊、以及 migration/audit 時間戳(fingerprint 只看 catalog,不看資料列)。
--       欄位的 attnum 屬於「實體順序」,故不入 detail。
--
-- Extension-owned 物件永不逐一列舉:被 pg_depend deptype='e' 指向某 extension 的 catalog 物件
-- (pgvector 的型別、運算子、operator class、cast、函式都因為 CREATE EXTENSION 沒帶 SCHEMA 子句
-- 而落在 public)只由該 extension 那一列的「名稱+版本+安裝 schema」代表。
-- =============================================================================
WITH ns AS (
    SELECT n.oid, n.nspname
    FROM pg_namespace n
    WHERE n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
      AND n.nspname NOT LIKE 'pg\_temp%'
      AND n.nspname NOT LIKE 'pg\_toast%'
),
ext_owned AS (
    SELECT d.classid, d.objid
    FROM pg_depend d
    WHERE d.deptype = 'e'
),
rel AS (
    SELECT c.oid, c.relname, c.relkind, c.relpersistence, c.relrowsecurity, c.relforcerowsecurity, ns.nspname
    FROM pg_class c
    JOIN ns ON ns.oid = c.relnamespace
    WHERE NOT EXISTS (
        SELECT 1 FROM ext_owned e
        WHERE e.classid = 'pg_class'::regclass AND e.objid = c.oid)
)
SELECT * FROM (
    -- schema
    SELECT ns.nspname AS object_schema, 'schema' AS object_type, ns.nspname AS object_name, '' AS detail
    FROM ns

    UNION ALL
    -- extension:名稱 + 版本 + 安裝 schema,其擁有的物件不再逐一列舉
    SELECT n.nspname, 'extension', e.extname, 'version=' || e.extversion
    FROM pg_extension e
    JOIN pg_namespace n ON n.oid = e.extnamespace

    UNION ALL
    -- table(含 partitioned table 的 partition key)
    SELECT rel.nspname, 'table', rel.relname,
           'kind=' || rel.relkind::text
           || ' persistence=' || rel.relpersistence::text
           || ' partitionkey=' || COALESCE(pg_get_partkeydef(rel.oid), '')
           || ' partitionbound=' || COALESCE(pg_get_expr(c.relpartbound, c.oid), '')
    FROM rel
    JOIN pg_class c ON c.oid = rel.oid
    WHERE rel.relkind IN ('r', 'p', 'f')

    UNION ALL
    -- column:邏輯型別、nullability、identity/generation、collation、正規化 default
    SELECT rel.nspname, 'column', rel.relname || '.' || a.attname,
           'type=' || format_type(a.atttypid, a.atttypmod)
           || ' notnull=' || a.attnotnull::text
           || ' identity=' || COALESCE(NULLIF(a.attidentity::text, ''), '-')
           || ' generated=' || COALESCE(NULLIF(a.attgenerated::text, ''), '-')
           || ' collation=' || COALESCE(co.collname, '-')
           || ' default=' || COALESCE(pg_get_expr(ad.adbin, ad.adrelid), '-')
    FROM rel
    JOIN pg_attribute a ON a.attrelid = rel.oid
    LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
    LEFT JOIN pg_collation co ON co.oid = a.attcollation
    WHERE a.attnum > 0
      AND NOT a.attisdropped
      AND rel.relkind IN ('r', 'p', 'f', 'v', 'm')

    UNION ALL
    -- constraint:pg_get_constraintdef 已含 FK 的 ON UPDATE/ON DELETE action
    SELECT rel.nspname, 'constraint', rel.relname || '.' || con.conname,
           pg_get_constraintdef(con.oid)
           || ' deferrable=' || con.condeferrable::text
           || ' deferred=' || con.condeferred::text
    FROM rel
    JOIN pg_constraint con ON con.conrelid = rel.oid

    UNION ALL
    -- index
    SELECT rel.nspname, 'index', rel.relname || '.' || ic.relname, pg_get_indexdef(i.indexrelid)
    FROM rel
    JOIN pg_index i ON i.indrelid = rel.oid
    JOIN pg_class ic ON ic.oid = i.indexrelid

    UNION ALL
    -- view / materialized view 的正規化定義
    SELECT rel.nspname, CASE rel.relkind WHEN 'v' THEN 'view' ELSE 'matview' END, rel.relname,
           pg_get_viewdef(rel.oid, true)
    FROM rel
    WHERE rel.relkind IN ('v', 'm')

    UNION ALL
    -- sequence:定義選項與 ownership(last_value 是資料,不是 schema,故排除)
    SELECT rel.nspname, 'sequence', rel.relname,
           'type=' || format_type(s.seqtypid, NULL)
           || ' start=' || s.seqstart::text
           || ' increment=' || s.seqincrement::text
           || ' min=' || s.seqmin::text
           || ' max=' || s.seqmax::text
           || ' cache=' || s.seqcache::text
           || ' cycle=' || s.seqcycle::text
           || ' ownedby=' || COALESCE(
                (SELECT oc.relname || '.' || oa.attname
                 FROM pg_depend od
                 JOIN pg_class oc ON oc.oid = od.refobjid
                 JOIN pg_attribute oa ON oa.attrelid = od.refobjid AND oa.attnum = od.refobjsubid
                 WHERE od.classid = 'pg_class'::regclass AND od.objid = rel.oid AND od.deptype = 'a'), '-')
    FROM rel
    JOIN pg_sequence s ON s.seqrelid = rel.oid
    WHERE rel.relkind = 'S'

    UNION ALL
    -- function / procedure:簽名與定義
    SELECT ns.nspname, 'function', p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
           'kind=' || p.prokind::text
           || ' returns=' || pg_get_function_result(p.oid)
           || ' volatility=' || p.provolatile::text
           || ' strict=' || p.proisstrict::text
           || ' security_definer=' || p.prosecdef::text
           || ' language=' || l.lanname
           || ' body=' || COALESCE(p.prosrc, '')
    FROM pg_proc p
    JOIN ns ON ns.oid = p.pronamespace
    JOIN pg_language l ON l.oid = p.prolang
    WHERE NOT EXISTS (
        SELECT 1 FROM ext_owned e WHERE e.classid = 'pg_proc'::regclass AND e.objid = p.oid)

    UNION ALL
    -- trigger(不含 catalog 內部 FK 觸發器)
    SELECT rel.nspname, 'trigger', rel.relname || '.' || t.tgname, pg_get_triggerdef(t.oid, true)
    FROM rel
    JOIN pg_trigger t ON t.tgrelid = rel.oid
    WHERE NOT t.tgisinternal

    UNION ALL
    -- custom type / domain / enum
    SELECT ns.nspname,
           CASE t.typtype WHEN 'd' THEN 'domain' WHEN 'e' THEN 'enum' ELSE 'type' END,
           t.typname,
           'typtype=' || t.typtype::text
           || ' category=' || t.typcategory::text
           || ' notnull=' || t.typnotnull::text
           || ' basetype=' || COALESCE(format_type(NULLIF(t.typbasetype, 0), t.typtypmod), '-')
           || ' default=' || COALESCE(t.typdefault, '-')
           || ' labels=' || COALESCE(
                (SELECT string_agg(en.enumlabel, ',' ORDER BY en.enumsortorder)
                 FROM pg_enum en WHERE en.enumtypid = t.oid), '-')
           || ' constraints=' || COALESCE(
                (SELECT string_agg(pg_get_constraintdef(dc.oid), ' ' ORDER BY dc.conname)
                 FROM pg_constraint dc WHERE dc.contypid = t.oid), '-')
    FROM pg_type t
    JOIN ns ON ns.oid = t.typnamespace
    WHERE t.typtype IN ('d', 'e', 'c', 'r')
      -- 表/view 自動產生的 composite type 由該 relation 自己表示
      AND NOT EXISTS (SELECT 1 FROM pg_class rc WHERE rc.oid = t.typrelid AND rc.relkind <> 'c')
      AND NOT EXISTS (
          SELECT 1 FROM ext_owned e WHERE e.classid = 'pg_type'::regclass AND e.objid = t.oid)

    UNION ALL
    -- row level security 狀態
    SELECT rel.nspname, 'rls', rel.relname,
           'enabled=' || rel.relrowsecurity::text || ' forced=' || rel.relforcerowsecurity::text
    FROM rel
    WHERE rel.relkind IN ('r', 'p')

    UNION ALL
    -- row level security policy
    SELECT rel.nspname, 'policy', rel.relname || '.' || pol.polname,
           'command=' || pol.polcmd::text
           || ' permissive=' || pol.polpermissive::text
           || ' using=' || COALESCE(pg_get_expr(pol.polqual, pol.polrelid), '-')
           || ' check=' || COALESCE(pg_get_expr(pol.polwithcheck, pol.polrelid), '-')
    FROM rel
    JOIN pg_policy pol ON pol.polrelid = rel.oid
) t
ORDER BY t.object_schema COLLATE "C",
         t.object_type COLLATE "C",
         t.object_name COLLATE "C",
         t.detail COLLATE "C";
