SELECT to_regclass('public.fx_widget') IS NOT NULL
   AND to_regclass('public.fx_widget_item') IS NOT NULL
   AND to_regclass('public.fx_legacy_a') IS NULL;
