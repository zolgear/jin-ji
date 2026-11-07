-- 0024: 組織の中の人の並び順（ADR-0013 決定 10）
--
-- 位置づけ
-- - 名前・年齢・入社順・チーム順など、現場ごとの決まりで組織の中の並びを決められるようにする。並び順は主務の所属の行（assignment）の
--   sort_order に持ち、所属と同じく有効日と記録の期間を持つ（来期の組織での並びを前もって登録でき、取り消せる）
-- - 並べていない人（NULL）は、並べた人の後ろに既定の順（フリガナ）で続く。異動すると並び順は外れる（異動先で並べ直す）
-- - 変える経路は直接登録の「並び順」（member_order）だけ。組織の範囲を持つ人が、その組織の所属者の順を 1 回の変更で決める。
--   取込（人事パッケージ）は並び順を持たない（取込で所属が変わっても、変わらない所属の並び順は残る）
--
-- 素案の中身
-- - 変更の種類に member_order を足す（target_id は組織、values の person_ids は並べた人の jin-ji ID をカンマでつないだもの）
-- - person_ids だけは 200 文字の上限を超えてよい（1 組織 500 人まで）

-- ---------------------------------------------------------------------------
-- 1. 所属の並び順
-- ---------------------------------------------------------------------------

ALTER TABLE assignment ADD COLUMN sort_order integer;
ALTER TABLE assignment ADD CONSTRAINT assignment_sort_order_check CHECK (sort_order IS NULL OR sort_order >= 0);
COMMENT ON COLUMN assignment.sort_order IS '組織の中の並び順（小さいほど先。NULL は並べていない。主務だけが持つ）';

-- ---------------------------------------------------------------------------
-- 2. 素案の中身の検査
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_plan_value_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'org_unit_id', 'position_title', 'site_id', 'remote_work', 'extension_number', 'work_mobile_phone',
        'code', 'name', 'short_name', 'level_key', 'parent_org_unit_id', 'head_person_id',
        'person_id', 'role',
        -- 0024
        'person_ids'
    ]::text[]
$$;

CREATE OR REPLACE FUNCTION jinji_plan_changes_allowed(changes jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(changes) = 'array'
       AND jsonb_array_length(changes) <= 500
       AND NOT EXISTS (
           SELECT 1 FROM jsonb_array_elements(changes) AS c (item)
           WHERE jsonb_typeof(c.item) <> 'object'
              OR EXISTS (SELECT 1 FROM jsonb_object_keys(c.item) AS k (key) WHERE k.key NOT IN ('kind', 'target_id', 'values'))
              OR coalesce(c.item ->> 'kind', '') NOT IN
                  ('assignment', 'person', 'org_update', 'org_create', 'org_abolish', 'member_order', 'group_join', 'group_leave')
              OR (c.item ? 'target_id' AND jsonb_typeof(c.item -> 'target_id') NOT IN ('string', 'null'))
              OR (c.item ? 'values' AND (
                      jsonb_typeof(c.item -> 'values') <> 'object'
                      OR EXISTS (
                          SELECT 1 FROM jsonb_each(c.item -> 'values') AS v (key, value)
                          WHERE v.key <> ALL (public.jinji_plan_value_keys())
                             OR jsonb_typeof(v.value) NOT IN ('string', 'null')
                             -- 並び順の人の一覧は 500 人分（36 文字の ID とカンマ）まで
                             OR char_length(coalesce(v.value #>> '{}', '')) > CASE WHEN v.key = 'person_ids' THEN 18500 ELSE 200 END))))
$$;
